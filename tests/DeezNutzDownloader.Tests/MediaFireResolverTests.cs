using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class MediaFireResolverTests
{
    [Theory]
    [InlineData(
        "https://www.mediafire.com/file/abc123/report.pdf",
        "abc123")]
    [InlineData(
        "https://www.mediafire.com/file/abc123",
        "abc123")]
    [InlineData(
        "https://mediafire.com/file/a1B2c3/file.zip",
        "a1B2c3")]
    public void TryParseFileKey_AcceptsSharePages(
        string url,
        string expected)
    {
        Assert.Equal(
            expected,
            MediaFireResolver.TryParseFileKey(new Uri(url)));
    }

    [Theory]
    [InlineData("https://www.mediafire.com/folder/abc123/name")]
    [InlineData("https://www.mediafire.com/file/ab/name")]
    [InlineData("https://www.mediafire.com/download/abc123")]
    [InlineData("https://pixeldrain.com/u/abc123")]
    public void TryParseFileKey_RejectsOthers(string url)
    {
        Assert.Null(
            MediaFireResolver.TryParseFileKey(new Uri(url)));
    }

    [Fact]
    public void FindDownloadUrl_ButtonId_Wins()
    {
        const string html =
            "<html><body>" +
            "<a id=\"downloadButton\" class=\"x\" " +
            "href=\"https://download1234.mediafire.com/abc/file.zip\">" +
            "Download</a></body></html>";

        Assert.Equal(
            "https://download1234.mediafire.com/abc/file.zip",
            MediaFirePage.FindDownloadUrl(
                html,
                new Uri(
                    "https://www.mediafire.com/file/key123/file.zip")));
    }

    [Fact]
    public void FindDownloadUrl_AriaLabel_Fallback()
    {
        const string html =
            "<html><body>" +
            "<a aria-label=\"Download file\" " +
            "href=\"https://download1.mediafire.com/a/b.zip\">x</a>" +
            "</body></html>";

        Assert.Equal(
            "https://download1.mediafire.com/a/b.zip",
            MediaFirePage.FindDownloadUrl(
                html,
                new Uri("https://www.mediafire.com/file/key/b.zip")));
    }

    [Fact]
    public void FindDownloadUrl_SelfLink_Rejected()
    {
        const string html =
            "<html><body>" +
            "<a id=\"downloadButton\" " +
            "href=\"https://www.mediafire.com/file/key/b.zip\">x</a>" +
            "</body></html>";

        Assert.Null(
            MediaFirePage.FindDownloadUrl(
                html,
                new Uri("https://www.mediafire.com/file/key/b.zip")));
    }

    [Fact]
    public void IsPasswordLocked_DetectsForm()
    {
        Assert.True(
            MediaFirePage.IsPasswordLocked(
                "<form><input type=\"password\" name=\"p\"></form>"));

        Assert.False(
            MediaFirePage.IsPasswordLocked(
                "<a id=\"downloadButton\" href=\"https://d.x/y\">x</a>"));
    }

    [Fact]
    public async Task Resolve_FullFlow_ReturnsDirectWithName()
    {
        const string html =
            "<html><body>" +
            "<a id=\"downloadButton\" " +
            "href=\"https://download999.mediafire.com/k/report.pdf\">" +
            "Download</a></body></html>";

        var handler = new FixedHandler(html);
        using var client = new HttpClient(handler);

        var resolver = new MediaFireResolver();

        Assert.True(
            resolver.CanResolve(
                new Uri(
                    "https://www.mediafire.com/file/key999/report.pdf")));

        ResolvedHostFile resolved = await resolver.ResolveAsync(
            new Uri(
                "https://www.mediafire.com/file/key999/report.pdf"),
            client);

        Assert.Equal(
            "https://download999.mediafire.com/k/report.pdf",
            resolved.DownloadUri.AbsoluteUri);
        Assert.Equal("report.pdf", resolved.SuggestedFileName);
    }

    [Fact]
    public async Task Resolve_PasswordPage_ThrowsLoginWall()
    {
        var handler = new FixedHandler(
            "<form><input type=\"password\"></form>");

        using var client = new HttpClient(handler);

        ChallengeDetectedException challenge =
            await Assert.ThrowsAsync<ChallengeDetectedException>(
                () => new MediaFireResolver().ResolveAsync(
                    new Uri(
                        "https://www.mediafire.com/file/key1/a.zip"),
                    client));

        Assert.Equal(ChallengeKind.LoginWall, challenge.Kind);
        Assert.True(challenge.IsSolvableInBrowser);
    }

    [Fact]
    public async Task Resolve_Captcha403_ThrowsCaptcha()
    {
        var handler = new StatusHandler(
            HttpStatusCode.Forbidden,
            "<div class=\"g-recaptcha\"></div>");

        using var client = new HttpClient(handler);

        ChallengeDetectedException challenge =
            await Assert.ThrowsAsync<ChallengeDetectedException>(
                () => new MediaFireResolver().ResolveAsync(
                    new Uri(
                        "https://www.mediafire.com/file/key1/a.zip"),
                    client));

        Assert.Equal(ChallengeKind.Captcha, challenge.Kind);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly string _html;

        public FixedHandler(string html)
        {
            _html = html;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html)
                });
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StatusHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body)
                });
        }
    }
}
