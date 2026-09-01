using System.Collections.Concurrent;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class DownloadOrchestratorTests
{
    [Fact]
    public async Task StartAllAsync_CompletesItemsAndStoresProgress()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/first.bin",
            "https://example.test/second.bin");

        var service = new DelegateTransferService(
            (request, progress, cancellationToken) =>
            {
                progress?.Report(
                    new DownloadTransferProgress(
                        40,
                        100));

                progress?.Report(
                    new DownloadTransferProgress(
                        100,
                        100));

                return Task.FromResult(
                    CreateResult(
                        request,
                        bytesWritten: 100));
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 2);

        Assert.Equal(2, result.SelectedCount);
        Assert.Equal(2, result.StartedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.CanceledCount);
        Assert.Equal(0, result.NotStartedCount);
        Assert.Equal(2, service.RequestCount);

        Assert.All(
            queue.Items,
            item =>
            {
                Assert.Equal(
                    DownloadQueueItemState.Completed,
                    item.State);

                Assert.Equal(100, item.BytesReceived);
                Assert.Equal<long?>(100, item.TotalBytes);
                Assert.Equal(100d, item.ProgressPercentage);
                Assert.NotNull(item.DestinationPath);
                Assert.Null(item.ErrorMessage);
            });
    }

    [Fact]
    public async Task StartAllAsync_RespectsMaximumConcurrency()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/one.bin",
            "https://example.test/two.bin",
            "https://example.test/three.bin",
            "https://example.test/four.bin",
            "https://example.test/five.bin",
            "https://example.test/six.bin");

        var firstPairEntered =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var release =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new ConcurrencyProbe();

        var service = new DelegateTransferService(
            async (
                request,
                progress,
                cancellationToken) =>
            {
                int callNumber = probe.Enter();

                try
                {
                    if (callNumber == 2)
                    {
                        firstPairEntered.TrySetResult(true);
                    }

                    await release.Task.WaitAsync(
                        cancellationToken);

                    return CreateResult(
                        request,
                        bytesWritten: 10);
                }
                finally
                {
                    probe.Exit();
                }
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        Task<DownloadBatchResult> run =
            orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 2);

        await firstPairEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, probe.CallCount);
        Assert.Equal(2, probe.MaximumActive);

        release.TrySetResult(true);

        DownloadBatchResult result =
            await run.WaitAsync(
                TimeSpan.FromSeconds(5));

        Assert.Equal(6, result.CompletedCount);
        Assert.Equal(6, probe.CallCount);
        Assert.Equal(2, probe.MaximumActive);
    }

    [Fact]
    public async Task StartAllAsync_RecordsFailureAndContinuesBatch()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/bad.bin",
            "https://example.test/good.bin");

        var service = new DelegateTransferService(
            (request, progress, cancellationToken) =>
            {
                if (
                    request.Source.AbsolutePath.EndsWith(
                        "bad.bin",
                        StringComparison.Ordinal)
                )
                {
                    return Task.FromException<
                        DownloadTransferResult>(
                        new HttpRequestException(
                            "simulated failure"));
                }

                return Task.FromResult(
                    CreateResult(
                        request,
                        bytesWritten: 25));
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 2);

        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.CanceledCount);

        DownloadQueueItem failed =
            queue.Items.Single(
                item =>
                    item.Url.EndsWith(
                        "/bad.bin",
                        StringComparison.Ordinal));

        DownloadQueueItem completed =
            queue.Items.Single(
                item =>
                    item.Url.EndsWith(
                        "/good.bin",
                        StringComparison.Ordinal));

        Assert.Equal(
            DownloadQueueItemState.Failed,
            failed.State);

        Assert.Equal(
            "simulated failure",
            failed.ErrorMessage);

        Assert.Equal(
            DownloadQueueItemState.Completed,
            completed.State);
    }

    [Fact]
    public async Task Cancel_StopsActiveItemAndLeavesPendingItemsQueued()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/one.bin",
            "https://example.test/two.bin",
            "https://example.test/three.bin");

        var transferEntered =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new DelegateTransferService(
            async (
                request,
                progress,
                cancellationToken) =>
            {
                transferEntered.TrySetResult(true);

                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);

                return CreateResult(
                    request,
                    bytesWritten: 1);
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        Task<DownloadBatchResult> run =
            orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 1);

        await transferEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.True(orchestrator.IsRunning);
        Assert.True(orchestrator.Cancel());

        DownloadBatchResult result =
            await run.WaitAsync(
                TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.StartedCount);
        Assert.Equal(1, result.CanceledCount);
        Assert.Equal(2, result.NotStartedCount);
        Assert.False(orchestrator.IsRunning);
        Assert.False(orchestrator.Cancel());

        Assert.Equal(
            DownloadQueueItemState.Canceled,
            queue.Items[0].State);

        Assert.All(
            queue.Items.Skip(1),
            item =>
                Assert.Equal(
                    DownloadQueueItemState.Queued,
                    item.State));
    }

    [Fact]
    public async Task StartAllAsync_TreatsUnrequestedCancellationAsFailure()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/timeout.bin");

        var service = new DelegateTransferService(
            (request, progress, cancellationToken) =>
                Task.FromException<DownloadTransferResult>(
                    new OperationCanceledException(
                        "simulated transport timeout")));

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 1);

        Assert.Equal(1, result.StartedCount);
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.CanceledCount);

        DownloadQueueItem failed =
            Assert.Single(queue.Items);

        Assert.Equal(
            DownloadQueueItemState.Failed,
            failed.State);

        Assert.Equal(
            "simulated transport timeout",
            failed.ErrorMessage);
    }

    [Fact]
    public async Task StartAllAsync_RetriesFailedButSkipsCompleted()
    {
        const string retryUrl =
            "https://example.test/retry.bin";

        const string stableUrl =
            "https://example.test/stable.bin";

        DownloadQueue queue = CreateQueue(
            retryUrl,
            stableUrl);

        var attempts =
            new ConcurrentDictionary<string, int>(
                StringComparer.Ordinal);

        var service = new DelegateTransferService(
            (request, progress, cancellationToken) =>
            {
                string key = request.Source.AbsoluteUri;

                int attempt = attempts.AddOrUpdate(
                    key,
                    1,
                    (_, current) => current + 1);

                if (
                    string.Equals(
                        key,
                        retryUrl,
                        StringComparison.Ordinal) &&
                    attempt == 1
                )
                {
                    return Task.FromException<
                        DownloadTransferResult>(
                        new IOException(
                            "first attempt failed"));
                }

                return Task.FromResult(
                    CreateResult(
                        request,
                        bytesWritten: 50));
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        DownloadBatchResult first =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 2);

        Assert.Equal(1, first.CompletedCount);
        Assert.Equal(1, first.FailedCount);

        DownloadBatchResult second =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 2);

        Assert.Equal(1, second.SelectedCount);
        Assert.Equal(1, second.CompletedCount);
        Assert.Equal(0, second.FailedCount);

        Assert.Equal(2, attempts[retryUrl]);
        Assert.Equal(1, attempts[stableUrl]);

        Assert.All(
            queue.Items,
            item =>
                Assert.Equal(
                    DownloadQueueItemState.Completed,
                    item.State));
    }

    [Fact]
    public async Task StartAllAsync_RejectsOverlappingBatches()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/blocked.bin");

        var transferEntered =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new DelegateTransferService(
            async (
                request,
                progress,
                cancellationToken) =>
            {
                transferEntered.TrySetResult(true);

                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);

                return CreateResult(
                    request,
                    bytesWritten: 1);
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        Task<DownloadBatchResult> firstRun =
            orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 1);

        await transferEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        try
        {
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => orchestrator.StartAllAsync(
                    CreateDestinationPath(),
                    maximumConcurrency: 1));
        }
        finally
        {
            orchestrator.Cancel();
        }

        DownloadBatchResult result =
            await firstRun.WaitAsync(
                TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.CanceledCount);
    }

    [Fact]
    public async Task ActiveItem_CannotBeRemovedOrCleared()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/active.bin");

        Guid itemId = queue.Items[0].Id;

        var transferEntered =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new DelegateTransferService(
            async (
                request,
                progress,
                cancellationToken) =>
            {
                transferEntered.TrySetResult(true);

                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);

                return CreateResult(
                    request,
                    bytesWritten: 1);
            });

        using var orchestrator =
            new DownloadOrchestrator(
                queue,
                service);

        Task<DownloadBatchResult> run =
            orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 1);

        await transferEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.False(queue.Remove(itemId));
        Assert.Equal(0, queue.Clear());
        Assert.Single(queue.Items);

        orchestrator.Cancel();

        await run.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.True(queue.Remove(itemId));
        Assert.Empty(queue.Items);
    }

    private static DownloadQueue CreateQueue(
        params string[] urls)
    {
        var queue = new DownloadQueue();

        DownloadQueueImportResult result =
            queue.Enqueue(
                LinkAnalyzer.Analyze(
                    string.Join(
                        Environment.NewLine,
                        urls)));

        Assert.Equal(urls.Length, result.AddedCount);

        return queue;
    }

    private static string CreateDestinationPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "DeezNutzDownloader.Tests",
            Guid.NewGuid().ToString("N"));
    }

    private static DownloadTransferResult CreateResult(
        DownloadTransferRequest request,
        long bytesWritten)
    {
        string fileName = Path.GetFileName(
            request.Source.AbsolutePath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download";
        }

        return new DownloadTransferResult(
            request.Source,
            Path.Combine(
                request.DestinationDirectory,
                fileName),
            fileName,
            bytesWritten,
            bytesWritten);
    }

    private sealed class DelegateTransferService
        : IDownloadTransferService
    {
        private readonly Func<
            DownloadTransferRequest,
            IProgress<DownloadTransferProgress>?,
            CancellationToken,
            Task<DownloadTransferResult>> _download;

        private int _requestCount;

        public DelegateTransferService(
            Func<
                DownloadTransferRequest,
                IProgress<DownloadTransferProgress>?,
                CancellationToken,
                Task<DownloadTransferResult>> download)
        {
            _download = download;
        }

        public int RequestCount =>
            Volatile.Read(ref _requestCount);

        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);

            return _download(
                request,
                progress,
                cancellationToken);
        }
    }

    private sealed class ConcurrencyProbe
    {
        private int _active;
        private int _maximumActive;
        private int _callCount;

        public int MaximumActive =>
            Volatile.Read(ref _maximumActive);

        public int CallCount =>
            Volatile.Read(ref _callCount);

        public int Enter()
        {
            int callNumber =
                Interlocked.Increment(ref _callCount);

            int active =
                Interlocked.Increment(ref _active);

            while (true)
            {
                int observedMaximum =
                    Volatile.Read(ref _maximumActive);

                if (active <= observedMaximum)
                {
                    break;
                }

                if (
                    Interlocked.CompareExchange(
                        ref _maximumActive,
                        active,
                        observedMaximum) ==
                    observedMaximum
                )
                {
                    break;
                }
            }

            return callNumber;
        }

        public void Exit()
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
