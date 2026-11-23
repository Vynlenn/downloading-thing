using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class PixeldrainGateTests
{
    [Fact]
    public async Task StartAllAsync_SingleLaneHost_AllowsOneAtATime()
    {
        DownloadQueue queue = CreateQueue(
            "https://pixeldrain.com/u/one",
            "https://pixeldrain.com/u/two",
            "https://pixeldrain.com/u/three",
            "https://pixeldrain.com/u/four");

        var probe = new ConcurrencyProbe(TimeSpan.FromMilliseconds(150));

        using var orchestrator =
            new DownloadOrchestrator(queue, probe);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 4);

        Assert.Equal(4, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, probe.MaximumActive);
    }

    [Fact]
    public async Task StartAllAsync_RegularHost_StillParallelizes()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/a.bin",
            "https://example.test/b.bin",
            "https://example.test/c.bin",
            "https://example.test/d.bin");

        var probe = new ConcurrencyProbe(TimeSpan.FromMilliseconds(150));

        using var orchestrator =
            new DownloadOrchestrator(queue, probe);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(
                CreateDestinationPath(),
                maximumConcurrency: 4);

        Assert.Equal(4, result.CompletedCount);
        Assert.True(probe.MaximumActive > 1);
    }

    [Fact]
    public async Task StartAllAsync_ThrottledFailure_RetriesThenStaysRetryable()
    {
        DownloadQueue queue = CreateQueue(
            "https://pixeldrain.com/u/quota-hit");

        int attempts = 0;

        var service = new DelegateService(
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);

                return Task.FromException<DownloadTransferResult>(
                    new HostThrottledException(
                        "pixeldrain.com",
                        "pixeldrain.com free quota is exhausted.",
                        isQuotaExhausted: true));
            });

        // Each clock read jumps a day ahead, so every host-limit
        // wait has already passed when the worker looks again.
        long ticks = DateTimeOffset.UtcNow.UtcTicks;
        DateTimeOffset FastClock() =>
            new(Interlocked.Add(ref ticks, TimeSpan.TicksPerDay), TimeSpan.Zero);

        using var orchestrator =
            new DownloadOrchestrator(queue, service, FastClock);

        DownloadBatchResult result =
            await orchestrator
                .StartAllAsync(
                    CreateDestinationPath(),
                    maximumConcurrency: 2)
                .WaitAsync(TimeSpan.FromSeconds(30));

        // First try plus every automatic retry, then a failure
        // the user can still retry by hand.
        Assert.Equal(ThrottleRetryPlanner.MaximumAutomaticRetries + 1, attempts);
        Assert.Equal(1, result.FailedCount);

        DownloadQueueItem item = Assert.Single(queue.Items);

        Assert.Equal(DownloadQueueItemState.Failed, item.State);
        Assert.True(item.CanRetry);
        Assert.Contains("quota", item.ErrorMessage);
    }

    [Fact]
    public async Task StartAllAsync_HostLimit_WaitsThenCompletesInSameBatch()
    {
        DownloadQueue queue = CreateQueue(
            "https://pixeldrain.com/u/limited");

        int attempts = 0;

        var service = new DelegateService(
            (request, _, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return Task.FromException<DownloadTransferResult>(
                        new HostThrottledException(
                            "pixeldrain.com",
                            "Rate limited.",
                            retryAfter: TimeSpan.FromMilliseconds(1)));
                }

                return Task.FromResult(
                    new DownloadTransferResult(
                        request.Source,
                        Path.Combine(request.DestinationDirectory, "f.bin"),
                        "f.bin",
                        1,
                        1));
            });

        long ticks = DateTimeOffset.UtcNow.UtcTicks;
        DateTimeOffset FastClock() =>
            new(Interlocked.Add(ref ticks, TimeSpan.TicksPerMinute), TimeSpan.Zero);

        using var orchestrator =
            new DownloadOrchestrator(queue, service, FastClock);

        DownloadBatchResult result =
            await orchestrator
                .StartAllAsync(CreateDestinationPath(), maximumConcurrency: 1)
                .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, attempts);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(
            DownloadQueueItemState.Completed,
            Assert.Single(queue.Items).State);
    }

    private static DownloadQueue CreateQueue(params string[] urls)
    {
        var queue = new DownloadQueue();

        DownloadQueueImportResult import =
            queue.Enqueue(
                LinkAnalyzer.Analyze(
                    string.Join(Environment.NewLine, urls)));

        Assert.Equal(urls.Length, import.AddedCount);
        return queue;
    }

    private static string CreateDestinationPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "DeezNutzDownloader.Tests",
            Guid.NewGuid().ToString("N"));
    }

    private sealed class ConcurrencyProbe : IDownloadTransferService
    {
        private readonly TimeSpan _work;
        private int _active;
        private int _maximumActive;

        public ConcurrencyProbe(TimeSpan work)
        {
            _work = work;
        }

        public int MaximumActive =>
            Volatile.Read(ref _maximumActive);

        public async Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            int active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);

            try
            {
                await Task.Delay(_work, cancellationToken)
                    .ConfigureAwait(false);

                string fileName =
                    Path.GetFileName(request.Source.AbsolutePath);

                return new DownloadTransferResult(
                    request.Source,
                    Path.Combine(
                        request.DestinationDirectory,
                        fileName),
                    fileName,
                    0,
                    null);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            int current = Volatile.Read(ref _maximumActive);

            while (
                active > current &&
                Interlocked.CompareExchange(
                    ref _maximumActive,
                    active,
                    current) != current)
            {
                current = Volatile.Read(ref _maximumActive);
            }
        }
    }

    private sealed class DelegateService : IDownloadTransferService
    {
        private readonly Func<
            DownloadTransferRequest,
            IProgress<DownloadTransferProgress>?,
            CancellationToken,
            Task<DownloadTransferResult>> _download;

        public DelegateService(
            Func<
                DownloadTransferRequest,
                IProgress<DownloadTransferProgress>?,
                CancellationToken,
                Task<DownloadTransferResult>> download)
        {
            _download = download;
        }

        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return _download(request, progress, cancellationToken);
        }
    }
}
