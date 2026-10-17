using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class ChallengeIntegrationTests : IDisposable
{
    private readonly string _directory;

    public ChallengeIntegrationTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-challenge-dl-tests",
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
    public async Task Download_Captcha403_ThrowsTypedChallenge()
    {
        var service = new HttpDownloadTransferService(
            new HttpClient(new FixedHandler(
                HttpStatusCode.Forbidden,
                "<html><div class=\"g-recaptcha\" " +
                "data-sitekey=\"x\"></div></html>")));

        var request = new DownloadTransferRequest(
            new Uri("https://files.example.com/f/1.bin"),
            _directory);

        ChallengeDetectedException challenge =
            await Assert.ThrowsAsync<ChallengeDetectedException>(
                () => service.DownloadAsync(request));

        Assert.Equal(ChallengeKind.Captcha, challenge.Kind);
        Assert.Equal("files.example.com", challenge.Host);
        Assert.True(challenge.IsSolvableInBrowser);
    }

    [Fact]
    public async Task Download_Pixeldrain403_StaysQuotaThrottle()
    {
        var service = new HttpDownloadTransferService(
            new HttpClient(new FixedHandler(
                HttpStatusCode.Forbidden,
                "{\"success\":false}")));

        var request = new DownloadTransferRequest(
            new Uri("https://pixeldrain.com/api/file/abc"),
            _directory);

        HostThrottledException throttled =
            await Assert.ThrowsAsync<HostThrottledException>(
                () => service.DownloadAsync(request));

        Assert.True(throttled.IsQuotaExhausted);
    }

    [Fact]
    public async Task Crawl_BrowserCheck_BuildsTypedChallenge()
    {
        using var response = new HttpResponseMessage(
            HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "<html><title>Just a moment...</title></html>")
        };

        Exception failure =
            await PageLinkCrawler.BuildChallengeOrFailureAsync(
                response,
                new Uri("https://example.com/gallery/1"));

        var challenge = Assert.IsType<ChallengeDetectedException>(
            failure);

        Assert.Equal(ChallengeKind.BrowserCheck, challenge.Kind);
        Assert.Equal(
            "https://example.com/gallery/1",
            challenge.PageUrl);
    }

    [Fact]
    public async Task Crawl_Plain403_BuildsGenericError()
    {
        using var response = new HttpResponseMessage(
            HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<h1>nope</h1>")
        };

        Exception failure =
            await PageLinkCrawler.BuildChallengeOrFailureAsync(
                response,
                new Uri("https://example.com/gallery/1"));

        Assert.IsType<HttpRequestException>(failure);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FixedHandler(HttpStatusCode status, string body)
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
