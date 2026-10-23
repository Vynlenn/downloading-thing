using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Adapters.Media;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class PixeldrainListTests
{
    [Theory]
    [InlineData("https://pixeldrain.com/l/AbC123xY", "AbC123xY")]
    [InlineData("https://pixeldrain.nl/l/AbC123xY", "AbC123xY")]
    public void TryParseListId_AcceptsListPages(
        string url,
        string expected)
    {
        Assert.Equal(
            expected,
            PixeldrainResolver.TryParseListId(new Uri(url)));
    }

    [Theory]
    [InlineData("https://pixeldrain.com/u/AbC123xY")]
    [InlineData("https://pixeldrain.com/l/ab")]
    [InlineData("https://gofile.io/d/AbC123xY")]
    public void TryParseListId_RejectsOthers(string url)
    {
        Assert.Null(
            PixeldrainResolver.TryParseListId(new Uri(url)));
    }

    [Fact]
    public void ParseList_ReadsTitleAndFiles()
    {
        const string json =
            "{\"success\":true,\"id\":\"L8bhwx\"," +
            "\"title\":\"Rust in Peace\",\"file_count\":2," +
            "\"files\":[" +
            "{\"id\":\"_SqVWi\",\"name\":\"01 Holy.mp3\",\"size\":111}," +
            "{\"id\":\"RKwgZb\",\"name\":\"02 Hangar.mp3\",\"size\":222}]}";

        PixeldrainList list =
            PixeldrainListParser.Parse(json, "L8bhwx");

        Assert.Equal("Rust in Peace", list.Title);
        Assert.Equal(2, list.Files.Count);
        Assert.Equal(
            "https://pixeldrain.com/api/file/_SqVWi?download",
            list.Files[0].DownloadUri.AbsoluteUri);
        Assert.Equal("01 Holy.mp3", list.Files[0].SuggestedFileName);
        Assert.Equal(111, list.Files[0].TotalBytes);
    }

    [Fact]
    public void ParseList_EmptyFiles_Parses()
    {
        PixeldrainList list = PixeldrainListParser.Parse(
            "{\"success\":true,\"id\":\"L1\",\"files\":[]}",
            "L1");

        Assert.Empty(list.Files);
    }

    [Fact]
    public void ParseInfo_ReadsSha256()
    {
        const string json =
            "{\"success\":true,\"id\":\"abc123\"," +
            "\"name\":\"screenshot.png\",\"size\":9," +
            "\"hash_sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"}";

        PixeldrainFileInfo info =
            PixeldrainInfoParser.Parse(json, "abc123");

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            info.Sha256Hex);
    }

    [Fact]
    public async Task ResolveFolder_MissingList_ThrowsTerminal()
    {
        var handler = new RouteHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"success\":false,\"value\":\"not_found\"," +
                    "\"message\":\"The entity you requested " +
                    "could not be found\"}")
            });

        var resolver = new PixeldrainResolver();
        using var client = new HttpClient(handler);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => resolver.ResolveFolderAsync(
                    new Uri("https://pixeldrain.com/l/L8bhwx"),
                    client));

        Assert.Contains("no file", failure.Message);
        Assert.DoesNotContain("Check the connection", failure.Message);
    }

    [Fact]
    public void ParseInfo_BadHash_DegradesToNull()
    {
        const string json =
            "{\"success\":true,\"id\":\"abc123\"," +
            "\"hash_sha256\":\"not-a-hash\"}";

        Assert.Null(
            PixeldrainInfoParser.Parse(json, "abc123").Sha256Hex);
    }

    [Fact]
    public async Task ResolveFolder_FullFlow_ReturnsFiles()
    {
        var handler = new RouteHandler(_ => Json(
            "{\"success\":true,\"id\":\"L8bhwx\"," +
            "\"title\":\"Mix\",\"files\":[" +
            "{\"id\":\"fileOne1\",\"name\":\"a.mp3\",\"size\":1}," +
            "{\"id\":\"fileTwo2\",\"name\":\"b.mp3\",\"size\":2}]}"));

        var resolver = new PixeldrainResolver();
        using var client = new HttpClient(handler);

        Assert.True(
            resolver.CanResolveFolder(
                new Uri("https://pixeldrain.com/l/L8bhwx")));

        Assert.False(
            resolver.CanResolve(
                new Uri("https://pixeldrain.com/l/L8bhwx")));

        ResolvedHostFolder folder =
            await resolver.ResolveFolderAsync(
                new Uri("https://pixeldrain.com/l/L8bhwx"),
                client);

        Assert.Equal("Mix", folder.FolderName);
        Assert.Equal(2, folder.Files.Count);
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

public sealed class GofileFolderTests
{
    [Fact]
    public void FlattenFiles_PreservesSubfolders()
    {
        var content = new GofileContent(
            "Mix",
            Array.Empty<ResolvedHostFile>(),
            new[]
            {
                new GofileEntry(
                    new ResolvedHostFile(
                        new Uri("https://s.test/a.zip"),
                        "a.zip",
                        1),
                    null,
                    Array.Empty<GofileEntry>()),
                new GofileEntry(
                    null,
                    "live",
                    new[]
                    {
                        new GofileEntry(
                            new ResolvedHostFile(
                                new Uri("https://s.test/b.mp3"),
                                "b.mp3",
                                2),
                            null,
                            Array.Empty<GofileEntry>()),
                    }),
            });

        IReadOnlyList<ResolvedHostFile> flat =
            GofileResolver.FlattenFiles(content);

        Assert.Equal(2, flat.Count);
        Assert.Equal("a.zip", flat[0].SuggestedFileName);
        Assert.Equal("live/b.mp3", flat[1].SuggestedFileName);
    }

    [Fact]
    public async Task ResolveFolder_FullFlow_ReturnsFolder()
    {
        var handler = new GofileRouteHandler();
        using var client = new HttpClient(handler);

        var resolver = new GofileResolver();

        ResolvedHostFolder folder =
            await resolver.ResolveFolderAsync(
                new Uri("https://gofile.io/d/AbC123"),
                client);

        Assert.Equal("Mix", folder.FolderName);
        Assert.Equal(2, folder.Files.Count);
    }

    private sealed class GofileRouteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url == "https://api.gofile.io/accounts")
            {
                return Task.FromResult(Json(
                    "{\"status\":\"ok\",\"data\":{\"token\":\"t\"}}"));
            }

            return Task.FromResult(Json(
                "{\"status\":\"ok\",\"data\":{" +
                "\"id\":\"d\",\"type\":\"folder\",\"name\":\"Mix\"," +
                "\"children\":{" +
                "\"f1\":{\"id\":\"f1\",\"type\":\"file\"," +
                "\"name\":\"a.zip\",\"size\":1," +
                "\"link\":\"https://s.test/a.zip\"}," +
                "\"f2\":{\"id\":\"f2\",\"type\":\"file\"," +
                "\"name\":\"b.zip\",\"size\":2," +
                "\"link\":\"https://s.test/b.zip\"}}}}"));
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }
}

public sealed class QueueHostFilesTests
{
    [Fact]
    public void EnqueueHostFiles_OneRowPerFile_WithFolder()
    {
        var queue = new DownloadQueue();

        DownloadQueueImportResult result =
            queue.EnqueueHostFiles(
                "My Mix!",
                new[]
                {
                    new ResolvedHostFile(
                        new Uri("https://s.test/a.zip"),
                        "a.zip",
                        1),
                    new ResolvedHostFile(
                        new Uri("https://s.test/b.zip"),
                        "live/b.mp3",
                        2),
                });

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, queue.Count);

        DownloadQueueItem first = queue.Items[0];
        DownloadQueueItem second = queue.Items[1];

        Assert.Equal("My Mix!", first.PackageName);
        Assert.Equal("a.zip", first.DisplayName);

        Assert.Contains("My Mix!", second.RelativeFolder);
        Assert.Contains("live", second.RelativeFolder);
    }

    [Fact]
    public void EnqueueHostFiles_Duplicates_CountAsQueued()
    {
        var queue = new DownloadQueue();

        var files = new[]
        {
            new ResolvedHostFile(
                new Uri("https://s.test/a.zip"),
                "a.zip",
                1),
        };

        Assert.Equal(1, queue.EnqueueHostFiles("F", files).AddedCount);
        Assert.Equal(0, queue.EnqueueHostFiles("F", files).AddedCount);
    }

    [Fact]
    public void CombineFolderPath_BlocksTraversal()
    {
        Assert.Equal(
            Path.Combine("F", "a.zip"),
            DownloadQueue.CombineFolderPath("F", "a.zip"));

        Assert.Equal(
            Path.Combine("F", "sub", "b.mp3"),
            DownloadQueue.CombineFolderPath("F", "sub/b.mp3"));

        string? traversal =
            DownloadQueue.CombineFolderPath("F", "../../evil.exe");

        Assert.NotNull(traversal);
        Assert.DoesNotContain("..", traversal);
    }
}

public sealed class ChecksumVerifyTests : IDisposable
{
    private readonly string _directory;

    public ChecksumVerifyTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-checksum-tests",
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
    public void VerifyChecksum_Match_Passes()
    {
        string file = Path.Combine(_directory, "a.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        string hex;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            hex = Convert.ToHexString(
                sha.ComputeHash(new byte[] { 1, 2, 3 }));
        }

        HttpDownloadTransferService.VerifyChecksum(
            file,
            hex.ToLowerInvariant(),
            new Uri("https://pixeldrain.com/api/file/x?download"));
    }

    [Fact]
    public void VerifyChecksum_Mismatch_ThrowsAndKeepsFile()
    {
        string file = Path.Combine(_directory, "a.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        ChecksumMismatchException failure =
            Assert.Throws<ChecksumMismatchException>(
                () => HttpDownloadTransferService.VerifyChecksum(
                    file,
                    new string('a', 64),
                    new Uri(
                        "https://pixeldrain.com/api/file/x?download")));

        Assert.Equal(file, failure.KeptPath);
        Assert.True(File.Exists(file));
        Assert.Contains("KEPT", failure.Message);
    }

    [Fact]
    public void VerifyChecksum_NoAdvertisedHash_Skips()
    {
        string file = Path.Combine(_directory, "a.bin");
        File.WriteAllBytes(file, new byte[] { 9 });

        HttpDownloadTransferService.VerifyChecksum(
            file,
            null,
            new Uri("https://example.com/f"));
    }
}

public sealed class SubtitleArgumentTests
{
    [Fact]
    public void BuildDownload_WithLangs_EmitsSrtSidecar()
    {
        List<string> arguments =
            YtDlpArgumentBuilder.BuildDownload(
                "https://video.test/v/1",
                formatId: null,
                outputTemplate: "out",
                ffmpegPath: string.Empty,
                subtitleLangs: "en,de");

        int index = arguments.IndexOf("--write-subs");
        Assert.True(index >= 0);
        Assert.Equal("en,de", arguments[arguments.IndexOf("--sub-langs") + 1]);
        Assert.Contains("srt", arguments);
    }

    [Fact]
    public void BuildDownload_WithoutLangs_OmitsSubs()
    {
        List<string> arguments =
            YtDlpArgumentBuilder.BuildDownload(
                "https://video.test/v/1",
                formatId: null,
                outputTemplate: "out",
                ffmpegPath: string.Empty,
                subtitleLangs: "  ");

        Assert.DoesNotContain("--write-subs", arguments);
        Assert.DoesNotContain("--sub-langs", arguments);
    }
}

public sealed class MegaSanitizeTests
{
    [Theory]
    [InlineData("secret plans.pdf", "secret plans.pdf")]
    [InlineData("a/b", "a_b")]
    [InlineData("  ", "download")]
    [InlineData("...", "download")]
    public void SanitizeNodeName_Cleans(string input, string expected)
    {
        Assert.Equal(
            expected,
            MegaDownloadService.SanitizeNodeName(input));
    }
}
