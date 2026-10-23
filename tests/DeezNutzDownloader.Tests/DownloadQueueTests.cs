using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class DownloadQueueTests
{
    [Fact]
    public void Enqueue_AddsOnlyValidUniqueLinks()
    {
        LinkAnalysisResult analysis = LinkAnalyzer.Analyze(
            "HTTPS://EXAMPLE.TEST:443/files/archive.zip#first\n" +
            "https://example.test/files/archive.zip#second\n" +
            "not a url");

        var queue = new DownloadQueue();

        DownloadQueueImportResult result =
            queue.Enqueue(analysis);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.AlreadyQueuedCount);
        Assert.Equal(2, result.IgnoredCount);
        Assert.Equal(1, queue.Count);

        Assert.Equal(
            "https://example.test/files/archive.zip",
            queue.Items[0].Url);

        Assert.Equal(
            "archive.zip",
            queue.Items[0].DisplayName);
    }

    [Fact]
    public void Enqueue_PreventsDuplicatesAcrossImports()
    {
        var queue = new DownloadQueue();

        DownloadQueueImportResult firstResult =
            queue.Enqueue(
                LinkAnalyzer.Analyze(
                    "HTTPS://EXAMPLE.TEST:443/file.zip#one"));

        DownloadQueueImportResult secondResult =
            queue.Enqueue(
                LinkAnalyzer.Analyze(
                    "https://example.test/file.zip#two"));

        Assert.Equal(1, firstResult.AddedCount);
        Assert.Equal(0, secondResult.AddedCount);
        Assert.Equal(1, secondResult.AlreadyQueuedCount);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Enqueue_PreservesInputOrderAndCreatesDisplayNames()
    {
        var queue = new DownloadQueue();

        queue.Enqueue(
            LinkAnalyzer.Analyze(
                "https://example.test/files/first.zip\n" +
                "https://example.test/files/second%20file.bin"));

        Assert.Collection(
            queue.Items,
            first =>
            {
                Assert.Equal("first.zip", first.DisplayName);
                Assert.Equal("Queued", first.Status);
            },
            second =>
            {
                Assert.Equal(
                    "second file.bin",
                    second.DisplayName);

                Assert.Equal("Queued", second.Status);
            });
    }

    [Fact]
    public void Remove_DeletesOnlyTheRequestedItem()
    {
        var queue = new DownloadQueue();

        queue.Enqueue(
            LinkAnalyzer.Analyze(
                "https://example.test/first.zip\n" +
                "https://example.test/second.zip"));

        Guid firstId = queue.Items[0].Id;
        Guid secondId = queue.Items[1].Id;

        Assert.True(queue.Remove(firstId));
        Assert.False(queue.Remove(firstId));

        Assert.Single(queue.Items);
        Assert.Equal(secondId, queue.Items[0].Id);
    }

    [Fact]
    public void TryResetForRetry_RequeuesFailedItems()
    {
        Guid failedId = Guid.NewGuid();
        Guid queuedId = Guid.NewGuid();

        var queue = new DownloadQueue();

        queue.RestoreSnapshot(
            new[]
            {
                new DownloadQueueItem(
                    failedId,
                    "https://example.test/failed.zip",
                    "failed.zip",
                    DownloadQueueItemState.Failed,
                    100,
                    200,
                    null,
                    null,
                    "boom"),
                new DownloadQueueItem(
                    queuedId,
                    "https://example.test/waiting.zip",
                    "waiting.zip")
            });

        Assert.False(queue.TryResetForRetry(queuedId));
        Assert.False(queue.TryResetForRetry(Guid.NewGuid()));
        Assert.True(queue.TryResetForRetry(failedId));

        DownloadQueueItem retried =
            Assert.Single(
                queue.Items,
                item => item.Id == failedId);

        Assert.Equal(
            DownloadQueueItemState.Queued,
            retried.State);
        Assert.Null(retried.ErrorMessage);
        Assert.Equal(0, retried.BytesReceived);
        Assert.Null(retried.TotalBytes);
        Assert.True(retried.CanRemove);
        Assert.False(retried.CanRetry);
    }

    [Fact]
    public void ClearCompleted_RemovesOnlyFinishedItems()
    {
        var queue = new DownloadQueue();

        queue.RestoreSnapshot(
            new[]
            {
                new DownloadQueueItem(
                    Guid.NewGuid(),
                    "https://example.test/failed.zip",
                    "failed.zip",
                    DownloadQueueItemState.Failed,
                    100,
                    200,
                    null,
                    null,
                    "network error"),
                new DownloadQueueItem(
                    Guid.NewGuid(),
                    "https://example.test/canceled.zip",
                    "canceled.zip",
                    DownloadQueueItemState.Canceled),
                new DownloadQueueItem(
                    Guid.NewGuid(),
                    "https://example.test/waiting.zip",
                    "waiting.zip")
            });

        int removed = queue.ClearCompleted();

        Assert.Equal(2, removed);

        DownloadQueueItem remaining =
            Assert.Single(queue.Items);

        Assert.Equal(
            "https://example.test/waiting.zip",
            remaining.Url);
        Assert.Equal(
            DownloadQueueItemState.Queued,
            remaining.State);
    }

    [Fact]
    public void RestoreSnapshot_NormalizesActiveItemsToQueued()
    {
        var queue = new DownloadQueue();

        var snapshot = new[]
        {
            new DownloadQueueItem(
                Guid.NewGuid(),
                "https://example.test/active.zip",
                "active.zip",
                DownloadQueueItemState.Downloading,
                1234,
                5678),
            new DownloadQueueItem(
                Guid.NewGuid(),
                "https://example.test/failed.zip",
                "failed.zip",
                DownloadQueueItemState.Failed,
                100,
                200,
                null,
                null,
                "network error"),
            new DownloadQueueItem(
                Guid.Empty,
                "not a url",
                "bad")
        };

        queue.RestoreSnapshot(snapshot);

        Assert.Equal(2, queue.Count);
        Assert.Equal(
            DownloadQueueItemState.Queued,
            queue.Items[0].State);
        Assert.Equal(0, queue.Items[0].BytesReceived);
        Assert.Null(queue.Items[0].TotalBytes);
        Assert.Equal("network error", queue.Items[1].ErrorMessage);
    }

    [Fact]
    public void ProgressText_IncludesSpeedAndEta()
    {
        var item = new DownloadQueueItem(
            Guid.NewGuid(),
            "https://example.test/file.zip",
            "file.zip",
            DownloadQueueItemState.Downloading,
            50 * 1024 * 1024,
            100 * 1024 * 1024,
            null,
            null,
            null,
            1024 * 1024);

        Assert.Contains("/s", item.ProgressText);
        Assert.Contains("left", item.ProgressText);
        Assert.NotNull(item.EstimatedRemainingText);
    }

    [Fact]
    public void Clear_RemovesEveryQueuedItem()
    {
        var queue = new DownloadQueue();

        queue.Enqueue(
            LinkAnalyzer.Analyze(
                "https://example.test/first.zip\n" +
                "https://example.test/second.zip"));

        int removedCount = queue.Clear();

        Assert.Equal(2, removedCount);
        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.Count);
    }
}
