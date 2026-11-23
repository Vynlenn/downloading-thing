using System.Collections.Concurrent;
using DeezNutzDownloader.Core.Challenges;

namespace DeezNutzDownloader.Core.Downloads;

public sealed record DownloadBatchResult(
    int SelectedCount,
    int StartedCount,
    int CompletedCount,
    int FailedCount,
    int CanceledCount,
    int NotStartedCount);

/// <summary>
/// Runs a batch of queue items with bounded concurrency.
///
/// The batch is live: items can be paused or canceled one by one,
/// added while it runs (resume, newly queued links), and are taken
/// in the queue's current order, so reordering the queue changes
/// what downloads next. Items stopped by a host limit wait inside
/// the batch until the host allows them again, keeping their
/// partial files.
/// </summary>
public sealed class DownloadOrchestrator : IDisposable
{
    private const int MaximumSupportedConcurrency = 64;

    // Idle workers re-check at least this often (waiting items).
    private static readonly TimeSpan MaximumIdleWait =
        TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly DownloadQueue _queue;
    private readonly IDownloadTransferService _transferService;
    private readonly Func<DateTimeOffset> _clock;

    private CancellationTokenSource? _runCancellation;
    private BatchWorkState? _activeBatch;
    private readonly ConcurrentDictionary<string, SemaphoreSlim>
        _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public DownloadOrchestrator(
        DownloadQueue queue,
        IDownloadTransferService transferService,
        Func<DateTimeOffset>? clock = null)
    {
        _queue =
            queue ??
            throw new ArgumentNullException(nameof(queue));

        _transferService =
            transferService ??
            throw new ArgumentNullException(
                nameof(transferService));

        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? RunningStateChanged;

    /// <summary>
    /// Raised (on a worker thread) when an item fails on a typed
    /// host challenge. The item is still marked failed with a
    /// friendly message; subscribers collect these and offer the
    /// solve window once per host after the batch.
    /// </summary>
    public event EventHandler<ChallengeDetectedException>?
        ChallengeBlocked;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _runCancellation is not null;
            }
        }
    }

    public async Task<DownloadBatchResult> StartAllAsync(
        string destinationDirectory,
        int maximumConcurrency,
        CancellationToken cancellationToken = default,
        long? maxBytesPerSecond = null,
        int connectionsPerDownload = 1,
        IReadOnlyCollection<Guid>? onlyItems = null)
    {
        string normalizedDestination =
            ValidateDestinationDirectory(
                destinationDirectory);

        if (
            maximumConcurrency is < 1 or
            > MaximumSupportedConcurrency
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                maximumConcurrency,
                $"Concurrency must be between 1 and " +
                $"{MaximumSupportedConcurrency}.");
        }

        CancellationTokenSource runCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        // onlyItems: start just these (Resume on a few rows) rather
        // than everything startable.
        IReadOnlyList<DownloadQueueItem> selectedItems =
            _queue.GetStartableItems()
                .Where(item => onlyItems is null || onlyItems.Contains(item.Id))
                .ToArray();

        var state = new BatchWorkState(
            normalizedDestination,
            maxBytesPerSecond,
            Math.Max(1, connectionsPerDownload),
            runCancellation.Token);

        foreach (DownloadQueueItem item in selectedItems)
        {
            state.Pending.Add(item.Id);
        }

        state.SelectedCount = selectedItems.Count;

        lock (_sync)
        {
            ThrowIfDisposed();

            if (_runCancellation is not null)
            {
                runCancellation.Dispose();

                throw new InvalidOperationException(
                    "A download batch is already running.");
            }

            _runCancellation = runCancellation;
            _activeBatch = state;
        }

        RaiseRunningStateChanged();

        try
        {
            if (selectedItems.Count == 0)
            {
                return new DownloadBatchResult(0, 0, 0, 0, 0, 0);
            }

            var workers = new Task[maximumConcurrency];

            for (
                int workerIndex = 0;
                workerIndex < workers.Length;
                workerIndex++
            )
            {
                workers[workerIndex] = RunWorkerAsync(state);
            }

            await Task.WhenAll(workers)
                .ConfigureAwait(false);

            int startedCount;

            lock (state.Sync)
            {
                startedCount = state.Started.Count;
            }

            return new DownloadBatchResult(
                SelectedCount: state.SelectedCount,
                StartedCount: startedCount,
                CompletedCount:
                    Volatile.Read(ref state.CompletedCount),
                FailedCount:
                    Volatile.Read(ref state.FailedCount),
                CanceledCount:
                    Volatile.Read(ref state.CanceledCount),
                NotStartedCount:
                    Math.Max(0, state.SelectedCount - startedCount));
        }
        finally
        {
            lock (_sync)
            {
                if (
                    ReferenceEquals(
                        _runCancellation,
                        runCancellation)
                )
                {
                    _runCancellation = null;
                    _activeBatch = null;
                }
            }

            runCancellation.Dispose();
            RaiseRunningStateChanged();
        }
    }

    public bool Cancel()
    {
        CancellationTokenSource? cancellation;

        lock (_sync)
        {
            cancellation = _runCancellation;
        }

        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds a queued item to the running batch (resumed item, new
    /// link). False when no batch runs; the caller starts one.
    /// </summary>
    public bool TryAddToBatch(Guid itemId)
    {
        BatchWorkState? batch = ActiveBatch();

        if (batch is null)
        {
            return false;
        }

        lock (batch.Sync)
        {
            if (batch.Closed)
            {
                return false;
            }

            if (
                !batch.Active.ContainsKey(itemId) &&
                batch.Pending.Add(itemId))
            {
                batch.SelectedCount++;
            }
        }

        SignalLaneFreed(batch);
        return true;
    }

    /// <summary>Pauses one item: active ones stop and keep their partial file.</summary>
    public bool PauseItem(Guid itemId)
    {
        return StopItem(itemId, ItemStop.Pause);
    }

    /// <summary>Cancels one item; its partial file is kept for a later retry.</summary>
    public bool CancelItem(Guid itemId)
    {
        return StopItem(itemId, ItemStop.Cancel);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _runCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The batch completed concurrently.
        }
    }

    /// <summary>
    /// Where an item's file goes: the batch folder plus its package
    /// subfolder, never escaping the batch folder. Shared with the
    /// UI so it can find and clean up an item's partial file.
    /// </summary>
    public static string ResolveItemDirectory(
        string destinationDirectory,
        string? relativeFolder)
    {
        string root = Path.GetFullPath(destinationDirectory);

        if (string.IsNullOrWhiteSpace(relativeFolder))
        {
            return root;
        }

        try
        {
            string combined =
                Path.GetFullPath(
                    Path.Combine(
                        root,
                        relativeFolder));

            string rootWithSeparator =
                root.EndsWith(
                    Path.DirectorySeparatorChar)
                    ? root
                    : root +
                        Path.DirectorySeparatorChar;

            if (
                combined.Equals(
                    root,
                    StringComparison.OrdinalIgnoreCase) ||
                combined.StartsWith(
                    rootWithSeparator,
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                return combined;
            }
        }
        catch (Exception exception)
            when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException
            )
        {
        }

        return root;
    }

    private BatchWorkState? ActiveBatch()
    {
        lock (_sync)
        {
            return _activeBatch;
        }
    }

    private bool StopItem(Guid itemId, ItemStop stop)
    {
        BatchWorkState? batch = ActiveBatch();

        if (batch is not null)
        {
            CancellationTokenSource? running = null;

            lock (batch.Sync)
            {
                if (batch.Active.TryGetValue(itemId, out ActiveItem? active))
                {
                    active.Stop = stop;
                    running = active.Cancellation;
                }
                else
                {
                    batch.Pending.Remove(itemId);
                }
            }

            if (running is not null)
            {
                try
                {
                    running.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // It finished in the meantime.
                }

                return true;
            }
        }

        // Not transferring right now: a queue-only state change.
        return stop == ItemStop.Pause && _queue.TryPause(itemId);
    }

    private async Task RunWorkerAsync(BatchWorkState state)
    {
        CancellationToken runToken = state.RunToken;

        while (!runToken.IsCancellationRequested)
        {
            Take take = TryTakeNextItem(state);

            if (take.Item is null)
            {
                if (take.Wake is null)
                {
                    return;
                }

                try
                {
                    await take.Wake
                        .WaitAsync(runToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await RunItemAsync(take.Item, take.Active!, state)
                    .ConfigureAwait(false);
            }
            finally
            {
                take.HostGate?.Release();

                lock (state.Sync)
                {
                    state.Active.Remove(take.Item.Id);
                }

                take.Active!.Cancellation.Dispose();
                SignalLaneFreed(state);
            }
        }
    }

    private Take TryTakeNextItem(BatchWorkState state)
    {
        // The queue's current order decides what runs next, so
        // "download next" and manual reordering apply mid-batch.
        IReadOnlyList<DownloadQueueItem> ordered = _queue.Items;
        DateTimeOffset now = _clock();
        DateTimeOffset? earliestRetry = null;

        lock (state.Sync)
        {
            foreach (DownloadQueueItem item in ordered)
            {
                if (!state.Pending.Contains(item.Id))
                {
                    continue;
                }

                if (!DownloadQueue.IsStartable(item.State))
                {
                    // Paused, or finished by another path: no
                    // longer this batch's work.
                    state.Pending.Remove(item.Id);
                    continue;
                }

                if (item.RetryAfterUtc is DateTimeOffset retryAt && retryAt > now)
                {
                    earliestRetry = earliestRetry is null || retryAt < earliestRetry
                        ? retryAt
                        : earliestRetry;
                    continue;
                }

                SemaphoreSlim? hostGate = GetHostGate(item.Url);

                // Wait(0) never blocks: it either takes the host's
                // lane now or reports it busy, so a busy single-lane
                // host never parks a worker while others wait.
                if (hostGate is not null && !hostGate.Wait(0))
                {
                    continue;
                }

                if (!_queue.TryMarkDownloading(item.Id))
                {
                    hostGate?.Release();
                    state.Pending.Remove(item.Id);
                    continue;
                }

                state.Pending.Remove(item.Id);
                state.Started.Add(item.Id);

                var active = new ActiveItem(
                    CancellationTokenSource.CreateLinkedTokenSource(
                        state.RunToken));

                state.Active[item.Id] = active;
                return new Take(item, hostGate, active, null);
            }

            // Items removed from the queue entirely.
            var present = new HashSet<Guid>(ordered.Select(item => item.Id));
            state.Pending.RemoveWhere(id => !present.Contains(id));

            if (state.Pending.Count == 0 && state.Active.Count == 0)
            {
                state.Closed = true;
                return new Take(null, null, null, null);
            }

            // Sleep until something frees a lane, an item is added,
            // or the earliest waiting item may start.
            Task wake = state.LaneFreed.Task;
            TimeSpan delay = MaximumIdleWait;

            if (earliestRetry is DateTimeOffset retry)
            {
                TimeSpan untilRetry = retry - now;
                delay = untilRetry < delay ? untilRetry : delay;
            }

            if (delay < TimeSpan.FromMilliseconds(50))
            {
                delay = TimeSpan.FromMilliseconds(50);
            }

            return new Take(
                null,
                null,
                null,
                Task.WhenAny(wake, Task.Delay(delay, state.RunToken)));
        }
    }

    private static void SignalLaneFreed(BatchWorkState state)
    {
        TaskCompletionSource freed;

        lock (state.Sync)
        {
            freed = state.LaneFreed;
            state.LaneFreed = NewLaneSignal();
        }

        freed.TrySetResult();
    }

    private static TaskCompletionSource NewLaneSignal()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task RunItemAsync(
        DownloadQueueItem item,
        ActiveItem active,
        BatchWorkState state)
    {
        CancellationToken itemToken = active.Cancellation.Token;

        string itemDirectory =
            ResolveItemDirectory(
                state.DestinationDirectory,
                item.RelativeFolder);

        try
        {
            var request = new DownloadTransferRequest(
                new Uri(item.Url, UriKind.Absolute),
                itemDirectory,
                state.MaxBytesPerSecond,
                state.ConnectionsPerDownload,
                PartialDownloadFiles.KeyFor(item.Id),
                item.PreferBrowser);

            var progress =
                new InlineProgress<
                    DownloadTransferProgress>(
                    update =>
                        _queue.TryReportProgress(
                            item.Id,
                            update));

            DownloadTransferResult result =
                await _transferService.DownloadAsync(
                        request,
                        progress,
                        itemToken)
                    .ConfigureAwait(false);

            itemToken.ThrowIfCancellationRequested();

            _queue.TryMarkCompleted(
                item.Id,
                result);

            Interlocked.Increment(
                ref state.CompletedCount);
        }
        catch (OperationCanceledException)
            when (itemToken.IsCancellationRequested)
        {
            ItemStop stop;

            lock (state.Sync)
            {
                stop = active.Stop;
            }

            if (stop == ItemStop.Pause && !state.RunToken.IsCancellationRequested)
            {
                _queue.TryMarkPaused(item.Id);
                return;
            }

            _queue.TryMarkCanceled(item.Id);

            Interlocked.Increment(
                ref state.CanceledCount);
        }
        catch (HostThrottledException throttled)
        {
            // Wait out the host's limit inside this batch; the
            // partial file stays, so nothing is downloaded twice.
            DownloadQueueItem? current = _queue.Find(item.Id);

            DateTimeOffset? retryAt =
                ThrottleRetryPlanner.NextAttempt(
                    throttled,
                    current?.AutoRetryCount ?? item.AutoRetryCount,
                    _clock());

            if (
                retryAt is DateTimeOffset next &&
                _queue.TryMarkWaiting(
                    item.Id,
                    next,
                    ThrottleRetryPlanner.DescribeWait(throttled, next)))
            {
                lock (state.Sync)
                {
                    state.Pending.Add(item.Id);
                }

                return;
            }

            _queue.TryMarkFailed(
                item.Id,
                GetFailureMessage(throttled));

            Interlocked.Increment(
                ref state.FailedCount);
        }
        catch (Exception exception)
        {
            _queue.TryMarkFailed(
                item.Id,
                GetFailureMessage(exception));

            Interlocked.Increment(
                ref state.FailedCount);

            if (
                exception is
                    ChallengeDetectedException challenge)
            {
                RaiseChallengeBlocked(challenge);
            }
        }
    }

    private void RaiseChallengeBlocked(
        ChallengeDetectedException challenge)
    {
        try
        {
            ChallengeBlocked?.Invoke(this, challenge);
        }
        catch
        {
            // UI subscribers must never break a batch.
        }
    }

    private SemaphoreSlim? GetHostGate(string url)
    {
        string? host = TryGetHost(url);

        if (!HostThrottlePolicy.IsSingleLaneHost(host))
        {
            return null;
        }

        return _hostGates.GetOrAdd(
            host!,
            _ => new SemaphoreSlim(1, 1));
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? uri.Host
            : null;
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
            return Path.GetFullPath(
                destinationDirectory);
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

    private static string GetFailureMessage(
        Exception exception)
    {
        const int maximumLength = 500;

        string message = string.IsNullOrWhiteSpace(
            exception.Message)
                ? "The download failed."
                : exception.Message.Trim();

        return message.Length <= maximumLength
            ? message
            : message[..maximumLength];
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    private void RaiseRunningStateChanged()
    {
        EventHandler? handlers = RunningStateChanged;

        if (handlers is null)
        {
            return;
        }

        foreach (
            Delegate subscriber in
            handlers.GetInvocationList()
        )
        {
            try
            {
                ((EventHandler)subscriber)(
                    this,
                    EventArgs.Empty);
            }
            catch
            {
                // Presentation observers cannot be allowed
                // to interrupt orchestration.
            }
        }
    }

    private enum ItemStop
    {
        None,
        Pause,
        Cancel
    }

    private sealed class ActiveItem
    {
        public ActiveItem(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }

        // Guarded by BatchWorkState.Sync.
        public ItemStop Stop { get; set; }
    }

    private sealed record Take(
        DownloadQueueItem? Item,
        SemaphoreSlim? HostGate,
        ActiveItem? Active,
        Task? Wake);

    private sealed class BatchWorkState
    {
        public BatchWorkState(
            string destinationDirectory,
            long? maxBytesPerSecond,
            int connectionsPerDownload,
            CancellationToken runToken)
        {
            DestinationDirectory = destinationDirectory;
            MaxBytesPerSecond = maxBytesPerSecond;
            ConnectionsPerDownload = connectionsPerDownload;
            RunToken = runToken;
        }

        public readonly object Sync = new();

        public string DestinationDirectory { get; }

        public long? MaxBytesPerSecond { get; }

        public int ConnectionsPerDownload { get; }

        public CancellationToken RunToken { get; }

        // All guarded by Sync.
        public readonly HashSet<Guid> Pending = new();
        public readonly HashSet<Guid> Started = new();
        public readonly Dictionary<Guid, ActiveItem> Active = new();
        public int SelectedCount;
        public bool Closed;

        // Completed (and replaced) whenever an item finishes or is
        // added, waking workers that found nothing to start.
        public TaskCompletionSource LaneFreed = NewLaneSignal();

        public int CompletedCount;
        public int FailedCount;
        public int CanceledCount;
    }

    private sealed class InlineProgress<T>
        : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report =
                report ??
                throw new ArgumentNullException(
                    nameof(report));
        }

        public void Report(T value)
        {
            _report(value);
        }
    }
}
