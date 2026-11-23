using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using DeezNutzDownloader.Core.Downloads;
using Microsoft.Win32.SafeHandles;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Downloads one file over several HTTP range connections at once,
/// writing each byte straight to its final offset.
///
/// Correctness first: every range request carries If-Range (strong
/// ETag or Last-Modified) and every 206 must state the expected
/// offset and total length, so a file that changes mid-download is
/// never spliced from two versions. Any inconsistency makes the
/// whole attempt return false and the caller restarts it as a
/// plain single stream.
///
/// Speed: when a connection finishes its segment it takes over an
/// orphaned range or splits the largest remaining one in half, so
/// one slow connection cannot hold up the finish. Connections the
/// server refuses (429/503/403) simply retire; the others absorb
/// their ranges.
/// </summary>
internal sealed class SegmentedDownloader
{
    /// <summary>Files smaller than this always use one stream.</summary>
    internal const long MinimumFileBytes = 4L * 1024 * 1024;

    /// <summary>Each planned segment is at least this large.</summary>
    internal const long MinimumSegmentBytes = 1L * 1024 * 1024;

    /// <summary>A range is only split when both halves get this much.</summary>
    internal const long MinimumSplitBytes = 512L * 1024;

    internal const int MaximumConnections = 16;

    private const int BufferSize = 81_920;
    private const int MaximumAttemptsWithoutProgress = 4;
    private const long ProgressReportIntervalMilliseconds = 100;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _stallTimeout;

    public SegmentedDownloader(
        HttpClient httpClient,
        TimeSpan stallTimeout)
    {
        _httpClient = httpClient;
        _stallTimeout = stallTimeout;
    }

    public static int PlanConnectionCount(
        long totalBytes,
        int requestedConnections)
    {
        if (
            requestedConnections <= 1 ||
            totalBytes < MinimumFileBytes)
        {
            return 1;
        }

        long bySize = totalBytes / MinimumSegmentBytes;

        return (int)Math.Clamp(
            bySize,
            1,
            Math.Min(requestedConnections, MaximumConnections));
    }

    /// <summary>
    /// Whether the first (plain GET) response allows segmenting.
    /// Compressed bodies are excluded: ranges would address the
    /// encoded bytes, which is consistent but pointless to split.
    /// </summary>
    public static bool SupportsRanges(HttpResponseMessage response)
    {
        return
            response.StatusCode == HttpStatusCode.OK &&
            response.Headers.AcceptRanges.Contains(
                "bytes",
                StringComparer.OrdinalIgnoreCase) &&
            response.Content.Headers.ContentEncoding.Count == 0;
    }

    /// <summary>
    /// Strong ETag first (weak ones are not allowed in If-Range),
    /// then Last-Modified. Null when the server gives neither; the
    /// Content-Range total check still guards each segment.
    /// </summary>
    public static RangeConditionHeaderValue? BuildValidator(
        HttpResponseMessage response)
    {
        EntityTagHeaderValue? etag = response.Headers.ETag;

        if (etag is { IsWeak: false })
        {
            return new RangeConditionHeaderValue(etag);
        }

        DateTimeOffset? lastModified =
            response.Content.Headers.LastModified;

        return lastModified.HasValue
            ? new RangeConditionHeaderValue(lastModified.Value)
            : null;
    }

    /// <returns>
    /// True when the file at <paramref name="path"/> holds all
    /// <paramref name="totalBytes"/>. False when the server did not
    /// honor ranges consistently; the file contents are then
    /// undefined and the caller must restart without segments.
    /// </returns>
    public async Task<bool> DownloadAsync(
        Uri source,
        string path,
        long totalBytes,
        int connections,
        RangeConditionHeaderValue? validator,
        IProgress<DownloadTransferProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<PartialSegment>? resumeSegments = null,
        Action<IReadOnlyList<PartialSegment>>? persist = null)
    {
        // Reserve the whole file up front: a full disk fails now,
        // not after minutes of downloading. On resume the file is
        // already this size and its saved ranges stay intact.
        using (
            var reserve = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None))
        {
            reserve.SetLength(totalBytes);
        }

        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous);

        using var abort =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        var run = new Run(
            source,
            handle,
            totalBytes,
            validator,
            progress,
            abort);

        List<Segment> initial =
            BuildInitialSegments(totalBytes, connections, resumeSegments);

        Segment[] owned;

        lock (run.Sync)
        {
            run.Segments.AddRange(initial);

            // Unfinished ranges beyond the worker count start as
            // orphans; finishing workers claim them.
            owned = initial
                .Where(segment => !segment.Done)
                .Take(connections)
                .ToArray();

            foreach (Segment segment in owned)
            {
                segment.Owned = true;
            }

            run.ActiveWorkers = owned.Length;
        }

        run.InitializeBytes(
            initial.Sum(segment => segment.Confirmed - segment.Start));

        using var saverStop = new CancellationTokenSource();

        Task saver = persist is null
            ? Task.CompletedTask
            : PersistLoopAsync(run, persist, saverStop.Token);

        try
        {
            Task[] workers = owned
                .Select(segment => WorkerAsync(run, segment))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            saverStop.Cancel();
            await saver.ConfigureAwait(false);

            // Final map: what a cancel, pause or failure leaves on
            // disk is exactly what the next attempt resumes from.
            persist?.Invoke(run.Snapshot());
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (run.Failure is not null)
        {
            if (run.Failure is RangeNotHonoredException)
            {
                return false;
            }

            ExceptionDispatchInfo.Capture(run.Failure).Throw();
        }

        lock (run.Sync)
        {
            if (run.Segments.Any(segment => segment.Confirmed < segment.End))
            {
                throw new IOException(
                    $"The segmented download from {source.Host} ended " +
                    "with ranges still missing.");
            }
        }

        run.ReportProgress(force: true);
        return true;
    }

    private static List<Segment> BuildInitialSegments(
        long totalBytes,
        int connections,
        IReadOnlyList<PartialSegment>? resumeSegments)
    {
        if (resumeSegments is { Count: > 0 })
        {
            return resumeSegments
                .Select(saved =>
                {
                    long start = Math.Clamp(saved.Start, 0, totalBytes);
                    long end = Math.Clamp(saved.End, start, totalBytes);
                    long confirmed = Math.Clamp(saved.Confirmed, start, end);

                    return new Segment(start, end)
                    {
                        Position = confirmed,
                        Confirmed = confirmed
                    };
                })
                .ToList();
        }

        long segmentLength = totalBytes / connections;
        var segments = new List<Segment>(connections);

        for (int index = 0; index < connections; index++)
        {
            long start = index * segmentLength;
            long end = index == connections - 1
                ? totalBytes
                : start + segmentLength;

            segments.Add(new Segment(start, end));
        }

        return segments;
    }

    private static async Task PersistLoopAsync(
        Run run,
        Action<IReadOnlyList<PartialSegment>> persist,
        CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                persist(run.Snapshot());
            }
        }
        catch (OperationCanceledException)
        {
            // The download finished or stopped; the caller saves last.
        }
    }

    private async Task WorkerAsync(Run run, Segment first)
    {
        Segment? segment = first;

        // Leaving the pool (retiring, or finding no work) happens
        // atomically with the ActiveWorkers decrement, so no worker
        // can retire on the belief that one which is already
        // leaving will pick up its range.
        bool exited = false;

        try
        {
            while (segment is not null)
            {
                bool retired = await RunSegmentAsync(run, segment)
                    .ConfigureAwait(false);

                if (retired)
                {
                    exited = true;
                    return;
                }

                segment = ClaimNext(run);
            }

            exited = true;
        }
        catch (OperationCanceledException)
            when (run.Abort.IsCancellationRequested)
        {
            // Another worker failed or the user canceled.
        }
        catch (Exception exception)
        {
            run.Fail(exception);
        }
        finally
        {
            if (!exited)
            {
                lock (run.Sync)
                {
                    run.ActiveWorkers--;
                }
            }
        }
    }

    private static Segment? ClaimNext(Run run)
    {
        lock (run.Sync)
        {
            // Ranges left behind by a retired connection come first.
            Segment? orphan = run.Segments.FirstOrDefault(
                segment => !segment.Owned && !segment.Done);

            if (orphan is not null)
            {
                orphan.Owned = true;
                return orphan;
            }

            Segment? largest = run.Segments
                .Where(segment => segment.Owned && !segment.Done)
                .MaxBy(segment => segment.Remaining);

            if (
                largest is null ||
                largest.Remaining < 2 * MinimumSplitBytes)
            {
                run.ActiveWorkers--;
                return null;
            }

            // The owner keeps the front half and stops at the new
            // End; this worker fetches the back half.
            long middle = largest.Position + largest.Remaining / 2;

            var stolen = new Segment(middle, largest.End)
            {
                Owned = true
            };

            largest.End = middle;
            run.Segments.Add(stolen);
            return stolen;
        }
    }

    /// <returns>
    /// True when the server refused this connection and the worker
    /// retired, leaving its range for the others.
    /// </returns>
    private async Task<bool> RunSegmentAsync(Run run, Segment segment)
    {
        CancellationToken token = run.Abort.Token;
        int attemptsWithoutProgress = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                long start;

                lock (run.Sync)
                {
                    if (segment.Done)
                    {
                        return false;
                    }

                    start = segment.Position;
                }

                long startPosition = start;

                try
                {
                    using var request =
                        new HttpRequestMessage(HttpMethod.Get, run.Source);

                    // Open-ended: the segment's End can shrink while
                    // it downloads (another worker split it).
                    request.Headers.Range =
                        new RangeHeaderValue(start, null);

                    if (run.Validator is not null)
                    {
                        request.Headers.IfRange = run.Validator;
                    }

                    using HttpResponseMessage response =
                        await _httpClient.SendAsync(
                                request,
                                HttpCompletionOption.ResponseHeadersRead,
                                token)
                            .ConfigureAwait(false);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // Range ignored, or If-Range saw a changed
                            // file: splicing would corrupt it.
                            throw new RangeNotHonoredException();
                        }

                        // Host refused an extra connection (429, 503,
                        // 403...): bow out if others can carry on.
                        if (TryRetire(run, segment))
                        {
                            return true;
                        }

                        response.EnsureSuccessStatusCode();

                        // 2xx other than 200/206: not a range answer.
                        throw new RangeNotHonoredException();
                    }

                    ContentRangeHeaderValue? range =
                        response.Content.Headers.ContentRange;

                    if (
                        range is null ||
                        range.From != start ||
                        range.Length != run.TotalBytes)
                    {
                        throw new RangeNotHonoredException();
                    }

                    await CopySegmentAsync(
                            run,
                            segment,
                            response,
                            buffer,
                            token)
                        .ConfigureAwait(false);

                    lock (run.Sync)
                    {
                        if (segment.Done)
                        {
                            return false;
                        }
                    }

                    throw new IOException(
                        $"{run.Source.Host} closed a range early.");
                }
                catch (Exception exception)
                    when (
                        exception is HttpRequestException or IOException &&
                        !token.IsCancellationRequested)
                {
                    long now;

                    lock (run.Sync)
                    {
                        now = segment.Position;
                    }

                    attemptsWithoutProgress =
                        now > startPosition
                            ? 1
                            : attemptsWithoutProgress + 1;

                    if (attemptsWithoutProgress >= MaximumAttemptsWithoutProgress)
                    {
                        // Let healthy connections finish this range
                        // rather than failing the whole file.
                        if (TryRetire(run, segment))
                        {
                            return true;
                        }

                        throw;
                    }

                    TimeSpan? wait =
                        HostThrottlePolicy.ComputeWaitBeforeRetry(
                            attemptsWithoutProgress,
                            retryAfter: null);

                    if (wait.HasValue)
                    {
                        await Task.Delay(wait.Value, token)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CopySegmentAsync(
        Run run,
        Segment segment,
        HttpResponseMessage response,
        byte[] buffer,
        CancellationToken token)
    {
        await using Stream body =
            await response.Content.ReadAsStreamAsync(token)
                .ConfigureAwait(false);

        using var stall =
            CancellationTokenSource.CreateLinkedTokenSource(token);

        while (true)
        {
            int read;
            stall.CancelAfter(_stallTimeout);

            try
            {
                read = await body.ReadAsync(
                        buffer.AsMemory(0, BufferSize),
                        stall.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!token.IsCancellationRequested)
            {
                throw new IOException(
                    $"No data from {run.Source.Host} for " +
                    $"{_stallTimeout.TotalSeconds:0} seconds; reconnecting.",
                    exception);
            }

            if (read == 0)
            {
                return;
            }

            long offset;
            int allowed;

            // Reserve the bytes before writing so a concurrent split
            // never hands the same offsets to a second worker.
            lock (run.Sync)
            {
                allowed = (int)Math.Min(read, segment.End - segment.Position);

                if (allowed <= 0)
                {
                    return;
                }

                offset = segment.Position;
                segment.Position += allowed;
            }

            try
            {
                await RandomAccess.WriteAsync(
                        run.Handle,
                        buffer.AsMemory(0, allowed),
                        offset,
                        token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                // Disk errors (full, removed) are not network blips:
                // retrying would leave a hole at the reserved range.
                throw new SegmentWriteException(exception);
            }

            // Only bytes that actually reached the file count as
            // saved for resume; reservations alone never do.
            lock (run.Sync)
            {
                segment.Confirmed = offset + allowed;
            }

            run.AddBytes(allowed);

            if (allowed < read)
            {
                return;
            }
        }
    }

    private static bool TryRetire(Run run, Segment segment)
    {
        lock (run.Sync)
        {
            // The last connection never retires: someone must finish.
            if (run.ActiveWorkers <= 1)
            {
                return false;
            }

            segment.Owned = false;
            run.ActiveWorkers--;
            return true;
        }
    }

    private sealed class Segment
    {
        public Segment(long start, long end)
        {
            Start = start;
            Position = start;
            Confirmed = start;
            End = end;
        }

        public long Start { get; }

        /// <summary>Next offset to hand out (reserved, maybe not yet written).</summary>
        public long Position { get; set; }

        /// <summary>Everything before this is on disk.</summary>
        public long Confirmed { get; set; }

        public long End { get; set; }

        public bool Owned { get; set; }

        public long Remaining => Math.Max(0, End - Position);

        public bool Done => Position >= End;
    }

    private sealed class Run
    {
        private readonly IProgress<DownloadTransferProgress>? _progress;
        private readonly object _progressSync = new();
        private long _bytesWritten;
        private long _lastReportTicks = long.MinValue;
        private Exception? _failure;

        public Run(
            Uri source,
            SafeFileHandle handle,
            long totalBytes,
            RangeConditionHeaderValue? validator,
            IProgress<DownloadTransferProgress>? progress,
            CancellationTokenSource abort)
        {
            Source = source;
            Handle = handle;
            TotalBytes = totalBytes;
            Validator = validator;
            _progress = progress;
            Abort = abort;
        }

        public object Sync { get; } = new();

        // Guarded by Sync.
        public List<Segment> Segments { get; } = new();

        // Guarded by Sync.
        public int ActiveWorkers { get; set; }

        public Uri Source { get; }

        public SafeFileHandle Handle { get; }

        public long TotalBytes { get; }

        public RangeConditionHeaderValue? Validator { get; }

        public CancellationTokenSource Abort { get; }

        public Exception? Failure => Volatile.Read(ref _failure);

        public void Fail(Exception exception)
        {
            if (exception is SegmentWriteException write)
            {
                exception = write.InnerException!;
            }

            if (Interlocked.CompareExchange(ref _failure, exception, null) is null)
            {
                try
                {
                    Abort.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void InitializeBytes(long alreadySaved)
        {
            Interlocked.Exchange(ref _bytesWritten, alreadySaved);
        }

        public IReadOnlyList<PartialSegment> Snapshot()
        {
            lock (Sync)
            {
                return Segments
                    .Select(segment =>
                        new PartialSegment(
                            segment.Start,
                            segment.Confirmed,
                            segment.End))
                    .ToArray();
            }
        }

        public void AddBytes(long count)
        {
            Interlocked.Add(ref _bytesWritten, count);
            ReportProgress(force: false);
        }

        public void ReportProgress(bool force)
        {
            if (_progress is null)
            {
                return;
            }

            // Serialized: progress sinks are not required to be
            // thread-safe, and reports must never go backwards.
            lock (_progressSync)
            {
                long now = Environment.TickCount64;

                if (
                    !force &&
                    _lastReportTicks != long.MinValue &&
                    now - _lastReportTicks < ProgressReportIntervalMilliseconds)
                {
                    return;
                }

                _lastReportTicks = now;

                _progress.Report(
                    new DownloadTransferProgress(
                        Interlocked.Read(ref _bytesWritten),
                        TotalBytes));
            }
        }
    }

    private sealed class RangeNotHonoredException : Exception
    {
        public RangeNotHonoredException()
            : base("The server did not honor byte ranges consistently.")
        {
        }
    }

    private sealed class SegmentWriteException : Exception
    {
        public SegmentWriteException(Exception inner)
            : base(inner.Message, inner)
        {
        }
    }
}
