using DeezNutzDownloader.Adapters.Media;

namespace DeezNutzDownloader.Tests;

public sealed class YtDlpPlaylistArgumentTests
{
    [Fact]
    public void BuildProbe_UsesFlatPlaylist_NotNoPlaylist()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildProbe(
            "https://video.test/playlist?list=x");

        Assert.Contains("--flat-playlist", arguments);
        Assert.DoesNotContain("--no-playlist", arguments);
        Assert.Equal("https://video.test/playlist?list=x", arguments[^1]);
    }

    [Fact]
    public void BuildDownload_PlaylistTrue_EmitsYesPlaylist()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildDownload(
            "https://video.test/playlist?list=x",
            formatId: null,
            outputTemplate: "out",
            ffmpegPath: string.Empty,
            downloadPlaylist: true);

        Assert.Contains("--yes-playlist", arguments);
        Assert.DoesNotContain("--no-playlist", arguments);
    }

    [Fact]
    public void BuildDownload_PlaylistFalse_EmitsNoPlaylist()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildDownload(
            "https://video.test/v/1",
            formatId: "22",
            outputTemplate: "out",
            ffmpegPath: string.Empty,
            downloadPlaylist: false);

        Assert.Contains("--no-playlist", arguments);
        Assert.DoesNotContain("--yes-playlist", arguments);
        Assert.Contains("22", arguments);
    }

    [Fact]
    public void BuildDownload_PlaylistWithProxyAndCookies_KeepsAll()
    {
        List<string> arguments = YtDlpArgumentBuilder.BuildDownload(
            "https://video.test/playlist?list=x",
            formatId: null,
            outputTemplate: "out",
            ffmpegPath: string.Empty,
            proxyUrl: "http://127.0.0.1:8080/",
            cookiesFromBrowser: "chrome",
            downloadPlaylist: true);

        Assert.Contains("--yes-playlist", arguments);
        Assert.Contains("--proxy", arguments);
        Assert.Contains("--cookies-from-browser", arguments);
        Assert.Equal("https://video.test/playlist?list=x", arguments[^1]);
    }
}
