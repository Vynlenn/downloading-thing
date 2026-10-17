using DeezNutzDownloader.Adapters.Media;

namespace DeezNutzDownloader.Tests;

public sealed class YtDlpProbeParserTests
{
    [Fact]
    public void ParseProbeJson_Video_ExtractsTitleAndSortedFormats()
    {
        const string json = """
            {
                "id": "abc123",
                "title": "Sample video",
                "uploader": "Some channel",
                "duration": 125.5,
                "formats": [
                    {
                        "format_id": "251",
                        "ext": "webm",
                        "vcodec": "none",
                        "acodec": "opus",
                        "filesize": 3200000
                    },
                    {
                        "format_id": "22",
                        "ext": "mp4",
                        "width": 1280,
                        "height": 720,
                        "fps": 30,
                        "filesize": 25000000,
                        "format_note": "720p"
                    },
                    {
                        "format_id": "18",
                        "ext": "mp4",
                        "width": 640,
                        "height": 360,
                        "filesize": 8000000
                    }
                ]
            }
            """;

        MediaProbeResult result =
            YtDlpProbeParser.ParseProbeJson(
                json,
                "https://video.test/watch?v=abc123");

        Assert.Equal("Sample video", result.Title);
        Assert.Equal("Some channel", result.Uploader);
        Assert.Equal(125.5, result.DurationSeconds);
        Assert.False(result.IsPlaylist);
        Assert.Equal(3, result.Formats.Count);

        // Tallest video first, audio-only last.
        Assert.Equal("22", result.Formats[0].FormatId);
        Assert.Equal(720, result.Formats[0].Height);
        Assert.False(result.Formats[0].IsAudioOnly);
        Assert.True(result.Formats[^1].IsAudioOnly);
        Assert.Contains("audio", result.Formats[^1].Label);
    }

    [Fact]
    public void ParseProbeJson_Playlist_ReportsCount()
    {
        const string json = """
            {
                "_type": "playlist",
                "title": "My playlist",
                "playlist_count": 12,
                "entries": [{}, {}, {}]
            }
            """;

        MediaProbeResult result =
            YtDlpProbeParser.ParseProbeJson(
                json,
                "https://video.test/playlist?list=x");

        Assert.True(result.IsPlaylist);
        Assert.Equal(12, result.PlaylistCount);
        Assert.Empty(result.Formats);
        Assert.Contains("playlist", result.Summary);
    }

    [Fact]
    public void ParseProbeJson_MissingTitle_FallsBackToId()
    {
        const string json = """
            { "id": "xyz", "formats": [] }
            """;

        MediaProbeResult result =
            YtDlpProbeParser.ParseProbeJson(
                json,
                "https://video.test/v/xyz");

        Assert.Equal("xyz", result.Title);
        Assert.Empty(result.Formats);
    }

    [Fact]
    public void ResolveExecutable_MissingFile_FallsBackToPath()
    {
        var service = new YtDlpMediaService();

        Assert.Equal(
            "yt-dlp",
            service.ResolveExecutable(string.Empty));

        Assert.Equal(
            "yt-dlp",
            service.ResolveExecutable(
                Path.Combine(
                    Path.GetTempPath(),
                    "no-such-ytdlp-probe.exe")));
    }

    [Fact]
    public async Task IsAvailableAsync_BogusName_ReturnsFalse()
    {
        var service = new YtDlpMediaService();

        Assert.False(
            await service.IsAvailableAsync(
                "definitely-not-a-real-binary-xyz123"));
    }
}
