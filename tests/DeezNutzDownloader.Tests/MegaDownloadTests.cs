using CG.Web.MegaApiClient;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Adapters.Media;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class MegaLinksTests
{
    [Theory]
    [InlineData("https://mega.nz/file/AbC123#key456789")]
    [InlineData("https://mega.nz/folder/AbC123#key456789")]
    [InlineData("https://mega.io/file/AbC123#key456789")]
    public void IsLink_AcceptsKeyedLinks(string url)
    {
        Assert.True(MegaLinks.IsLink(new Uri(url)));
    }

    [Theory]
    [InlineData("https://mega.nz/file/AbC123")]
    [InlineData("https://mega.nz/file/AbC123#")]
    [InlineData("https://mega.nz/doc/help")]
    [InlineData("https://pixeldrain.com/u/AbC123")]
    [InlineData("https://evilmega.nz/file/AbC123#key")]
    public void IsLink_RejectsOthers(string url)
    {
        Assert.False(MegaLinks.IsLink(new Uri(url)));
    }

    [Fact]
    public void IsLink_Null_ReturnsFalse()
    {
        Assert.False(MegaLinks.IsLink(null));
    }
}

public sealed class MegaErrorMappingTests
{
    private static readonly Uri Link =
        new("https://mega.nz/file/AbC123#key456789");

    [Fact]
    public void MapApiError_Quota_StaysQueued()
    {
        HostThrottledException throttled =
            Assert.IsType<HostThrottledException>(
                MegaDownloadService.MapApiError(
                    Link,
                    ApiResultCode.QuotaExceeded,
                    "quota"));

        Assert.True(throttled.IsQuotaExhausted);
    }

    [Fact]
    public void MapApiError_RateLimit_StaysQueued()
    {
        Assert.IsType<HostThrottledException>(
            MegaDownloadService.MapApiError(
                Link,
                ApiResultCode.TooManyRequests,
                "slow"));
    }

    [Fact]
    public void MapApiError_GoneFile_SaysRemove()
    {
        InvalidOperationException failure =
            Assert.IsType<InvalidOperationException>(
                MegaDownloadService.MapApiError(
                    Link,
                    ApiResultCode.ResourceNotExists,
                    "gone"));

        Assert.Contains("remove it", failure.Message);
    }
}

public sealed class MegaTransferTests : IDisposable
{
    private readonly string _directory;

    public MegaTransferTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-mega-tests",
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
    public async Task Transfer_MegaFile_DecryptsToNamedFile()
    {
        var mega = new StubMega(
            new MegaFileMetadata("secret plans.pdf", 4, false),
            new byte[] { 9, 8, 7, 6 });

        var service = new HttpDownloadTransferService(
            new HttpClient(new UnusedHandler()),
            resolvers: null,
            megaDownloader: mega);

        DownloadTransferResult result =
            await service.DownloadAsync(
                new DownloadTransferRequest(
                    new Uri("https://mega.nz/file/AbC123#key456789"),
                    _directory));

        Assert.Equal("secret plans.pdf", result.FileName);
        Assert.Equal(
            new byte[] { 9, 8, 7, 6 },
            await File.ReadAllBytesAsync(result.DestinationPath));
    }

    [Fact]
    public async Task Transfer_MegaFolder_DownloadsIntoSubfolder()
    {
        var mega = new StubMega(
            new MegaFileMetadata("folder", 0, true),
            Array.Empty<byte>());

        var service = new HttpDownloadTransferService(
            new HttpClient(new UnusedHandler()),
            resolvers: null,
            megaDownloader: mega);

        DownloadTransferResult result =
            await service.DownloadAsync(
                new DownloadTransferRequest(
                    new Uri(
                        "https://mega.nz/folder/AbC123#key456789"),
                    _directory));

        Assert.Equal("folder", result.FileName);
        Assert.Equal(
            Path.Combine(_directory, "folder"),
            result.DestinationPath);
        Assert.True(Directory.Exists(result.DestinationPath));
    }

    private sealed class StubMega : IMegaDownloader
    {
        private readonly MegaFileMetadata _info;
        private readonly byte[] _bytes;

        public StubMega(MegaFileMetadata info, byte[] bytes)
        {
            _info = info;
            _bytes = bytes;
        }

        public Task<MegaFileMetadata> GetInfoAsync(
            Uri link,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_info);
        }

        public Task<string> DownloadToAsync(
            Uri link,
            string destinationPath,
            long? totalBytes,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, _bytes);

            progress?.Report(new DownloadTransferProgress(
                _bytes.Length,
                _bytes.Length));

            return Task.FromResult(destinationPath);
        }

        public Task<IReadOnlyList<MegaFolderFile>> ListFolderAsync(
            Uri link,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MegaFolderFile> empty =
                Array.Empty<MegaFolderFile>();

            return Task.FromResult(empty);
        }

        public Task<long> DownloadFolderToAsync(
            Uri link,
            string destinationDirectory,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0L);
        }
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "MEGA transfers must not touch HTTP.");
        }
    }
}
