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
