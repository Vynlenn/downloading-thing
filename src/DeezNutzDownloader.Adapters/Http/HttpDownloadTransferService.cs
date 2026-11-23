using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

public sealed class HttpDownloadTransferService
    : IDownloadTransferService
{
    private const int BufferSize = 81_920;
    private const int MaximumFileNameLength = 180;
    private const int MaximumPreservedExtensionLength = 32;

    // Progress is coalesced: one report per window instead of one
    // per 80 KB chunk (1,000+ per second on a fast transfer), which
    // kept the queue lock, record allocations and UI refreshes hot.
    private const long ProgressReportIntervalMilliseconds = 100;

    private const long StateSaveIntervalMilliseconds = 2_000;

    private static readonly TimeSpan DefaultStallTimeout =
        TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> ReservedWindowsNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IHostFileResolver> _resolvers;
    private readonly IMegaDownloader? _megaDownloader;
    private readonly TimeSpan _stallTimeout;

    public HttpDownloadTransferService(
        HttpClient httpClient,
        IEnumerable<IHostFileResolver>? resolvers = null,
        IMegaDownloader? megaDownloader = null)
        : this(
            httpClient,
            resolvers,
            megaDownloader,
            DefaultStallTimeout)
    {
    }

    /// <param name="stallTimeout">
    /// How long a body read may receive nothing before the
    /// connection counts as dead and the transfer resumes on a new
    /// one. HttpClient.Timeout is infinite for large files, so
    /// without this a silently dropped connection hangs forever.
    /// </param>
    internal HttpDownloadTransferService(
        HttpClient httpClient,
        IEnumerable<IHostFileResolver>? resolvers,
        IMegaDownloader? megaDownloader,
        TimeSpan stallTimeout)
    {
        if (stallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stallTimeout));
        }

        _stallTimeout = stallTimeout;

        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));

        _resolvers = resolvers?.ToArray() ??
            Array.Empty<IHostFileResolver>();

        _megaDownloader = megaDownloader;
    }

    public async Task<DownloadTransferResult> DownloadAsync(
        DownloadTransferRequest request,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri source = ValidateSource(request.Source);
        Uri originalSource = source;
        string? suggestedFileName = null;
        long? advertisedTotalBytes = null;
        string? expectedSha256 = null;
        bool preferSuggestedName = false;

        // Host pages (/u/ID) become direct downloads first.
        // Resolver failures are typed (challenge/quota/unreachable)
        // and propagate — the item stays queued for solve or retry.
        foreach (IHostFileResolver resolver in _resolvers)
        {
            if (!resolver.CanResolve(source))
            {
                continue;
            }

            ResolvedHostFile resolved =
                await resolver.ResolveAsync(
                        source,
                        _httpClient,
                        cancellationToken)
                    .ConfigureAwait(false);

            source = resolved.DownloadUri;
            suggestedFileName = resolved.SuggestedFileName;
            advertisedTotalBytes = resolved.TotalBytes;
            expectedSha256 = resolved.ExpectedSha256;
            preferSuggestedName = true;
            break;
        }

        string destinationDirectory =
            ValidateDestinationDirectory(
                request.DestinationDirectory);

        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        bool isMega =
            _megaDownloader is not null &&
            MegaLinks.IsLink(source);

        // Queue downloads carry a resume key: their partial file has
        // a stable name and survives cancel, pause, quota stops,
        // crashes and restarts. MEGA decrypts client-side and always
        // starts fresh.
        string? resumeKey =
            !isMega && PartialDownloadFiles.IsValidKey(request.ResumeKey)
                ? request.ResumeKey
                : null;

        string temporaryPath;
        PartialDownloadState? resumeState = null;

        if (resumeKey is not null)
        {
            temporaryPath =
                PartialDownloadFiles.PartPath(destinationDirectory, resumeKey);

            resumeState =
                PartialDownloadFiles.TryLoad(destinationDirectory, resumeKey);

            // Same queue item, different link: never mix the bytes.
            if (
                resumeState is not null &&
                !string.Equals(
                    resumeState.SourceUrl,
                    originalSource.AbsoluteUri,
                    StringComparison.Ordinal))
            {
                resumeState = null;
            }

            if (resumeState is null)
            {
                StartFreshPartial(destinationDirectory, resumeKey, temporaryPath);
            }
        }
        else
        {
            (
                temporaryPath,
                FileStream temporaryStream
            ) = CreateTemporaryFile(destinationDirectory);

            // The creation stream only reserves a unique name.
            temporaryStream.Dispose();
        }

        // The temp file is kept across throttle retries so a
        // 429/503 wait can resume instead of restarting. Without a
        // resume key it is deleted on terminal failure (see catch
        // below); with one it is kept for the next attempt.
        string? pendingTemporaryPath = temporaryPath;
        long bytesWritten = 0;
        long? totalBytes = advertisedTotalBytes;
        string? fileName = null;
        RangeConditionHeaderValue? resumeValidator = null;

        // Set while the segmented engine owns the resume state, so
        // the single-stream save below never overwrites its map.
        bool segmentsOwnState = false;

        // MEGA links carry encrypted bytes: decrypt straight into
        // the temp file instead of the generic HTTP path.
        if (isMega)
        {
            return await DownloadViaMegaAsync(
                    source,
                    destinationDirectory,
                    temporaryPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // When the host advertises a SHA-256, hash while writing
        // instead of re-reading the whole file afterwards. The
        // state always covers exactly bytesWritten bytes, so a
        // resumed transfer keeps hashing where it left off.
        IncrementalHash? hasher =
            string.IsNullOrWhiteSpace(expectedSha256)
                ? null
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            if (resumeState is not null && resumeKey is not null)
            {
                resumeValidator = ToValidator(resumeState);
                fileName = resumeState.FileName;
                totalBytes = resumeState.TotalBytes ?? totalBytes;

                if (
                    resumeState.Segments is { Count: > 0 } savedSegments &&
                    resumeState.TotalBytes is long savedTotal)
                {
                    segmentsOwnState = true;

                    bool finished;

                    try
                    {
                        finished = await RunSegmentsAsync(
                                source,
                                temporaryPath,
                                destinationDirectory,
                                resumeKey,
                                resumeState,
                                savedTotal,
                                Math.Max(1, request.Connections),
                                resumeValidator,
                                savedSegments,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                        when (
                            exception is HttpRequestException or
                                IOException &&
                            !cancellationToken.IsCancellationRequested)
                    {
                        // The saved map stays on disk for the next
                        // try; this attempt continues as one stream.
                        finished = false;
                    }

                    segmentsOwnState = false;

                    if (finished && fileName is not null)
                    {
                        return CompleteResumable(
                            temporaryPath,
                            destinationDirectory,
                            resumeKey,
                            fileName,
                            source,
                            originalSource,
                            savedTotal,
                            expectedSha256,
                            ref pendingTemporaryPath);
                    }

                    // Changed file or no range support any more:
                    // start over cleanly.
                    StartFreshPartial(destinationDirectory, resumeKey, temporaryPath);
                    resumeValidator = null;
                    fileName = null;
                    totalBytes = advertisedTotalBytes;
                }
                else
                {
                    long onDisk = new FileInfo(temporaryPath).Length;
                    bytesWritten = Math.Clamp(resumeState.ContiguousBytes, 0, onDisk);

                    if (bytesWritten > 0 && hasher is not null)
                    {
                        await HashPrefixAsync(
                                hasher,
                                temporaryPath,
                                bytesWritten,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long attemptStartBytes = bytesWritten;

                using var httpRequest =
                    new HttpRequestMessage(HttpMethod.Get, source);

                if (bytesWritten > 0)
                {
                    httpRequest.Headers.Range =
                        new RangeHeaderValue(bytesWritten, null);

                    // A changed file answers 200 with the new body
                    // instead of splicing old and new bytes.
                    if (resumeValidator is not null)
                    {
                        httpRequest.Headers.IfRange = resumeValidator;
                    }
                }

                using HttpResponseMessage response =
                    await _httpClient.SendAsync(
                            httpRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (
                    HostThrottlePolicy.IsThrottleStatusCode(
                        response.StatusCode)
                )
                {
                    TimeSpan? retryAfter =
                        HostThrottlePolicy.TryParseRetryAfter(
                            response.Headers,
                            DateTimeOffset.UtcNow);

                    TimeSpan? wait =
                        HostThrottlePolicy.ComputeWaitBeforeRetry(
                            attempt,
                            retryAfter);

                    if (
                        !HostThrottlePolicy.ShouldRetryAttempt(
                            response.StatusCode,
                            attempt) ||
                        wait is null
                    )
                    {
                        throw new HostThrottledException(
                            source.Host,
                            HostThrottlePolicy.FriendlyMessage(
                                source.Host,
                                response.StatusCode,
                                retryAfter));
                    }

                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));

                    await Task.Delay(wait.Value, cancellationToken)
                        .ConfigureAwait(false);

                    continue;
                }

                if (
                    response.StatusCode ==
                        System.Net.HttpStatusCode.Forbidden)
                {
                    ChallengeKind? challenge =
                        await DetectChallengeAsync(
                                response,
                                source,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (challenge.HasValue)
                    {
                        throw BuildChallengeException(
                            source,
                            challenge.Value);
                    }

                    if (HostThrottlePolicy.IsSingleLaneHost(source.Host))
                    {
                        throw new HostThrottledException(
                            source.Host,
                            HostThrottlePolicy.FriendlyMessage(
                                source.Host,
                                response.StatusCode,
                                retryAfter: null,
                                isPixeldrainQuotaBody: true),
                            isQuotaExhausted: true);
                    }
                }

                if (
                    response.StatusCode ==
                        System.Net.HttpStatusCode
                            .RequestedRangeNotSatisfiable
                )
                {
                    // Our resume offset is stale (the file
                    // changed server-side). Restart once.
                    bytesWritten = 0;
                    totalBytes = null;
                    TruncateFile(temporaryPath);
                    ResetHash(ref hasher);

                    if (attempt >= 4)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    continue;
                }

                response.EnsureSuccessStatusCode();

                HttpContent content =
                    response.Content ??
                    throw new HttpRequestException(
                        "The HTTP response did not contain a body.");

                bool isPartial =
                    response.StatusCode ==
                        System.Net.HttpStatusCode.PartialContent;

                ContentRangeHeaderValue? answeredRange =
                    content.Headers.ContentRange;

                bool rangeMatches =
                    isPartial &&
                    bytesWritten > 0 &&
                    (answeredRange?.From is null ||
                        answeredRange.From == bytesWritten) &&
                    (totalBytes is null ||
                        answeredRange?.Length is null ||
                        answeredRange.Length == totalBytes);

                if (rangeMatches)
                {
                    totalBytes ??=
                        DeriveTotalBytes(
                            content.Headers,
                            bytesWritten);
                }
                else
                {
                    if (isPartial && bytesWritten > 0)
                    {
                        // A 206 for a different offset or file size:
                        // drop it and ask for the whole file again.
                        bytesWritten = 0;
                        totalBytes = advertisedTotalBytes;
                        TruncateFile(temporaryPath);
                        ResetHash(ref hasher);
                        resumeValidator = null;
                        continue;
                    }

                    if (bytesWritten > 0)
                    {
                        // The server ignored our Range header (or
                        // If-Range saw a changed file). Restart
                        // cleanly instead of splicing.
                        bytesWritten = 0;
                        TruncateFile(temporaryPath);
                        ResetHash(ref hasher);
                    }

                    totalBytes = content.Headers.ContentLength;
                    resumeValidator = SegmentedDownloader.BuildValidator(response);
                }

                if (fileName is null)
                {
                    Uri fileNameSource =
                        response.RequestMessage?.RequestUri is
                            { IsAbsoluteUri: true } responseUri
                            ? responseUri
                            : source;

                    fileName =
                        ResolveSafeFileName(
                            content.Headers,
                            fileNameSource,
                            suggestedFileName,
                            preferSuggestedName);
                }

                if (resumeKey is not null && bytesWritten == 0)
                {
                    SaveStreamState(
                        destinationDirectory,
                        resumeKey,
                        originalSource,
                        totalBytes,
                        resumeValidator,
                        fileName,
                        0);
                }

                // Large files on range-capable servers download over
                // several connections at once. Free file hosts are
                // excluded: they throttle per IP and extra
                // connections burn quota or trigger bans.
                if (
                    attempt == 1 &&
                    bytesWritten == 0 &&
                    hasher is null &&
                    request.Connections > 1 &&
                    request.MaxBytesPerSecond is not > 0 &&
                    totalBytes is long segmentTotal &&
                    !HostThrottlePolicy.IsSingleLaneHost(source.Host) &&
                    !HostThrottlePolicy.IsSingleLaneHost(originalSource.Host) &&
                    SegmentedDownloader.SupportsRanges(response))
                {
                    int connections =
                        SegmentedDownloader.PlanConnectionCount(
                            segmentTotal,
                            request.Connections);

                    if (connections > 1)
                    {
                        // Ranges go to the post-redirect URL the
                        // server actually answered from.
                        Uri rangeSource =
                            response.RequestMessage?.RequestUri is
                                { IsAbsoluteUri: true } answered
                                ? answered
                                : source;

                        RangeConditionHeaderValue? validator =
                            SegmentedDownloader.BuildValidator(response);

                        // Release the probe connection before
                        // opening the range connections.
                        response.Dispose();

                        bool segmented;
                        segmentsOwnState = resumeKey is not null;

                        try
                        {
                            segmented = resumeKey is not null
                                ? await RunSegmentsAsync(
                                        rangeSource,
                                        temporaryPath,
                                        destinationDirectory,
                                        resumeKey,
                                        new PartialDownloadState(
                                            PartialDownloadState.CurrentVersion,
                                            originalSource.AbsoluteUri,
                                            segmentTotal,
                                            ETagOf(validator),
                                            validator?.Date,
                                            fileName,
                                            0,
                                            null),
                                        segmentTotal,
                                        connections,
                                        validator,
                                        null,
                                        progress,
                                        cancellationToken)
                                    .ConfigureAwait(false)
                                : await new SegmentedDownloader(
                                        _httpClient,
                                        _stallTimeout)
                                    .DownloadAsync(
                                        rangeSource,
                                        temporaryPath,
                                        segmentTotal,
                                        connections,
                                        validator,
                                        progress,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                            when (
                                exception is HttpRequestException or
                                    IOException &&
                                !cancellationToken.IsCancellationRequested)
                        {
                            // Anything odd: one clean single stream.
                            segmented = false;
                        }

                        segmentsOwnState = false;

                        if (segmented)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (resumeKey is not null)
                            {
                                return CompleteResumable(
                                    temporaryPath,
                                    destinationDirectory,
                                    resumeKey,
                                    fileName!,
                                    source,
                                    originalSource,
                                    segmentTotal,
                                    expectedSha256,
                                    ref pendingTemporaryPath);
                            }

                            DownloadTransferResult segmentedResult =
                                Complete(
                                    temporaryPath,
                                    destinationDirectory,
                                    fileName!,
                                    source,
                                    originalSource,
                                    segmentTotal,
                                    segmentTotal);

                            pendingTemporaryPath = null;
                            return segmentedResult;
                        }

                        TruncateFile(temporaryPath);

                        if (resumeKey is not null)
                        {
                            PartialDownloadFiles.DeleteState(
                                destinationDirectory,
                                resumeKey);
                        }

                        continue;
                    }
                }

                byte[] buffer =
                    ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
                    await using Stream sourceStream =
                        await content.ReadAsStreamAsync(
                                cancellationToken)
                            .ConfigureAwait(false);

                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));

                    // Optional quiet-hours pacing: a sliding 1s
                    // window shared across resume attempts.
                    long windowBytes = 0;
                    DateTimeOffset windowStart =
                        DateTimeOffset.UtcNow;

                    // MinValue: the first chunk always reports.
                    long lastReportTicks = long.MinValue;
                    long lastStateSaveTicks = Environment.TickCount64;

                    using var stallCancellation =
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                cancellationToken);

                    await using (
                        var resumeStream = new FileStream(
                            temporaryPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.None,
                            BufferSize,
                            FileOptions.Asynchronous |
                                FileOptions.SequentialScan))
                    {
                        // Drop any tail a failed write left past
                        // the last confirmed byte before resuming.
                        resumeStream.SetLength(bytesWritten);

                        resumeStream.Seek(
                            bytesWritten,
                            SeekOrigin.Begin);

                        while (true)
                        {
                            int bytesRead;

                            // Re-arm the inactivity timer per read.
                            stallCancellation.CancelAfter(
                                _stallTimeout);

                            try
                            {
                                bytesRead =
                                    await sourceStream.ReadAsync(
                                            buffer.AsMemory(
                                                0,
                                                BufferSize),
                                            stallCancellation.Token)
                                        .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException exception)
                                when (
                                    !cancellationToken
                                        .IsCancellationRequested)
                            {
                                throw new IOException(
                                    $"No data from {source.Host} for " +
                                    $"{_stallTimeout.TotalSeconds:0} " +
                                    "seconds; reconnecting.",
                                    exception);
                            }

                            if (bytesRead == 0)
                            {
                                break;
                            }

                            await resumeStream.WriteAsync(
                                    buffer.AsMemory(
                                        0,
                                        bytesRead),
                                    cancellationToken)
                                .ConfigureAwait(false);

                            hasher?.AppendData(
                                buffer,
                                0,
                                bytesRead);

                            checked
                            {
                                bytesWritten += bytesRead;
                                windowBytes += bytesRead;
                            }

                            long nowTicks = Environment.TickCount64;

                            if (
                                lastReportTicks == long.MinValue ||
                                nowTicks - lastReportTicks >=
                                    ProgressReportIntervalMilliseconds)
                            {
                                lastReportTicks = nowTicks;

                                progress?.Report(
                                    new DownloadTransferProgress(
                                        bytesWritten,
                                        totalBytes));
                            }

                            // Every few seconds: push buffered bytes to
                            // the OS, then record them, so a crash
                            // resumes from (at most) a moment ago.
                            if (
                                resumeKey is not null &&
                                nowTicks - lastStateSaveTicks >=
                                    StateSaveIntervalMilliseconds)
                            {
                                lastStateSaveTicks = nowTicks;

                                await resumeStream.FlushAsync(
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                SaveStreamState(
                                    destinationDirectory,
                                    resumeKey,
                                    originalSource,
                                    totalBytes,
                                    resumeValidator,
                                    fileName,
                                    bytesWritten);
                            }

                            if (request.MaxBytesPerSecond > 0)
                            {
                                TimeSpan elapsed =
                                    DateTimeOffset.UtcNow -
                                    windowStart;

                                TimeSpan wait =
                                    TransferThrottler.ComputeDelay(
                                        windowBytes,
                                        elapsed,
                                        request.MaxBytesPerSecond);

                                if (wait > TimeSpan.Zero)
                                {
                                    await Task.Delay(
                                            wait,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                }

                                if (elapsed >= TimeSpan.FromSeconds(1))
                                {
                                    windowBytes = 0;
                                    windowStart =
                                        DateTimeOffset.UtcNow;
                                }
                            }
                        }

                        await resumeStream.FlushAsync(
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));
                }
                catch (Exception exception)
                    when (
                        exception is HttpRequestException or
                            IOException &&
                        (attempt < 4 ||
                            bytesWritten > attemptStartBytes))
                {
                    // An attempt that moved data earns a fresh
                    // retry budget: a long download over a flaky
                    // link should resume as often as it keeps
                    // making progress, not die on the 4th blip.
                    if (bytesWritten > attemptStartBytes)
                    {
                        attempt = 0;
                    }

                    // Keep the partial file; the next attempt
                    // resumes via the Range header above.
                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));

                    TimeSpan? wait =
                        HostThrottlePolicy.ComputeWaitBeforeRetry(
                            attempt,
                            retryAfter: null);

                    if (wait.HasValue)
                    {
                        await Task.Delay(
                                wait.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (hasher is not null)
                {
                    VerifyChecksumHex(
                        Convert.ToHexString(
                            hasher.GetHashAndReset()),
                        temporaryPath,
                        expectedSha256,
                        source);
                }

                DownloadTransferResult result =
                    Complete(
                        temporaryPath,
                        destinationDirectory,
                        fileName!,
                        source,
                        originalSource,
                        bytesWritten,
                        totalBytes);

                pendingTemporaryPath = null;

                if (resumeKey is not null)
                {
                    PartialDownloadFiles.DeleteState(
                        destinationDirectory,
                        resumeKey);
                }

                return result;
            }
        }
        catch (Exception exception)
        {
            if (resumeKey is not null && pendingTemporaryPath is not null)
            {
                if (exception is ChecksumMismatchException)
                {
                    // The bytes are wrong: never resume from them.
                    // The file itself stays for inspection.
                    PartialDownloadFiles.DeleteState(
                        destinationDirectory,
                        resumeKey);
                }
                else if (!segmentsOwnState && fileName is not null)
                {
                    // Cancel, pause, quota, network: keep the partial
                    // and record exactly how much of it is good.
                    SaveStreamState(
                        destinationDirectory,
                        resumeKey,
                        originalSource,
                        totalBytes,
                        resumeValidator,
                        fileName,
                        bytesWritten);
                }

                throw;
            }

            // A checksum mismatch promises the user the file was
            // KEPT for inspection; every other failure cleans up.
            if (
                pendingTemporaryPath is not null &&
                exception is not ChecksumMismatchException)
            {
                TryDeleteTemporaryFile(
                    pendingTemporaryPath);
            }

            throw;
        }
        finally
        {
            hasher?.Dispose();
        }
    }

    /// <summary>
    /// Moves the finished temp file to a free name and marks it as
    /// downloaded from the internet.
    /// </summary>
    private static DownloadTransferResult Complete(
        string temporaryPath,
        string destinationDirectory,
        string fileName,
        Uri source,
        Uri originalSource,
        long bytesWritten,
        long? totalBytes)
    {
        string destinationPath =
            MoveToAvailableDestination(
                temporaryPath,
                destinationDirectory,
                fileName);

        MarkOfTheWeb.TryApply(
            destinationPath,
            source,
            originalSource);

        return new DownloadTransferResult(
            source,
            destinationPath,
            Path.GetFileName(destinationPath),
            bytesWritten,
            totalBytes);
    }

    private Task<bool> RunSegmentsAsync(
        Uri rangeSource,
        string temporaryPath,
        string destinationDirectory,
        string resumeKey,
        PartialDownloadState baseState,
        long totalBytes,
        int connections,
        RangeConditionHeaderValue? validator,
        IReadOnlyList<PartialSegment>? resumeSegments,
        IProgress<DownloadTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        return new SegmentedDownloader(_httpClient, _stallTimeout)
            .DownloadAsync(
                rangeSource,
                temporaryPath,
                totalBytes,
                Math.Clamp(connections, 1, SegmentedDownloader.MaximumConnections),
                validator,
                progress,
                cancellationToken,
                resumeSegments,
                segments => PartialDownloadFiles.TrySave(
                    destinationDirectory,
                    resumeKey,
                    baseState with
                    {
                        ContiguousBytes = 0,
                        Segments = segments
                    }));
    }

    private static DownloadTransferResult CompleteResumable(
        string temporaryPath,
        string destinationDirectory,
        string resumeKey,
        string fileName,
        Uri source,
        Uri originalSource,
        long totalBytes,
        string? expectedSha256,
        ref string? pendingTemporaryPath)
    {
        // Segments land out of order, so a host hash (rare here)
        // is checked against the finished file.
        VerifyChecksum(temporaryPath, expectedSha256, source);

        DownloadTransferResult result =
            Complete(
                temporaryPath,
                destinationDirectory,
                fileName,
                source,
                originalSource,
                totalBytes,
                totalBytes);

        pendingTemporaryPath = null;
        PartialDownloadFiles.DeleteState(destinationDirectory, resumeKey);
        return result;
    }

    private static void StartFreshPartial(
        string destinationDirectory,
        string resumeKey,
        string temporaryPath)
    {
        PartialDownloadFiles.DeleteState(destinationDirectory, resumeKey);

        using var reset = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
    }

    private static void SaveStreamState(
        string destinationDirectory,
        string resumeKey,
        Uri originalSource,
        long? totalBytes,
        RangeConditionHeaderValue? validator,
        string? fileName,
        long contiguousBytes)
    {
        PartialDownloadFiles.TrySave(
            destinationDirectory,
            resumeKey,
            new PartialDownloadState(
                PartialDownloadState.CurrentVersion,
                originalSource.AbsoluteUri,
                totalBytes,
                ETagOf(validator),
                validator?.Date,
                fileName,
                contiguousBytes,
                null));
    }

    private static string? ETagOf(RangeConditionHeaderValue? validator)
    {
        return validator?.EntityTag?.ToString();
    }

    private static RangeConditionHeaderValue? ToValidator(
        PartialDownloadState state)
    {
        if (
            !string.IsNullOrEmpty(state.ETag) &&
            EntityTagHeaderValue.TryParse(state.ETag, out EntityTagHeaderValue? tag) &&
            !tag.IsWeak)
        {
            return new RangeConditionHeaderValue(tag);
        }

        return state.LastModified is DateTimeOffset lastModified
            ? new RangeConditionHeaderValue(lastModified)
            : null;
    }

    private static async Task HashPrefixAsync(
        IncrementalHash hasher,
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            long remaining = length;

            while (remaining > 0)
            {
                int read = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(BufferSize, remaining)),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ResetHash(ref IncrementalHash? hasher)
    {
        if (hasher is null)
        {
            return;
        }

        hasher.Dispose();
        hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Verifies a host-advertised SHA-256 (Pixeldrain) against the
    /// finished temp file. The file is KEPT and the item is flagged
    /// failed with its path, so the user inspects instead of the
    /// app silently deleting evidence. No advertised hash: no-op.
    /// </summary>
    internal static void VerifyChecksum(
        string temporaryPath,
        string? expectedSha256,
        Uri source)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return;
        }

        string actual;

        using (var sha = SHA256.Create())
        using (Stream stream = File.OpenRead(temporaryPath))
        {
            byte[] hash = sha.ComputeHash(stream);
            actual = Convert.ToHexString(hash);
        }

        VerifyChecksumHex(
            actual,
            temporaryPath,
            expectedSha256,
            source);
    }

    internal static void VerifyChecksumHex(
        string actual,
        string temporaryPath,
        string? expectedSha256,
        Uri source)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return;
        }

        if (
            !actual.Equals(
                expectedSha256.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                source,
                temporaryPath,
                $"The downloaded file failed its SHA-256 check " +
                $"(expected {expectedSha256.Trim()[..16]}…, " +
                $"got {actual[..16]}…). The file was KEPT at " +
                $"{temporaryPath} for inspection — requeue to " +
                "retry, or remove the item.");
        }
    }

    private async Task<DownloadTransferResult> DownloadViaMegaAsync(
        Uri source,
        string destinationDirectory,
        string temporaryPath,
        IProgress<DownloadTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? pendingTemporaryPath = temporaryPath;

        try
        {
            MegaFileMetadata metadata =
                await _megaDownloader!.GetInfoAsync(
                        source,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (metadata.IsFolder)
            {
                // MEGA folders cannot expand to per-file links
                // (child keys never leave the folder session), so
                // the one folder row downloads every file into a
                // subfolder. Gofile/Pixeldrain folders expand to
                // one row per file before the batch instead.
                string folderDir = Path.Combine(
                    destinationDirectory,
                    SanitizeFileName(metadata.Name) ?? "mega-folder");

                Directory.CreateDirectory(folderDir);

                long folderBytes =
                    await _megaDownloader.DownloadFolderToAsync(
                            source,
                            folderDir,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                pendingTemporaryPath = null;
                TryDeleteTemporaryFile(temporaryPath);

                MarkOfTheWeb.TryApplyToDirectory(folderDir, source);

                return new DownloadTransferResult(
                    source,
                    folderDir,
                    Path.GetFileName(folderDir),
                    folderBytes,
                    folderBytes);
            }

            await _megaDownloader.DownloadToAsync(
                    source,
                    temporaryPath,
                    metadata.Size,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            long bytesWritten = new FileInfo(temporaryPath).Length;

            string fileName =
                SanitizeFileName(metadata.Name) ?? "download";

            string destinationPath =
                MoveToAvailableDestination(
                    temporaryPath,
                    destinationDirectory,
                    fileName);

            pendingTemporaryPath = null;

            MarkOfTheWeb.TryApply(destinationPath, source);

            return new DownloadTransferResult(
                source,
                destinationPath,
                Path.GetFileName(destinationPath),
                bytesWritten,
                metadata.Size);
        }
        catch
        {
            if (pendingTemporaryPath is not null)
            {
                TryDeleteTemporaryFile(pendingTemporaryPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Reads a bounded snippet of a 403 page and asks the pure
    /// detector what wall (if any) the host put up. Never throws:
    /// an unreadable body simply means "no detectable challenge".
    /// </summary>
    public static async Task<ChallengeKind?> DetectChallengeAsync(
        HttpResponseMessage response,
        Uri source,
        CancellationToken cancellationToken)
    {
        string snippet;

        try
        {
            snippet = await ErrorSnippetReader.ReadSnippetAsync(
                    response.Content,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return ChallengeDetector.Detect(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            snippet,
            source.Host);
    }

    public static Exception BuildChallengeException(
        Uri source,
        ChallengeKind kind)
    {
        if (kind == ChallengeKind.QuotaWall)
        {
            return new HostThrottledException(
                source.Host,
                HostThrottlePolicy.FriendlyMessage(
                    source.Host,
                    System.Net.HttpStatusCode.Forbidden,
                    retryAfter: null,
                    isPixeldrainQuotaBody: true),
                isQuotaExhausted: true);
        }

        return new ChallengeDetectedException(
            source.Host,
            kind,
            source.AbsoluteUri,
            ChallengeDetector.FriendlyMessage(
                source.Host,
                kind,
                source.AbsoluteUri));
    }

    private static Uri ValidateSource(Uri? source)
    {
        if (source is null)
        {
            throw new ArgumentException(
                "A source URL is required.",
                nameof(source));
        }

        if (!source.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The source URL must be absolute.",
                nameof(source));
        }

        bool isHttp = string.Equals(
            source.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);

        bool isHttps = string.Equals(
            source.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

        if (!isHttp && !isHttps)
        {
            throw new ArgumentException(
                "Only HTTP and HTTPS downloads are supported.",
                nameof(source));
        }

        if (string.IsNullOrWhiteSpace(source.Host))
        {
            throw new ArgumentException(
                "The source URL must contain a host.",
                nameof(source));
        }

        return source;
    }

    private static string ValidateDestinationDirectory(
        string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException(
                "A destination directory is required.",
                nameof(destinationDirectory));
        }

        try
        {
            return Path.GetFullPath(destinationDirectory);
        }
        catch (Exception exception)
            when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException
            )
        {
            throw new ArgumentException(
                "The destination directory is invalid.",
                nameof(destinationDirectory),
                exception);
        }
    }

    private static (
        string Path,
        FileStream Stream
    ) CreateTemporaryFile(string destinationDirectory)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string temporaryPath = Path.Combine(
                destinationDirectory,
                $".download-{Guid.NewGuid():N}.part");

            try
            {
                var stream = new FileStream(
                    temporaryPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = BufferSize,
                        Options =
                            FileOptions.Asynchronous |
                            FileOptions.SequentialScan
                    });

                return (temporaryPath, stream);
            }
            catch (IOException)
                when (
                    File.Exists(temporaryPath) ||
                    Directory.Exists(temporaryPath)
                )
            {
                // A GUID collision is extraordinarily unlikely,
                // but CreateNew keeps the operation race-safe.
            }
        }

        throw new IOException(
            "A unique temporary download file could not be created.");
    }

    private static string MoveToAvailableDestination(
        string temporaryPath,
        string destinationDirectory,
        string fileName)
    {
        for (
            int collisionIndex = 0;
            collisionIndex < int.MaxValue;
            collisionIndex++
        )
        {
            string candidateName =
                CreateCollisionFileName(
                    fileName,
                    collisionIndex);

            string candidatePath = Path.Combine(
                destinationDirectory,
                candidateName);

            if (
                File.Exists(candidatePath) ||
                Directory.Exists(candidatePath)
            )
            {
                continue;
            }

            try
            {
                File.Move(
                    temporaryPath,
                    candidatePath,
                    overwrite: false);

                return candidatePath;
            }
            catch (IOException)
                when (
                    File.Exists(candidatePath) ||
                    Directory.Exists(candidatePath)
                )
            {
                // Another transfer won the filename race.
                // Try the next suffix without overwriting it.
            }
        }

        throw new IOException(
            "An available destination filename could not be found.");
    }

    private static string CreateCollisionFileName(
        string fileName,
        int collisionIndex)
    {
        if (collisionIndex == 0)
        {
            return fileName;
        }

        string extension = Path.GetExtension(fileName);

        if (
            extension.Length >
            MaximumPreservedExtensionLength
        )
        {
            extension = string.Empty;
        }

        string stem = extension.Length == 0
            ? fileName
            : fileName[..^extension.Length];

        string suffix = $" ({collisionIndex})";

        int maximumStemLength =
            MaximumFileNameLength -
            extension.Length -
            suffix.Length;

        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength];
        }

        stem = stem.TrimEnd(' ', '.');

        if (stem.Length == 0)
        {
            stem = "_";
        }

        return stem + suffix + extension;
    }

    /// <summary>The name a response would be saved under (link checks).</summary>
    internal static string SuggestFileName(
        HttpContentHeaders headers,
        Uri source)
    {
        return ResolveSafeFileName(headers, source);
    }

    private static string ResolveSafeFileName(
        HttpContentHeaders headers,
        Uri source,
        string? suggestedFileName = null,
        bool preferSuggestedName = false)
    {
        string? dispositionName =
            GetContentDispositionFileName(headers);

        string? safeDispositionName =
            SanitizeFileName(dispositionName);

        if (safeDispositionName is not null)
        {
            return safeDispositionName;
        }

        string? urlName = GetUrlFileName(source);
        string? safeUrlName = SanitizeFileName(urlName);
        string? safeSuggested = SanitizeFileName(suggestedFileName);

        // Host-advertised names (Pixeldrain info) beat the URL
        // when a resolver engaged: endpoint paths carry IDs, not
        // names. Server headers always stay authoritative, and the
        // suggestion passes through the same sanitizer.
        if (preferSuggestedName && safeSuggested is not null)
        {
            return safeSuggested;
        }

        if (safeUrlName is not null)
        {
            return safeUrlName;
        }

        return safeSuggested ?? "download";
    }

    private static string? GetContentDispositionFileName(
        HttpContentHeaders headers)
    {
        try
        {
            ContentDispositionHeaderValue? disposition =
                headers.ContentDisposition;

            if (disposition is null)
            {
                return null;
            }

            if (
                !string.IsNullOrWhiteSpace(
                    disposition.FileNameStar)
            )
            {
                return DecodeExtendedHeaderValue(
                    disposition.FileNameStar);
            }

            if (
                !string.IsNullOrWhiteSpace(
                    disposition.FileName)
            )
            {
                return Unquote(disposition.FileName);
            }
        }
        catch (FormatException)
        {
            // A malformed server-controlled header must not
            // influence the destination path.
        }

        return null;
    }

    private static string DecodeExtendedHeaderValue(
        string rawValue)
    {
        string value = Unquote(rawValue);

        int firstQuote = value.IndexOf(
            "'",
            StringComparison.Ordinal);

        int secondQuote = firstQuote < 0
            ? -1
            : value.IndexOf(
                "'",
                firstQuote + 1,
                StringComparison.Ordinal);

        if (
            firstQuote >= 0 &&
            secondQuote > firstQuote
        )
        {
            value = value[(secondQuote + 1)..];
        }

        return DecodePercentEscapes(value);
    }

    private static string Unquote(string rawValue)
    {
        string value = rawValue.Trim();

        if (
            value.Length >= 2 &&
            value[0] == '"' &&
            value[^1] == '"'
        )
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? GetUrlFileName(Uri source)
    {
        string escapedPath = source.GetComponents(
            UriComponents.Path,
            UriFormat.UriEscaped);

        if (
            string.IsNullOrWhiteSpace(escapedPath) ||
            escapedPath.EndsWith(
                "/",
                StringComparison.Ordinal)
        )
        {
            return null;
        }

        int separatorIndex =
            escapedPath.LastIndexOf('/');

        string escapedName = separatorIndex >= 0
            ? escapedPath[(separatorIndex + 1)..]
            : escapedPath;

        return DecodePercentEscapes(escapedName);
    }

    private static string DecodePercentEscapes(
        string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    internal static string? SanitizeFileName(
        string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string value = candidate
            .Trim()
            .Trim('"');

        int lastSeparator = Math.Max(
            value.LastIndexOf('/'),
            value.LastIndexOf('\\'));

        if (lastSeparator >= 0)
        {
            value = value[(lastSeparator + 1)..];
        }

        value = value
            .Trim()
            .Trim('"')
            .TrimEnd(' ', '.');

        if (
            value.Length == 0 ||
            value is "." or ".."
        )
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);

        foreach (char character in value)
        {
            // Bidi overrides and zero-width characters let a server
            // disguise "invoice<RLO>fdp.exe" as "invoiceexe.pdf".
            // Drop them outright: they never belong in a filename.
            if (IsSpoofingCharacter(character))
            {
                continue;
            }

            // Collapse whitespace runs (and exotic Unicode spaces)
            // so "report.pdf<60 spaces>.exe" cannot push the real
            // extension out of view in Explorer or the queue.
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(
                IsInvalidFileNameCharacter(character)
                    ? '_'
                    : character);
        }

        string fileName = builder
            .ToString()
            .Trim()
            .TrimEnd(' ', '.');

        if (
            fileName.Length == 0 ||
            fileName is "." or ".."
        )
        {
            return null;
        }

        string deviceStem = fileName
            .Split('.', 2)[0]
            .TrimEnd(' ', '.');

        if (ReservedWindowsNames.Contains(deviceStem))
        {
            fileName = "_" + fileName;
        }

        return LimitFileName(fileName);
    }

    internal static bool IsSpoofingCharacter(char character)
    {
        return character is
            '\u061C' or                        // Arabic letter mark
            (>= '\u200B' and <= '\u200F') or  // zero-width, LRM/RLM
            (>= '\u202A' and <= '\u202E') or  // bidi embeds/overrides
            (>= '\u2060' and <= '\u2064') or  // word joiner, invisibles
            (>= '\u2066' and <= '\u2069') or  // bidi isolates
            '\uFEFF';                          // zero-width no-break
    }

    private static bool IsInvalidFileNameCharacter(
        char character)
    {
        return
            char.IsControl(character) ||
            character is
                '<' or
                '>' or
                ':' or
                '"' or
                '/' or
                '\\' or
                '|' or
                '?' or
                '*';
    }

    private static string LimitFileName(
        string fileName)
    {
        if (fileName.Length <= MaximumFileNameLength)
        {
            return fileName;
        }

        string extension = Path.GetExtension(fileName);

        if (
            extension.Length >
            MaximumPreservedExtensionLength
        )
        {
            extension = string.Empty;
        }

        int maximumStemLength =
            MaximumFileNameLength - extension.Length;

        string stem = extension.Length == 0
            ? fileName
            : fileName[..^extension.Length];

        stem = stem[..maximumStemLength]
            .TrimEnd(' ', '.');

        if (stem.Length == 0)
        {
            stem = "_";
        }

        return stem + extension;
    }

    private static long? DeriveTotalBytes(
        HttpContentHeaders contentHeaders,
        long resumeOffset)
    {
        // A 206 response carries the full length in
        // Content-Range (bytes s-e/total); otherwise the
        // remaining Content-Length plus what we already hold.
        if (contentHeaders.ContentRange?.Length is long total)
        {
            return total;
        }

        if (contentHeaders.ContentLength is long remaining)
        {
            return checked(resumeOffset + remaining);
        }

        return null;
    }

    private static void TruncateFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Truncate,
            FileAccess.Write,
            FileShare.None);
    }

    private static void TryDeleteTemporaryFile(
        string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException
            )
        {
            // Cleanup is best effort. The original transfer
            // exception remains more useful to the caller.
        }
    }
}
