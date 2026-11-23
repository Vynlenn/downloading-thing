using DeezNutzDownloader.Adapters.Media;

namespace DeezNutzDownloader.Tests;

/// <summary>
/// Which files a yt-dlp run produced (and therefore get the Mark
/// of the Web): printed media paths plus this run's sidecars, never
/// unrelated files in the same folder.
/// </summary>
public sealed class YtDlpOutputFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-ytdlp-output-tests",
        Guid.NewGuid().ToString("N"));

    public YtDlpOutputFilesTests()
    {
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
    public void ParsePrintedPaths_KeepsExistingAbsolutePathsOnly()
    {
        string first = Touch("Clip one [a1].mp4");
        string second = Touch("Clip two [b2].webm");

        string stdout =
            "[download]  42.0% of 10.00MiB at 1.00MiB/s ETA 00:05\n" +
            $"\"{first}\"\r\n" +
            "relative/path.mp4\n" +
            Path.Combine(_directory, "missing.mp4") + "\n" +
            second + "\n" +
            first + "\n";

        IReadOnlyList<string> paths =
            YtDlpOutputFiles.ParsePrintedPaths(stdout);

        Assert.Equal(new[] { first, second }, paths);
    }

    [Fact]
    public void ParsePrintedPaths_EmptyOutput_ReturnsNothing()
    {
        Assert.Empty(YtDlpOutputFiles.ParsePrintedPaths(null));
        Assert.Empty(YtDlpOutputFiles.ParsePrintedPaths(""));
    }

    [Fact]
    public void WithSidecars_AddsThisRunsSubtitlesButNotOldOrUnrelatedFiles()
    {
        DateTime started = DateTime.UtcNow.AddSeconds(-2);

        string media = Touch("Talk [x9].mp4");
        string englishSubs = Touch("Talk [x9].en.srt");
        string germanSubs = Touch("Talk [x9].de.srt");
        string unrelated = Touch("Other video [y1].mp4");
        string similarPrefix = Touch("Talk [x9] extra.mp4");
        string oldSidecar = Touch("Talk [x9].old.txt");

        File.SetLastWriteTimeUtc(oldSidecar, started.AddDays(-1));

        IReadOnlyList<string> files =
            YtDlpOutputFiles.WithSidecars(new[] { media }, started);

        Assert.Contains(media, files);
        Assert.Contains(englishSubs, files);
        Assert.Contains(germanSubs, files);
        Assert.DoesNotContain(unrelated, files);
        Assert.DoesNotContain(similarPrefix, files);
        Assert.DoesNotContain(oldSidecar, files);
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void WithSidecars_PlaylistMarksEveryVideo()
    {
        DateTime started = DateTime.UtcNow.AddSeconds(-2);

        string first = Touch("Ep 1 [e1].mp4");
        string second = Touch("Ep 2 [e2].mp4");
        string secondSubs = Touch("Ep 2 [e2].en.srt");

        IReadOnlyList<string> files =
            YtDlpOutputFiles.WithSidecars(new[] { first, second }, started);

        Assert.Equal(
            new[] { first, second, secondSubs }.OrderBy(path => path),
            files.OrderBy(path => path));
    }

    private string Touch(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "x");
        return Path.GetFullPath(path);
    }
}
