using System.IO.Compression;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class ArchiveExtractionTests : IDisposable
{
    private readonly string _directory;

    public ArchiveExtractionTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-archive-tests",
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
    public void IsSupportedArchive_ZipOnly()
    {
        Assert.True(ArchiveExtraction.IsSupportedArchive("a.ZIP"));
        Assert.False(ArchiveExtraction.IsSupportedArchive("a.rar"));
        Assert.False(ArchiveExtraction.IsSupportedArchive("a.7z"));
        Assert.False(ArchiveExtraction.IsSupportedArchive(null));
    }

    [Fact]
    public void SafeRelativePath_BlocksTraversal()
    {
        Assert.Null(ArchiveExtraction.SafeRelativePath("../evil.exe"));
        Assert.Null(ArchiveExtraction.SafeRelativePath("/abs/path"));
        Assert.Null(ArchiveExtraction.SafeRelativePath("C:\\win\\x"));
        Assert.Null(ArchiveExtraction.SafeRelativePath("   "));
        Assert.NotNull(
            ArchiveExtraction.SafeRelativePath("folder/ok.txt"));
    }

    [Fact]
    public void ExtractZip_HappyPath_ExtractsFiles()
    {
        string zip = Path.Combine(_directory, "pack.zip");

        using (ZipArchive archive = ZipFile.Open(
            zip, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry =
                archive.CreateEntry("hello.txt");
            using var writer = new StreamWriter(
                entry.Open());
            writer.Write("hi");
        }

        ArchiveExtractionResult result =
            ArchiveExtraction.ExtractZip(zip);

        Assert.Equal(1, result.ExtractedCount);
        Assert.Equal("hi", File.ReadAllText(
            Path.Combine(result.OutputDirectory, "hello.txt")));
    }

    [Fact]
    public void ExtractZip_NoOverwrite_CountsSkipped()
    {
        string zip = Path.Combine(_directory, "pack.zip");

        using (ZipArchive archive = ZipFile.Open(
            zip, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "a.txt", "b.txt" })
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(name);
            }
        }

        ArchiveExtractionResult first =
            ArchiveExtraction.ExtractZip(zip);

        Assert.Equal(2, first.ExtractedCount);

        ArchiveExtractionResult second =
            ArchiveExtraction.ExtractZip(
                zip,
                first.OutputDirectory);

        Assert.Equal(0, second.ExtractedCount);
        Assert.Equal(2, second.SkippedCount);
    }
}
