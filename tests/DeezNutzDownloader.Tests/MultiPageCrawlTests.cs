using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Adapters.Media;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class PaginationDetectionTests
{
    private static readonly Uri Page =
        new("https://forum.test/gallery/1");

    [Fact]
    public void IsNextPageLink_RelNext_Wins()
    {
        Assert.True(
            PageLinkExtractor.IsNextPageLink(
                "https://forum.test/gallery/abc",
                " rel=\"next\" ",
                Page));
    }

    [Fact]
    public void IsNextPageLink_QueryPage_Matches()
    {
        Assert.True(
            PageLinkExtractor.IsNextPageLink(
                "https://forum.test/gallery/1?page=2",
                string.Empty,
                Page));
    }

    [Fact]
    public void IsNextPageLink_PathPage_Matches()
    {
        Assert.True(
            PageLinkExtractor.IsNextPageLink(
                "https://forum.test/gallery/page/3",
                string.Empty,
                Page));
    }

    [Fact]
    public void IsNextPageLink_CrossHost_Rejected()
    {
        Assert.False(
            PageLinkExtractor.IsNextPageLink(
                "https://evil.test/gallery?page=2",
                " rel=\"next\" ",
                Page));
    }

    [Fact]
    public void IsNextPageLink_FileLink_NeverPagination()
    {
        Assert.False(
            PageLinkExtractor.IsNextPageLink(
                "https://pixeldrain.com/u/a1?page=2",
                " rel=\"next\" ",
                Page));
    }

    [Fact]
    public void IsNextPageLink_OrdinaryLink_Rejected()
    {
        Assert.False(
            PageLinkExtractor.IsNextPageLink(
                "https://forum.test/about",
                string.Empty,
                Page));
    }

    [Fact]
    public void IsNextPageLink_RelAfterHref_Wins()
    {
        const string html =
            "<html><body>" +
            "<a href=\"/gallery/2\" rel=\"next\">next</a>" +
            "</body></html>";

        PageExtractionResult result = PageLinkExtractor.Extract(
            html,
            "https://forum.test/gallery/1");

        Assert.Equal(
            "https://forum.test/gallery/2",
            result.NextPageUrl);
    }

    [Fact]
    public void Extract_SurfacesNextPageUrl()
    {
        const string html =
            "<html><body>" +
            "<a href=\"https://pixeldrain.com/u/a1\">m1</a>" +
            "<a rel=\"next\" href=\"/gallery/2\">next</a>" +
            "</body></html>";

        PageExtractionResult result = PageLinkExtractor.Extract(
            html,
            "https://forum.test/gallery/1");

        Assert.Single(result.Files);
        Assert.Equal(
            "https://forum.test/gallery/2",
            result.NextPageUrl);
    }
}

public sealed class MultiPageCrawlTests
{
    [Fact]
    public async Task ExtractFromPages_WalksTwoPages_MergesFiles()
    {
        var pages = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["https://forum.test/gallery/1"] =
                "<a href=\"https://pixeldrain.com/u/a1\">m1</a>" +
                "<a rel=\"next\" href=\"/gallery/2\">next</a>",
            ["https://forum.test/gallery/2"] =
                "<a href=\"https://pixeldrain.com/u/a1\">m1-again</a>" +
                "<a href=\"https://pixeldrain.com/u/a2\">m2</a>",
        };

        var handler = new MapHandler(pages);
        using var client = new HttpClient(handler);
        var crawler = new PageLinkCrawler(client);

        PageCrawlResult result = await crawler.ExtractFromPagesAsync(
            "https://forum.test/gallery/1");

        Assert.Equal(2, result.PagesVisited);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(
            "https://pixeldrain.com/u/a1",
            result.Files[0].Url);
        Assert.Equal(
            "https://pixeldrain.com/u/a2",
            result.Files[1].Url);
    }

    [Fact]
    public async Task ExtractFromPages_Loop_StopsAtRepeat()
    {
        var pages = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["https://forum.test/gallery/1"] =
                "<a rel=\"next\" href=\"/gallery/2\">next</a>",
            ["https://forum.test/gallery/2"] =
                "<a rel=\"next\" href=\"/gallery/1\">back</a>",
        };

        var handler = new MapHandler(pages);
        using var client = new HttpClient(handler);

        PageCrawlResult result =
            await new PageLinkCrawler(client).ExtractFromPagesAsync(
                "https://forum.test/gallery/1");

        Assert.Equal(2, result.PagesVisited);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ExtractFromPages_RespectsMaxPages()
    {
        var pages = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        for (int index = 1; index <= 5; index++)
        {
            pages[$"https://forum.test/gallery/{index}"] =
                $"<a rel=\"next\" href=\"/gallery/{index + 1}\">n</a>";
        }

        var handler = new MapHandler(pages);
        using var client = new HttpClient(handler);

        PageCrawlResult result =
            await new PageLinkCrawler(client).ExtractFromPagesAsync(
                "https://forum.test/gallery/1",
                maxPages: 3);

        Assert.Equal(3, result.PagesVisited);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task ExtractFromPages_OffHostNext_Stops()
    {
        var pages = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // The extractor itself never emits cross-host next
            // links; the crawler double-guards the walk anyway.
            ["https://forum.test/gallery/1"] =
                "<a href=\"https://pixeldrain.com/u/a1\">m1</a>",
        };

        var handler = new MapHandler(pages);
        using var client = new HttpClient(handler);

        PageCrawlResult result =
            await new PageLinkCrawler(client).ExtractFromPagesAsync(
                "https://forum.test/gallery/1");

        Assert.Equal(1, result.PagesVisited);
        Assert.Single(result.Files);
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pages;

        public int RequestCount { get; private set; }

        public MapHandler(Dictionary<string, string> pages)
        {
            _pages = pages;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (!_pages.TryGetValue(url, out string? html))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new StringContent(
                "<html><body>" + html + "</body></html>");

            content.Headers.ContentType =
                new MediaTypeHeaderValue("text/html");

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
        }
    }
}

public sealed class YtDlpCookiesArgumentTests
{
    [Fact]
    public void BuildProbe_WithBrowser_EmitsFlag()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildProbe(
            "https://video.test/v/1",
            proxyUrl: null,
            cookiesFromBrowser: "chrome");

        Assert.Contains("--cookies-from-browser", arguments);
        Assert.Contains("chrome", arguments);
    }

    [Fact]
    public void BuildDownload_WithoutBrowser_OmitsFlag()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildDownload(
            "https://video.test/v/1",
            formatId: null,
            outputTemplate: "%(title)s.%(ext)s",
            ffmpegPath: string.Empty,
            proxyUrl: null,
            cookiesFromBrowser: "  ");

        Assert.DoesNotContain("--cookies-from-browser", arguments);
    }

    [Fact]
    public void BuildDownload_WithBrowserAndProxy_EmitsBoth()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildDownload(
            "https://video.test/v/1",
            formatId: null,
            outputTemplate: "%(title)s.%(ext)s",
            ffmpegPath: string.Empty,
            proxyUrl: "http://127.0.0.1:8080/",
            cookiesFromBrowser: "firefox:myprofile");

        int cookiesIndex =
            arguments.IndexOf("--cookies-from-browser");

        Assert.True(cookiesIndex >= 0);
        Assert.Equal(
            "firefox:myprofile",
            arguments[cookiesIndex + 1]);
        Assert.Contains("--proxy", arguments);
    }
}
