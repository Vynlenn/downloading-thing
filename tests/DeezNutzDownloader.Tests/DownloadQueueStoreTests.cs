using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Infrastructure.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class DownloadQueueStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsItems()
    {
        string queueFile = Path.Combine(
            Path.GetTempPath(),
            $"deeznutz-queue-{Guid.NewGuid():N}.json");

        try
        {
            var store = new DownloadQueueStore(queueFile);

            var items = new[]
            {
                new DownloadQueueItem(
                    Guid.NewGuid(),
                    "https://example.test/first.zip",
                    "first.zip"),
                new DownloadQueueItem(
                    Guid.NewGuid(),
                    "https://example.test/second.zip",
                    "second.zip",
                    DownloadQueueItemState.Failed,
                    128,
                    1024,
                    null,
                    null,
                    "network error")
            };

            await store.SaveAsync(items);

            IReadOnlyList<DownloadQueueItem> loaded =
                await store.LoadAsync();

            Assert.Equal(2, loaded.Count);
            Assert.Equal(items[0].Id, loaded[0].Id);
            Assert.Equal(items[0].Url, loaded[0].Url);
            Assert.Equal("network error", loaded[1].ErrorMessage);
        }
        finally
        {
            if (File.Exists(queueFile))
            {
                File.Delete(queueFile);
            }
        }
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmpty()
    {
        string queueFile = Path.Combine(
            Path.GetTempPath(),
            $"deeznutz-queue-{Guid.NewGuid():N}.json");

        var store = new DownloadQueueStore(queueFile);

        IReadOnlyList<DownloadQueueItem> loaded =
            await store.LoadAsync();

        Assert.Empty(loaded);
    }
}
