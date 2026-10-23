using DeezNutzDownloader.Adapters.Media;

namespace DeezNutzDownloader.Tests;

public sealed class YtDlpProxyArgumentTests
{
    [Fact]
    public void BuildProbe_WithProxy_IncludesFlagBeforeUrl()
    {
        List<string> arguments =
            YtDlpArgumentBuilder.BuildProbe(
                "https://video.test/v/1",
                "http://proxy.test:8080/");

        int proxyIndex = arguments.IndexOf("--proxy");

        Assert.True(proxyIndex >= 0);
        Assert.Equal(
            "http://proxy.test:8080/",
            arguments[proxyIndex + 1]);
        Assert.Equal(
            "https://video.test/v/1",
            arguments[^1]);
    }

    [Fact]
    public void BuildProbe_WithoutProxy_OmitsFlag()
    {
        List<string> arguments =
            YtDlpArgumentBuilder.BuildProbe(
                "https://video.test/v/1");

        Assert.DoesNotContain("--proxy", arguments);
        Assert.Equal(
            "https://video.test/v/1",
            arguments[^1]);
    }

    [Fact]
    public void BuildDownload_WithProxyAndFfmpeg_KeepsAllFlags()
    {
        List<string> arguments =
            YtDlpArgumentBuilder.BuildDownload(
                "https://video.test/v/1",
                formatId: null,
                outputTemplate: "C:\\out\\%(title)s.%(ext)s",
                ffmpegPath: "C:\\tools\\ffmpeg.exe",
                proxyUrl: "http://bob:pw@proxy.test:8080/");

        Assert.Contains("bv*+ba/b", arguments);
        Assert.Contains("--ffmpeg-location", arguments);
        Assert.Contains("C:\\tools\\ffmpeg.exe", arguments);

        int proxyIndex = arguments.IndexOf("--proxy");

        Assert.True(proxyIndex >= 0);
        Assert.Equal(
            "http://bob:pw@proxy.test:8080/",
            arguments[proxyIndex + 1]);
        Assert.Equal(
            "https://video.test/v/1",
            arguments[^1]);
    }

    [Fact]
    public void BuildDownload_WithoutProxy_OmitsFlag()
    {
        List<string> arguments =
            YtDlpArgumentBuilder.BuildDownload(
                "https://video.test/v/1",
                "22",
                "out",
                string.Empty);

        Assert.DoesNotContain("--proxy", arguments);
        Assert.Contains("22", arguments);
    }
}
