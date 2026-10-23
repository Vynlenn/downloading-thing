using System.Net;
using System.Text;
using DeezNutzDownloader.Adapters.Media;

namespace DeezNutzDownloader.Tests;

public sealed class ExternalToolCatalogTests
{
    [Fact]
    public void BuildYtDlpAssetUrl_PinsExactAddress()
    {
        Assert.Equal(
            "https://github.com/yt-dlp/yt-dlp/releases/download/" +
            "2025.10.14/yt-dlp.exe",
            ExternalToolCatalog.BuildYtDlpAssetUrl("2025.10.14"));
    }

    [Fact]
    public void ParseYtDlpLatestTag_ReadsTagName()
    {
        Assert.Equal(
            "2025.10.14",
            ExternalToolCatalog.ParseYtDlpLatestTag(
                "{\"tag_name\":\"2025.10.14\",\"draft\":false}"));

        Assert.Null(
            ExternalToolCatalog.ParseYtDlpLatestTag(
                "{\"tag_name\":\"  \"}"));

        Assert.Null(
            ExternalToolCatalog.ParseYtDlpLatestTag("not json"));
    }

    [Fact]
    public void ParseFFmpegVersion_ReadsFirstToken()
    {
        Assert.Equal(
            "7.0.2-essentials_build-www.gyan.dev",
            ExternalToolCatalog.ParseFFmpegVersion(
                "ffmpeg version 7.0.2-essentials_build-www.gyan.dev " +
                "Copyright (c) 2000-2024"));

        Assert.Null(ExternalToolCatalog.ParseFFmpegVersion("garbage"));
        Assert.Null(ExternalToolCatalog.ParseFFmpegVersion(null));
    }

    [Fact]
    public void ParseYtDlpVersion_AcceptsBareTagOnly()
    {
        Assert.Equal(
            "2025.10.14",
            ExternalToolCatalog.ParseYtDlpVersion("2025.10.14"));

        Assert.Null(
            ExternalToolCatalog.ParseYtDlpVersion("yt-dlp 2025.10.14"));
    }
}

public sealed class ExternalToolProvisionerTests : IDisposable
{
    private readonly string _directory;

    public ExternalToolProvisionerTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-tools-tests",
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
    public async Task Resolve_ManualPath_WinsOverManagement()
    {
        string manual = Path.Combine(_directory, "custom-yt.exe");
        await File.WriteAllTextAsync(manual, "x");

        var provisioner = new ExternalToolProvisioner(
            new HttpClient(new StubHandler(_ => null)),
            _directory,
            versionProbe: (_, _) =>
                Task.FromResult<string?>("should-not-run"));

        string resolved = await provisioner.ResolveAsync(
            ExternalTool.YtDlp,
            manual,
            autoManage: true);

        Assert.Equal(manual, resolved);
    }

    [Fact]
    public async Task Resolve_AutoManageOff_FallsBackToBareName()
    {
        var provisioner = new ExternalToolProvisioner(
            new HttpClient(new StubHandler(_ => null)),
            _directory);

        string resolved = await provisioner.ResolveAsync(
            ExternalTool.YtDlp,
            configuredPath: string.Empty,
            autoManage: false);

        Assert.Equal("yt-dlp", resolved);
    }

    [Fact]
    public async Task Ensure_DownloadsAndManifestsYtDlp()
    {
        byte[] fakeExe = Encoding.UTF8.GetBytes("MZ-fake");

        var handler = new StubHandler(request =>
        {
            string url =
                request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url.Contains("api.github.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"tag_name\":\"2025.10.14\"}")
                };
            }

            if (url.EndsWith("/yt-dlp.exe"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fakeExe)
                };
            }

            return null;
        });

        var provisioner = new ExternalToolProvisioner(
            new HttpClient(handler),
            _directory,
            versionProbe: (_, _) =>
                Task.FromResult<string?>("2025.10.14"));

        string path = await provisioner.EnsureAsync(ExternalTool.YtDlp);

        Assert.Equal(
            Path.Combine(_directory, "yt-dlp.exe"),
            path);
        Assert.Equal(fakeExe, await File.ReadAllBytesAsync(path));

        List<ManagedToolManifest> manifests =
            await provisioner.ReadManifestAsync();

        ManagedToolManifest entry = Assert.Single(manifests);
        Assert.Equal("2025.10.14", entry.Version);

        Assert.Equal(
            "2025.10.14",
            await provisioner.InstalledVersionAsync(ExternalTool.YtDlp));
    }

    [Fact]
    public async Task Ensure_HealthyTool_DownloadsNothing()
    {
        string binary = Path.Combine(_directory, "ffmpeg.exe");
        await File.WriteAllTextAsync(binary, "x");

        int probes = 0;

        var provisioner = new ExternalToolProvisioner(
            new HttpClient(new StubHandler(_ =>
                throw new InvalidOperationException("no network"))),
            _directory,
            versionProbe: (_, _) =>
            {
                probes++;
                return Task.FromResult<string?>(
                    "ffmpeg version 7.0.2-essentials_build");
            });

        string path = await provisioner.EnsureAsync(ExternalTool.FFmpeg);

        Assert.Equal(binary, path);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Ensure_FailedSmokeTest_DeletesBinary()
    {
        var handler = new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri?.AbsoluteUri.Contains("api.github")
                        == true
                        ? "{\"tag_name\":\"2025.10.14\"}"
                        : "corrupt")
            });

        var provisioner = new ExternalToolProvisioner(
            new HttpClient(handler),
            _directory,
            versionProbe: (_, _) =>
                Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.EnsureAsync(ExternalTool.YtDlp));

        Assert.False(
            File.Exists(Path.Combine(_directory, "yt-dlp.exe")));
    }

    private sealed class StubHandler
        : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            HttpResponseMessage?> _respond;

        public StubHandler(
            Func<HttpRequestMessage, HttpResponseMessage?> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage? response = _respond(request);

            return Task.FromResult(
                response ??
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
