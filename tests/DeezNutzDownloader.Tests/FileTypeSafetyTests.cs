using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class FileTypeSafetyTests
{
    [Theory]
    [InlineData("invoice.pdf.exe", ".pdf", ".exe")]
    [InlineData("Holiday Photo.JPG.scr", ".jpg", ".scr")]
    [InlineData("movie.mp4.bat", ".mp4", ".bat")]
    [InlineData("readme.txt.js", ".txt", ".js")]
    [InlineData("report.docx.lnk", ".docx", ".lnk")]
    [InlineData("budget.xlsx.xlsm", ".xlsx", ".xlsm")]
    [InlineData("invoice.pdf .exe", ".pdf", ".exe")]
    [InlineData("invoice.pdf.exe.", ".pdf", ".exe")]
    public void DisguisedPrograms_AreDanger(
        string name,
        string decoy,
        string real)
    {
        FileSafetyAssessment result = FileTypeSafety.Assess(name);

        Assert.Equal(FileSafetyLevel.Danger, result.Level);
        Assert.Contains($"looks like a {decoy}", result.Message);
        Assert.Contains($"is a {real}", result.Message);
    }

    [Theory]
    [InlineData("setup.exe", "Program file")]
    [InlineData("Installer-2.1.0.msi", "Program file")]
    [InlineData("tool.v2.ps1", "Program file")]
    [InlineData("C:\\Downloads\\run.BAT", "Program file")]
    [InlineData("shortcut.url", "Program file")]
    [InlineData("quarterly.xlsm", "macros")]
    [InlineData("ubuntu-24.04.iso", "Disk image")]
    public void RiskyTypes_AreCaution(string name, string expectedText)
    {
        FileSafetyAssessment result = FileTypeSafety.Assess(name);

        Assert.Equal(FileSafetyLevel.Caution, result.Level);
        Assert.Contains(expectedText, result.Message);
    }

    [Theory]
    [InlineData("holiday.mp4")]
    [InlineData("archive.tar.gz")]
    [InlineData("report.pdf")]
    [InlineData("song.mp3")]
    [InlineData("Talk [x9].en.srt")]
    [InlineData(".bashrc")]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryFiles_HaveNoWarning(string? name)
    {
        FileSafetyAssessment result = FileTypeSafety.Assess(name);

        Assert.Equal(FileSafetyLevel.None, result.Level);
        Assert.Null(result.Message);
    }

    [Fact]
    public void QueueItem_WarningFollowsTheSavedName()
    {
        // The URL suggested a PDF; the server's real name is a
        // disguised program. The row must switch to the danger
        // warning once the saved name is known.
        var item = new DownloadQueueItem(
            Guid.NewGuid(),
            "https://example.test/files/invoice.pdf",
            "invoice.pdf");

        Assert.Null(item.SafetyWarning);

        DownloadQueueItem finished = item with
        {
            State = DownloadQueueItemState.Completed,
            SavedFileName = "invoice.pdf.exe"
        };

        Assert.True(finished.IsDangerous);
        Assert.NotNull(finished.SafetyWarning);
    }

    [Fact]
    public void QueueItem_SafetyIsNotPersisted()
    {
        var item = new DownloadQueueItem(
            Guid.NewGuid(),
            "https://example.test/setup.exe",
            "setup.exe");

        string json = System.Text.Json.JsonSerializer.Serialize(item);

        Assert.DoesNotContain("Safety", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDangerous", json, StringComparison.Ordinal);
    }
}
