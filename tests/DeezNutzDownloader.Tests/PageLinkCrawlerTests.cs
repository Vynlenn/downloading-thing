using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class PageLinkCrawlerTests
{
    [Fact]
    public async Task ExtractFromPageAsync_HtmlPage_ReturnsFiles()
    {
        const string html =
            "<html><body>" +
            "<a href=\"https://pixeldrain.com/u/a1\">m1</a>" +
            "<a href=\"/logo.png\">logo</a>" +
            "</body></html>";

        var handler = new StubHandler(_ =>
        {
            var content = new StringContent(html);
            content.Headers.ContentType =
                new MediaTypeHeaderValue("text/html");

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
        });

        using var client = new HttpClient(handler);
        var crawler = new PageLinkCrawler(client);

        PageExtractionResult result =
            await crawler.ExtractFromPageAsync(
                "https://forum.test/thread/1");

        PageLinkCandidate single = Assert.Single(result.Files);
        Assert.Equal("https://pixeldrain.com/u/a1", single.Url);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExtractFromPageAsync_DirectFile_SuggestsAnalyze()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
            content.Headers.ContentType =
                new MediaTypeHeaderValue(
                    "application/octet-stream");

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
        });

        using var client = new HttpClient(handler);
        var crawler = new PageLinkCrawler(client);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crawler.ExtractFromPageAsync(
                    "https://cdn.test/file.zip"));

        Assert.Contains("Analyze links", exception.Message);
    }

    [Fact]
    public async Task ExtractFromPageAsync_OversizePage_Refuses()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StringContent("<html></html>");
            content.Headers.ContentType =
                new MediaTypeHeaderValue("text/html");
            content.Headers.ContentLength = 50_000_000;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
        });

        using var client = new HttpClient(handler);
        var crawler = new PageLinkCrawler(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crawler.ExtractFromPageAsync(
                "https://forum.test/huge"));
    }

    [Fact]
    public async Task ExtractFromPageAsync_BadUrl_Throws()
    {
        var handler = new StubHandler(_ =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = new HttpClient(handler);
        var crawler = new PageLinkCrawler(client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => crawler.ExtractFromPageAsync("notaurl"));

        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            Task<HttpResponseMessage>> _send;

        public StubHandler(
            Func<
                HttpRequestMessage,
                Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return _send(request);
        }
    }
}
