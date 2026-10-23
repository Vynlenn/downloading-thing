using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class GofileResolverTests
{
    [Theory]
    [InlineData("https://gofile.io/d/AbC123", "AbC123")]
    [InlineData("https://www.gofile.io/d/AbC123", "AbC123")]
    public void TryParseContentCode_AcceptsShareLinks(
        string url,
        string expected)
    {
        Assert.Equal(
            expected,
            GofileResolver.TryParseContentCode(new Uri(url)));
    }

    [Theory]
    [InlineData("https://gofile.io/upload")]
    [InlineData("https://gofile.io/d/")]
    [InlineData("https://gofile.io/d/ab")]
    [InlineData("https://pixeldrain.com/u/AbC123")]
    public void TryParseContentCode_RejectsOthers(string url)
    {
        Assert.Null(
            GofileResolver.TryParseContentCode(new Uri(url)));
    }

    [Fact]
    public async Task Resolve_SingleFile_ReturnsDirectLink()
    {
        var handler = new RouteHandler(request =>
        {
            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (
                url == "https://api.gofile.io/accounts" &&
                request.Method == HttpMethod.Post)
            {
                return Json(
                    "{\"status\":\"ok\",\"data\":{" +
                    "\"id\":\"a\",\"token\":\"guest-token\"}}");
            }

            if (url == "https://api.gofile.io/contents/AbC123")
            {
                return Json(
                    "{\"status\":\"ok\",\"data\":{" +
                    "\"id\":\"f\",\"type\":\"file\"," +
                    "\"name\":\"report.pdf\",\"size\":99," +
                    "\"link\":\"https://store-1.gofile.io/" +
                    "download/web/f/report.pdf\"}}");
            }

            return null;
        });

        var resolver = new GofileResolver();
        using var client = new HttpClient(handler);

        Assert.True(
            resolver.CanResolve(
                new Uri("https://gofile.io/d/AbC123")));

        ResolvedHostFile resolved = await resolver.ResolveAsync(
            new Uri("https://gofile.io/d/AbC123"),
            client);

        Assert.Equal(
            "https://store-1.gofile.io/download/web/f/report.pdf",
            resolved.DownloadUri.AbsoluteUri);
        Assert.Equal("report.pdf", resolved.SuggestedFileName);
        Assert.Equal(99, resolved.TotalBytes);
    }

    [Fact]
    public async Task Resolve_Folder_ExplainsBatchExpansion()
    {
        var handler = new RouteHandler(request =>
        {
            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url == "https://api.gofile.io/accounts")
            {
                return Json(
                    "{\"status\":\"ok\",\"data\":{\"token\":\"t\"}}");
            }

            return Json(
                "{\"status\":\"ok\",\"data\":{" +
                "\"id\":\"d\",\"type\":\"folder\"," +
                "\"children\":{" +
                "\"f1\":{\"id\":\"f1\",\"type\":\"file\"," +
                "\"name\":\"a.zip\",\"size\":1," +
                "\"link\":\"https://s.gofile.io/download/web/f1/a.zip\"}," +
                "\"f2\":{\"id\":\"f2\",\"type\":\"file\"," +
                "\"name\":\"b.zip\",\"size\":2," +
                "\"link\":\"https://s.gofile.io/download/web/f2/b.zip\"}}}}");
        });

        using var client = new HttpClient(handler);

        FolderExpansionNeededException failure =
            await Assert.ThrowsAsync<FolderExpansionNeededException>(
                () => new GofileResolver().ResolveAsync(
                    new Uri("https://gofile.io/d/AbC123"),
                    client));

        Assert.Contains("2 files", failure.Message);
    }

    [Fact]
    public async Task Resolve_PremiumGate_SaysSoPlainly()
    {
        var handler = new RouteHandler(request =>
        {
            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url == "https://api.gofile.io/accounts")
            {
                return Json(
                    "{\"status\":\"ok\",\"data\":{\"token\":\"t\"}}");
            }

            return Json(
                "{\"status\":\"error-notPremium\"," +
                "\"data\":\"error-notPremium\"}");
        });

        using var client = new HttpClient(handler);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new GofileResolver().ResolveAsync(
                    new Uri("https://gofile.io/d/AbC123"),
                    client));

        Assert.Contains("Premium", failure.Message);
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            HttpResponseMessage?> _route;

        public RouteHandler(
            Func<HttpRequestMessage, HttpResponseMessage?> route)
        {
            _route = route;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                _route(request) ??
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
