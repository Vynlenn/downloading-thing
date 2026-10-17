using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;
using DeezNutzDownloader.Infrastructure.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class DownloadHistoryTests
{
    [Fact]
    public void RecordAndContains_MatchCaseInsensitively()
    {
        var history = new DownloadHistory();

        Assert.True(history.Record(new DownloadHistoryEntry(
            "https://pixeldrain.com/api/file/AbC?download",
            "clip.mp4",
            4,
            DateTimeOffset.UtcNow)));

        Assert.True(history.Contains(
            "HTTPS://pixeldrain.com/api/file/AbC?download"));

        // Duplicates are not recorded twice.
        Assert.False(history.Record(new DownloadHistoryEntry(
            "https://pixeldrain.com/api/file/AbC?download",
            "clip.mp4",
            4,
            DateTimeOffset.UtcNow)));

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Enqueue_SkipsHistoryUrls_AsAlreadyQueued()
    {
        var queue = new DownloadQueue();
        var history = new DownloadHistory();

        const string url = "https://files.example.com/f/1.zip";

        history.Record(new DownloadHistoryEntry(
            url, "1.zip", 10, DateTimeOffset.UtcNow));

        DownloadQueueImportResult result = queue.Enqueue(
            LinkAnalyzer.Analyze(url),
            history.Contains);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.AlreadyQueuedCount);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Enqueue_WithoutPredicate_BehavesAsBefore()
    {
        var queue = new DownloadQueue();

        DownloadQueueImportResult result = queue.Enqueue(
            LinkAnalyzer.Analyze("https://files.example.com/f/1.zip"));

        Assert.Equal(1, result.AddedCount);
    }

    [Fact]
    public async Task Store_RoundTrips_WithCap()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-history-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            var store = new DownloadHistoryStore(
                Path.Combine(directory, "history.json"));

            var history = new DownloadHistory();

            history.Record(new DownloadHistoryEntry(
                "https://a.test/1",
                "1.bin",
                7,
                DateTimeOffset.UtcNow));

            await store.SaveAsync(history);

            DownloadHistory loaded = await store.LoadAsync();

            Assert.True(loaded.Contains("https://a.test/1"));
            Assert.Equal(1, loaded.Count);

            Assert.True(File.Exists(
                Path.Combine(directory, "history.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }

    [Fact]
    public async Task Store_CorruptFile_ResetsToEmpty()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-history-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            string file = Path.Combine(directory, "history.json");
            await File.WriteAllTextAsync(file, "{{{nope");

            var store = new DownloadHistoryStore(file);

            DownloadHistory loaded = await store.LoadAsync();

            Assert.Equal(0, loaded.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }
}
