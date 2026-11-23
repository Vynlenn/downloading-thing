using System.Net;
using System.Text;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class BunkrTests : IDisposable
{
    // Same structure as a real album grid (names invented).
    private const string AlbumHtml = """
        <html><head>
        <meta property="og:title" content="Holiday Clips | Bunkr">
        </head><body>
        <h1 class="truncate">Holiday Clips</h1>
        <div class="relative group/item theItem" title="beach day.mp4">
          <div><p class="truncate theName text-center">beach day.mp4</p></div>
          <p class="text-xs theSize mb-1">42.76 MB</p>
          <a class="after:absolute" href="/f/AbC123xyz" aria-label="download"></a>
        </div>
        <div class="relative group/item theItem" title="sunset.jpg">
          <div><p class="truncate theName text-center">sunset &amp; sea.jpg</p></div>
          <p class="text-xs theSize mb-1">812 KB</p>
          <a class="after:absolute" href="/f/Zz9_q-1" aria-label="download"></a>
        </div>
        <div class="relative group/item theItem" title="folder/../trick.mp4">
          <p class="truncate theName text-center">folder/../trick.mp4</p>
          <p class="text-xs theSize mb-1">1.5 GB</p>
          <a href="/f/Trick000"></a>
        </div>
        <a href="?page=2">2</a><a href="?page=3">3</a>
        <script>var t = '<a href="/f/' + file.slug + '">';</script>
        </body></html>
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-bunkr-tests",
        Guid.NewGuid().ToString("N"));

    public BunkrTests()
    {
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

    [Theory]
    [InlineData("https://bunkr.cr/a/aX69bvxo", true, false)]
    [InlineData("https://bunkr.si/a/aX69bvxo", true, false)]
    [InlineData("https://bunkrr.su/a/aX69bvxo", true, false)]
    [InlineData("https://bunkr.cr/f/1jWCVZH7s6IA4", false, true)]
    [InlineData("https://bunkr.cr/v/legacy", false, true)]
    [InlineData("https://bunkr.cr/a/aX69bvxo/extra", false, false)]
    [InlineData("https://notbunkr.cr/a/aX69bvxo", false, false)]
    [InlineData("https://bunkr.cr.evil.test/f/x", false, false)]
    public void Links_RecognizeAlbumsAndFilePages(string url, bool album, bool file)
    {
        var uri = new Uri(url);
        Assert.Equal(album, BunkrLinks.IsAlbum(uri));
        Assert.Equal(file, BunkrLinks.IsFilePage(uri));
    }

    [Fact]
    public void BunkrIsOneFileAtATime()
    {
        Assert.True(HostThrottlePolicy.IsSingleLaneHost("bunkr.cr"));
        Assert.True(HostThrottlePolicy.IsSingleLaneHost("bunkrr.su"));
    }

    [Fact]
    public void Parser_ReadsNamesSizesTitleAndPages()
    {
        BunkrAlbum album = BunkrAlbumParser.Parse(AlbumHtml, new Uri("https://bunkr.cr/a/abc"));

        Assert.Equal("Holiday Clips", album.Title);
        Assert.Equal(3, album.Files.Count);

        Assert.Equal("https://bunkr.cr/f/AbC123xyz", album.Files[0].Page.AbsoluteUri);
        Assert.Equal("beach day.mp4", album.Files[0].Name);
        Assert.Equal((long)Math.Round(42.76 * 1024 * 1024), album.Files[0].SizeBytes);

        Assert.Equal("sunset & sea.jpg", album.Files[1].Name);
        Assert.Equal(812 * 1024, album.Files[1].SizeBytes);

        // Names are labels, never paths.
        Assert.DoesNotContain("/", album.Files[2].Name);

        Assert.Equal(new[] { 2, 3 }, album.OtherPages);
    }

    [Theory]
    [InlineData("42.76 MB", 44_837_110L)]
    [InlineData("1 GB", 1_073_741_824L)]
    [InlineData("900 B", 900L)]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    public void Parser_ParsesSizes(string text, long? expected)
    {
        Assert.Equal(expected, BunkrAlbumParser.ParseSize(text));
    }

    [Fact]
    public async Task Resolver_ExpandsAlbumAcrossPagesWithoutDuplicates()
    {
        var requested = new List<string>();

        var client = new HttpClient(new Respond(request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);

            string html = request.RequestUri.Query == "?page=3"
                ? """<div class="theItem"><p class="theName">late.mp4</p><a href="/f/Late001"></a></div>"""
                : AlbumHtml;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        }));

        ResolvedHostFolder folder = await new BunkrAlbumResolver()
            .ResolveFolderAsync(new Uri("https://bunkr.cr/a/abc"), client);

        Assert.Equal("Holiday Clips", folder.FolderName);
        Assert.Equal(4, folder.Files.Count);
        Assert.Contains(folder.Files, file => file.SuggestedFileName == "late.mp4");
        Assert.Equal(new[] { "/a/abc", "/a/abc?page=2", "/a/abc?page=3" }, requested);
    }

    [Fact]
    public async Task Resolver_EmptyOrMissingAlbumFailsClearly()
    {
        var empty = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>")
        }));

        var missing = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        InvalidOperationException none = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BunkrAlbumResolver().ResolveFolderAsync(new Uri("https://bunkr.cr/a/x"), empty));
        Assert.Contains("lists no files", none.Message);

        InvalidOperationException gone = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BunkrAlbumResolver().ResolveFolderAsync(new Uri("https://bunkr.cr/a/x"), missing));
        Assert.Contains("removed", gone.Message);
    }

    [Fact]
    public async Task FilePage_GoesThroughBrowserAndIsFinishedLikeAnyDownload()
    {
        int httpRequests = 0;
        var browser = new FakeBrowser(Png());

        var service = new HttpDownloadTransferService(
                new HttpClient(new Respond(_ =>
                {
                    httpRequests++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                })))
            .WithBrowserDownloader(browser);

        DownloadTransferResult result = await service.DownloadAsync(
            new DownloadTransferRequest(new Uri("https://bunkr.cr/f/AbC123xyz"), _directory));

        Assert.Equal(0, httpRequests);
        Assert.Equal(new Uri("https://bunkr.cr/f/AbC123xyz"), browser.LastPage);

        // Spoofing characters stripped, extension from the bytes,
        // temp file gone, Mark of the Web applied.
        Assert.Equal("holidayexe.png", result.FileName);
        Assert.True(File.Exists(result.DestinationPath));
        Assert.Single(Directory.GetFiles(_directory));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(MarkOfTheWeb.IsMarked(result.DestinationPath));
        }
    }

    [Fact]
    public async Task FilePage_WithoutBrowser_FailsClearly()
    {
        var service = new HttpDownloadTransferService(new HttpClient(new Respond(_ =>
            new HttpResponseMessage(HttpStatusCode.OK))));

        NotAFileException failure = await Assert.ThrowsAsync<NotAFileException>(
            () => service.DownloadAsync(
                new DownloadTransferRequest(new Uri("https://bunkr.cr/f/AbC123xyz"), _directory)));

        Assert.Contains("built-in browser", failure.Message);
    }

    [Fact]
    public async Task AlbumReachingTransfer_ExplainsExpansion()
    {
        var service = new HttpDownloadTransferService(new HttpClient(new Respond(_ =>
            new HttpResponseMessage(HttpStatusCode.OK))));

        NotAFileException failure = await Assert.ThrowsAsync<NotAFileException>(
            () => service.DownloadAsync(
                new DownloadTransferRequest(new Uri("https://bunkr.cr/a/abc"), _directory)));

        Assert.Contains("album", failure.Message);
    }

    private static byte[] Png() =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };

    private sealed class FakeBrowser : IBrowserDownloader
    {
        private readonly byte[] _content;

        public FakeBrowser(byte[] content)
        {
            _content = content;
        }

        public Uri? LastPage { get; private set; }

        public async Task<BrowserDownloadResult> DownloadAsync(
            Uri page,
            string destinationDirectory,
            string? displayName,
            IProgress<DownloadTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastPage = page;
            string temp = Path.Combine(destinationDirectory, ".download-test.browser");
            await File.WriteAllBytesAsync(temp, _content, cancellationToken);

            // A hostile suggested name: bidi override + no extension.
            return new BrowserDownloadResult(temp, "holiday‮exe", _content.Length, null);
        }
    }

    private sealed class Respond : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = _respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
