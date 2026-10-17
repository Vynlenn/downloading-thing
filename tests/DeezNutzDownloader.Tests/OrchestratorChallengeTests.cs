using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class OrchestratorChallengeTests : IDisposable
{
    private readonly string _directory;

    public OrchestratorChallengeTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-orch-challenge-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    [Fact]
    public async Task ChallengeFailure_RaisesEventAndMarksFailed()
    {
        DownloadQueue queue = CreateQueue(
            "https://files.example.com/f/1.bin");

        var seen = new List<ChallengeDetectedException>();

        using var orchestrator = new DownloadOrchestrator(
            queue,
            new ThrowingTransferService());

        orchestrator.ChallengeBlocked += (_, challenge) =>
        {
            lock (seen)
            {
                seen.Add(challenge);
            }
        };

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(_directory, 1);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.CompletedCount);

        ChallengeDetectedException raised = Assert.Single(seen);
        Assert.Equal(ChallengeKind.Captcha, raised.Kind);
        Assert.Equal("files.example.com", raised.Host);
    }

    [Fact]
    public async Task OrdinaryFailure_RaisesNoEvent()
    {
        DownloadQueue queue = CreateQueue(
            "https://files.example.com/f/1.bin");

        int events = 0;

        using var orchestrator = new DownloadOrchestrator(
            queue,
            new FailingTransferService());

        orchestrator.ChallengeBlocked += (_, _) =>
            Interlocked.Increment(ref events);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(_directory, 1);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, events);
    }

    private static DownloadQueue CreateQueue(params string[] urls)
    {
        var queue = new DownloadQueue();

        DownloadQueueImportResult result =
            queue.Enqueue(
                LinkAnalyzer.Analyze(
                    string.Join(Environment.NewLine, urls)));

        Assert.Equal(urls.Length, result.AddedCount);

        return queue;
    }

    private sealed class ThrowingTransferService
        : IDownloadTransferService
    {
        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new ChallengeDetectedException(
                request.Source.Host,
                ChallengeKind.Captcha,
                request.Source.AbsoluteUri,
                "CAPTCHA");
        }
    }

    private sealed class FailingTransferService
        : IDownloadTransferService
    {
        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException("boom");
        }
    }
}
