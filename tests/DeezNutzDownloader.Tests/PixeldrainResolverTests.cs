using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class PixeldrainResolverTests
{
    [Theory]
    [InlineData("https://pixeldrain.com/u/AbC123xYz", "AbC123xYz")]
    [InlineData("https://pixeldrain.com/u/AbC123xYz?x=1", "AbC123xYz")]
    [InlineData(
        "https://pixeldrain.com/api/file/AbC123xYz/info",
        "AbC123xYz")]
    [InlineData(
        "https://dl.pixeldrain.com/u/AbC123xYz",
        "AbC123xYz")]
    public void TryParseFileId_AcceptsKnownForms(
        string url,
        string expected)
    {
        Assert.Equal(
            expected,
            PixeldrainResolver.TryParseFileId(new Uri(url)));
    }

    [Theory]
    [InlineData("https://pixeldrain.com/l/AbC123xYz")]
    [InlineData("https://pixeldrain.com/u/")]
    [InlineData("https://pixeldrain.com/u/ab")]
    [InlineData("https://pixeldrain.com/u/a b")]
    [InlineData("https://gofile.io/d/AbC123xYz")]
    [InlineData("https://evil-pixeldrain.com/u/AbC123xYz")]
    public void TryParseFileId_RejectsNonFiles(string url)
    {
        Assert.Null(
            PixeldrainResolver.TryParseFileId(new Uri(url)));
    }

    [Fact]
    public void BuildDownloadUrl_UsesAttachmentEndpoint()
    {
        Assert.Equal(
            "https://pixeldrain.com/api/file/AbC123xYz?download",
            PixeldrainResolver.BuildDownloadUrl("AbC123xYz").AbsoluteUri);
    }

    [Fact]
    public void Parse_InfoJson_ReadsNameAndSize()
    {
        const string json =
            "{\"success\":true,\"id\":\"AbC123xYz\"," +
            "\"name\":\"movie night.mkv\",\"size\":123456789," +
            "\"can_download\":true}";

        PixeldrainFileInfo info =
            PixeldrainInfoParser.Parse(json, "AbC123xYz");

        Assert.Equal("movie night.mkv", info.Name);
        Assert.Equal(123456789, info.SizeBytes);
    }

    [Fact]
    public void Parse_ErrorJson_Captcha_ThrowsChallenge()
    {
        const string json =
            "{\"success\":false," +
            "\"value\":\"file_rate_limited_captcha_required\"," +
            "\"message\":\"captcha\"}";

        ChallengeDetectedException challenge =
            Assert.Throws<ChallengeDetectedException>(
                () => PixeldrainInfoParser.Parse(json, "AbC123xYz"));

        Assert.Equal(ChallengeKind.Captcha, challenge.Kind);
        Assert.True(challenge.IsSolvableInBrowser);
        Assert.Equal(
            "https://pixeldrain.com/u/AbC123xYz",
            challenge.PageUrl);
    }

    [Fact]
    public void Parse_ErrorJson_Quota_ThrowsThrottled()
    {
        const string json =
            "{\"success\":false," +
            "\"value\":\"ip_download_limit_exceeded\"," +
            "\"message\":\"slow down\"}";

        HostThrottledException throttled =
            Assert.Throws<HostThrottledException>(
                () => PixeldrainInfoParser.Parse(json, "AbC123xYz"));

        Assert.True(throttled.IsQuotaExhausted);
    }

    [Fact]
    public async Task Resolve_FullFlow_ReturnsDownload()
    {
        var handler = new RouteHandler(request =>
        {
            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url.EndsWith("/api/file/AbC123xYz/info"))
            {
                return Json(
                    "{\"success\":true,\"id\":\"AbC123xYz\"," +
                    "\"name\":\"clip.mp4\",\"size\":42}");
            }

            return null;
        });

        var resolver = new PixeldrainResolver();
        using var client = new HttpClient(handler);

        Assert.True(
            resolver.CanResolve(
                new Uri("https://pixeldrain.com/u/AbC123xYz")));

        ResolvedHostFile resolved = await resolver.ResolveAsync(
            new Uri("https://pixeldrain.com/u/AbC123xYz"),
            client);

        Assert.Equal(
            "https://pixeldrain.com/api/file/AbC123xYz?download",
            resolved.DownloadUri.AbsoluteUri);
        Assert.Equal("clip.mp4", resolved.SuggestedFileName);
        Assert.Equal(42, resolved.TotalBytes);
    }

    [Fact]
    public async Task Transfer_PixeldrainPage_SavesServerName()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-pixeldrain-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            byte[] bytes = new byte[] { 1, 2, 3, 4 };

            var handler = new RouteHandler(request =>
            {
                string url =
                    request.RequestUri?.AbsoluteUri ?? string.Empty;

                if (url.EndsWith("/api/file/AbC123xYz/info"))
                {
                    return Json(
                        "{\"success\":true,\"id\":\"AbC123xYz\"," +
                        "\"name\":\"clip.mp4\",\"size\":4}");
                }

                if (url.EndsWith("/api/file/AbC123xYz?download"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes)
                    };
                }

                return null;
            });

            var service = new HttpDownloadTransferService(
                new HttpClient(handler),
                new IHostFileResolver[]
                {
                    new PixeldrainResolver()
                });

            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://pixeldrain.com/u/AbC123xYz"),
                        directory));

            Assert.Equal("clip.mp4", result.FileName);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(
                result.DestinationPath));
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
    public async Task Transfer_PixeldrainCaptcha_SurfacesChallenge()
    {
        var handler = new RouteHandler(request => Json(
            "{\"success\":false," +
            "\"value\":\"hotlink_detected\",\"message\":\"x\"}"));

        var service = new HttpDownloadTransferService(
            new HttpClient(handler),
            new IHostFileResolver[] { new PixeldrainResolver() });

        string directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-pixeldrain-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            ChallengeDetectedException challenge =
                await Assert.ThrowsAsync<ChallengeDetectedException>(
                    () => service.DownloadAsync(
                        new DownloadTransferRequest(
                            new Uri(
                                "https://pixeldrain.com/u/AbC123xYz"),
                            directory)));

            Assert.Equal(ChallengeKind.Captcha, challenge.Kind);
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
