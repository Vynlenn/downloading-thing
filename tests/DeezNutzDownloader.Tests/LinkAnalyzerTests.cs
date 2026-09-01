using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class LinkAnalyzerTests
{
    [Fact]
    public void Analyze_ReturnsEmptyResultForBlankInput()
    {
        LinkAnalysisResult result =
            LinkAnalyzer.Analyze("  \r\n\t\r\n");

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ValidCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(0, result.InvalidCount);
    }

    [Fact]
    public void Analyze_IgnoresBlankLinesAndKeepsLineNumbers()
    {
        const string input =
            " \r\n" +
            " https://example.test/file.zip \r\n" +
            "\r\n" +
            "not a url";

        LinkAnalysisResult result =
            LinkAnalyzer.Analyze(input);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(1, result.InvalidCount);

        Assert.Equal(2, result.Items[0].LineNumber);
        Assert.Equal(
            LinkAnalysisStatus.Valid,
            result.Items[0].Status);

        Assert.Equal(4, result.Items[1].LineNumber);
        Assert.Equal(
            LinkAnalysisStatus.Invalid,
            result.Items[1].Status);
    }

    [Fact]
    public void Analyze_NormalizesAndDetectsEquivalentDuplicates()
    {
        const string input =
            "HTTPS://EXAMPLE.TEST:443/files/archive.zip#first\n" +
            "https://example.test/files/archive.zip#second";

        LinkAnalysisResult result =
            LinkAnalyzer.Analyze(input);

        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(0, result.InvalidCount);

        const string expected =
            "https://example.test/files/archive.zip";

        Assert.Equal(expected, result.Items[0].NormalizedUrl);
        Assert.Equal(expected, result.Items[1].NormalizedUrl);

        Assert.Equal(
            LinkAnalysisStatus.Duplicate,
            result.Items[1].Status);

        Assert.Contains("line 1", result.Items[1].Detail);
    }

    [Fact]
    public void Analyze_PreservesCaseSensitivePathDifferences()
    {
        const string input =
            "https://example.test/file.zip\n" +
            "https://example.test/File.zip";

        LinkAnalysisResult result =
            LinkAnalyzer.Analyze(input);

        Assert.Equal(2, result.ValidCount);
        Assert.Equal(0, result.DuplicateCount);
    }

    [Theory]
    [InlineData("ftp://example.test/file.zip")]
    [InlineData("relative/path/file.zip")]
    [InlineData("https://example.test/file name.zip")]
    public void Analyze_RejectsUnsupportedOrMalformedAddresses(
        string input)
    {
        LinkAnalysisResult result =
            LinkAnalyzer.Analyze(input);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.InvalidCount);
        Assert.Equal(
            LinkAnalysisStatus.Invalid,
            result.Items[0].Status);
    }
}
