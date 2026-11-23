using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

/// <summary>
/// What counts as a finished download: web pages are errors, files
/// get real extensions, keyless MEGA links are refused, and the
/// Pixeldrain key only ever reaches Pixeldrain.
/// </summary>
public sealed class DownloadOutcomeTests : IDisposable
{
    private static readonly byte[] Png =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 1, 64, 0, 0, 0, 180
    };

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-outcome-tests",
        Guid.NewGuid().ToString("N"));

    public DownloadOutcomeTests()
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

    // ---------- Web pages are not downloads ----------

    [Fact]
    public async Task HtmlPage_FailsAndLeavesNothingBehind()
    {
        var service = Service(_ => Html("<html><body>Album</body></html>"));
        string key = PartialDownloadFiles.KeyFor(Guid.NewGuid());

        NotAFileException failure = await Assert.ThrowsAsync<NotAFileException>(
            () => service.DownloadAsync(
                new DownloadTransferRequest(
                    new Uri("https://album.example.test/a/aX69bvxo"),
                    _directory,
                    ResumeKey: key)));

        Assert.Contains("web page, not a file", failure.Message);
        Assert.Contains("Crawl page", failure.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task HtmlOfferedAsAttachment_StillDownloads()
    {
        var service = Service(_ =>
        {
            HttpResponseMessage response = Html("<html>saved page</html>");
            response.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = "\"page.html\"" };
            return response;
        });

        DownloadTransferResult result = await service.DownloadAsync(
            new DownloadTransferRequest(new Uri("https://x.test/export"), _directory));

        Assert.Equal("page.html", result.FileName);
    }

    [Fact]
    public async Task HtmlFileUrl_StillDownloads()
    {
        var service = Service(_ => Html("<html>doc</html>"));

        DownloadTransferResult result = await service.DownloadAsync(
            new DownloadTransferRequest(new Uri("https://x.test/docs/manual.html"), _directory));

        Assert.Equal("manual.html", result.FileName);
    }

    [Fact]
    public async Task MegaLinkWithoutKey_IsRefusedBeforeAnyRequest()
    {
        int requests = 0;
        var service = Service(_ =>
        {
            requests++;
            return Html("<html></html>");
        });

        NotAFileException failure = await Assert.ThrowsAsync<NotAFileException>(
            () => service.DownloadAsync(
                new DownloadTransferRequest(new Uri("https://mega.nz/file/NzBzCB4D"), _directory)));

        Assert.Equal(0, requests);
        Assert.Contains("decryption key", failure.Message);
        Assert.True(MegaLinks.IsMissingKey(new Uri("https://mega.nz/folder/abc")));
        Assert.False(MegaLinks.IsMissingKey(new Uri("https://mega.nz/file/abc#key")));
        Assert.False(MegaLinks.IsMissingKey(new Uri("https://example.test/file/abc")));
    }

    [Fact]
    public async Task LinkCheck_FlagsMegaLinkWithoutKey()
    {
        var checker = new LinkChecker(new HttpClient(new Handler(_ => Html(""))));

        LinkCheckResult result = await checker.CheckAsync(new Uri("https://mega.nz/file/NzBzCB4D"));

        Assert.Equal(LinkCheckStatus.NeedsAttention, result.Status);
        Assert.Contains("decryption key", result.Message);
    }

    // ---------- Extensions ----------

    [Fact]
    public async Task IdNamedImage_GetsItsRealExtension()
    {
        var service = Service(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png)
        });

        DownloadTransferResult result = await service.DownloadAsync(
            new DownloadTransferRequest(
                new Uri("https://filester.example.test/t/89d50f21-094d"),
                _directory));

        Assert.Equal("89d50f21-094d.png", result.FileName);
        Assert.True(File.Exists(result.DestinationPath));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, null, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, null, ".gif")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, null, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20 }, null, ".mov")]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D }, null, ".webm")]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x88, 0x6D, 0x61, 0x74 }, null, ".mkv")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, null, ".webp")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null, ".zip")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, "video/mp4", ".mp4")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, "application/octet-stream", null)]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, "application/x-msdownload", null)]
    public void Sniffer_KnowsMediaButNeverAddsProgramExtensions(
        byte[] header,
        string? mediaType,
        string? expected)
    {
        Assert.Equal(expected, FileTypeSniffer.SuggestExtension(header, mediaType));
    }

    [Fact]
    public void Sniffer_LeavesExistingExtensionsAlone()
    {
        Assert.Equal("photo.jpeg", FileTypeSniffer.EnsureExtension("photo.jpeg", Png, "image/png"));
        Assert.Equal("clip.png", FileTypeSniffer.EnsureExtension("clip", Png, null));
    }

    // ---------- Pixeldrain API key ----------

    [Theory]
    [InlineData("https://pixeldrain.com/api/file/abc", true)]
    [InlineData("https://pixeldrain.nl/api/file/abc", true)]
    [InlineData("https://cdn.pixeldrain.com/x", true)]
    [InlineData("http://pixeldrain.com/api/file/abc", false)]
    [InlineData("https://pixeldrain.com.evil.test/x", false)]
    [InlineData("https://example.test/pixeldrain.com", false)]
    public void PixeldrainKey_OnlyForHttpsPixeldrainHosts(string url, bool expected)
    {
        Assert.Equal(expected, PixeldrainAuthHandler.IsPixeldrainUri(new Uri(url)));
    }

    [Fact]
    public async Task PixeldrainKey_IsSentAsBasicAuthOnlyToPixeldrain()
    {
        var seen = new List<(string Host, string? Auth)>();

        var inner = new Handler(request =>
        {
            seen.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(new PixeldrainAuthHandler("secret-key", inner));

        await client.GetAsync("https://pixeldrain.com/api/file/abc/info");
        await client.GetAsync("https://other.example.test/file");

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":secret-key"));

        Assert.Equal(expected, seen[0].Auth);
        Assert.Null(seen[1].Auth);
    }

    [Fact]
    public void PixeldrainAccount_DescribesUserAndPlan()
    {
        Assert.Equal(
            "Key accepted: signed in as maya (Pro).",
            PixeldrainAccount.DescribeAccount("{\"username\":\"maya\",\"subscription\":{\"name\":\"Pro\"}}"));

        Assert.Equal("Key accepted.", PixeldrainAccount.DescribeAccount("not json"));
    }

    [Fact]
    public async Task PixeldrainAccount_RejectedKeyIsReported()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        PixeldrainKeyCheck result = await PixeldrainAccount.CheckKeyAsync(client, "wrong");

        Assert.False(result.Valid);
        Assert.Contains("did not accept", result.Message);
    }

    // ---------- helpers ----------

    private static HttpDownloadTransferService Service(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)));

    private static HttpResponseMessage Html(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        };
        return response;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
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
