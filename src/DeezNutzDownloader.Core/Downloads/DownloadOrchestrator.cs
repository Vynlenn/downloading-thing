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

public sealed class DownloadOrchestrator : IDisposable
{
    private const int MaximumSupportedConcurrency = 64;

    private readonly object _sync = new();
    private readonly DownloadQueue _queue;
    private readonly IDownloadTransferService _transferService;

    private CancellationTokenSource? _runCancellation;
    private readonly ConcurrentDictionary<string, SemaphoreSlim>
        _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public DownloadOrchestrator(
        DownloadQueue queue,
        IDownloadTransferService transferService)
    {
        _queue =
            queue ??
            throw new ArgumentNullException(nameof(queue));

        _transferService =
            transferService ??
            throw new ArgumentNullException(
                nameof(transferService));
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
        long? maxBytesPerSecond = null)
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
        }

        RaiseRunningStateChanged();

        try
        {
            IReadOnlyList<DownloadQueueItem> selectedItems =
                _queue.GetStartableItems();

            if (selectedItems.Count == 0)
            {
                return new DownloadBatchResult(
                    SelectedCount: 0,
                    StartedCount: 0,
                    CompletedCount: 0,
                    FailedCount: 0,
                    CanceledCount: 0,
                    NotStartedCount: 0);
            }

            var state = new BatchWorkState();

            int workerCount = Math.Min(
                maximumConcurrency,
                selectedItems.Count);

            var workers = new Task[workerCount];

            for (
                int workerIndex = 0;
                workerIndex < workerCount;
                workerIndex++
            )
            {
                workers[workerIndex] = RunWorkerAsync(
                    selectedItems,
                    normalizedDestination,
                    state,
                    runCancellation.Token,
                    maxBytesPerSecond);
            }

            await Task.WhenAll(workers)
                .ConfigureAwait(false);

            int startedCount =
                Volatile.Read(ref state.StartedCount);

            return new DownloadBatchResult(
                SelectedCount: selectedItems.Count,
                StartedCount: startedCount,
                CompletedCount:
                    Volatile.Read(
                        ref state.CompletedCount),
                FailedCount:
                    Volatile.Read(
                        ref state.FailedCount),
                CanceledCount:
                    Volatile.Read(
                        ref state.CanceledCount),
                NotStartedCount:
                    selectedItems.Count - startedCount);
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

    private async Task RunWorkerAsync(
        IReadOnlyList<DownloadQueueItem> selectedItems,
        string destinationDirectory,
        BatchWorkState state,
        CancellationToken cancellationToken,
        long? maxBytesPerSecond = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int itemIndex = Interlocked.Increment(
                ref state.NextItemIndex);

            if (itemIndex >= selectedItems.Count)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DownloadQueueItem item =
                selectedItems[itemIndex];

            // Free file hosts (Pixeldrain, Gofile, MEGA, …)
            // throttle per IP: one active transfer per host.
            SemaphoreSlim? hostGate = GetHostGate(item.Url);

            if (hostGate is not null)
            {
                try
                {
                    await hostGate.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (!_queue.TryMarkDownloading(item.Id))
            {
                hostGate?.Release();
                continue;
            }

            Interlocked.Increment(
                ref state.StartedCount);

            string itemDirectory =
                ResolveItemDirectory(
                    destinationDirectory,
                    item.RelativeFolder);

            try
            {
                var request = new DownloadTransferRequest(
                    new Uri(item.Url, UriKind.Absolute),
                    itemDirectory,
                    maxBytesPerSecond);

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
                            cancellationToken)
                        .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                _queue.TryMarkCompleted(
                    item.Id,
                    result);

                Interlocked.Increment(
                    ref state.CompletedCount);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _queue.TryMarkCanceled(item.Id);

                Interlocked.Increment(
                    ref state.CanceledCount);
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

            hostGate?.Release();
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

    private static string ResolveItemDirectory(
        string destinationDirectory,
        string? relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder))
        {
            return destinationDirectory;
        }

        try
        {
            string combined =
                Path.GetFullPath(
                    Path.Combine(
                        destinationDirectory,
                        relativeFolder));

            string root =
                destinationDirectory.EndsWith(
                    Path.DirectorySeparatorChar)
                    ? destinationDirectory
                    : destinationDirectory +
                        Path.DirectorySeparatorChar;

            if (
                combined.Equals(
                    destinationDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                combined.StartsWith(
                    root,
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

        return destinationDirectory;
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

    private sealed class BatchWorkState
    {
        public int NextItemIndex = -1;
        public int StartedCount;
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
