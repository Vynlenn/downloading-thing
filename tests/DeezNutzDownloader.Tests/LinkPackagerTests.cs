using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class LinkPackagerTests
{
    [Fact]
    public void GroupByHost_GroupsValidLinksByHostInOrder()
    {
        LinkAnalysisResult analysis = LinkAnalyzer.Analyze(
            "https://example.test/a.zip\n" +
            "https://other.test/b.zip\n" +
            "https://example.test/c.zip\n" +
            "not a url");

        IReadOnlyList<LinkPackage> packages =
            LinkPackager.GroupByHost(analysis);

        Assert.Equal(2, packages.Count);
        Assert.Equal("example.test", packages[0].Name);
        Assert.Equal(2, packages[0].ValidCount);
        Assert.Equal("other.test", packages[1].Name);
        Assert.Equal(1, packages[1].ValidCount);
    }

    [Fact]
    public void GroupByHost_IgnoresDuplicatesAndInvalid()
    {
        LinkAnalysisResult analysis = LinkAnalyzer.Analyze(
            "https://example.test/a.zip\n" +
            "https://example.test/a.zip\n" +
            "garbage");

        IReadOnlyList<LinkPackage> packages =
            LinkPackager.GroupByHost(analysis);

        LinkPackage package = Assert.Single(packages);

        Assert.Equal("example.test", package.Name);
        Assert.Equal(1, package.ValidCount);
    }

    [Theory]
    [InlineData("example.test", "example.test")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("a/b\\c:d", "c_d")]
    [InlineData("..", "package")]
    [InlineData("", "package")]
    [InlineData("CON", "_CON")]
    public void Sanitize_ProducesSafeFolderNames(
        string candidate,
        string expected)
    {
        Assert.Equal(expected, PackageFolderNames.Sanitize(candidate));
    }

    [Theory]
    [InlineData("example.test", true)]
    [InlineData("my package", true)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData(" trailing ", false)]
    [InlineData("CON", false)]
    public void IsSafeSegment_ValidatesSegments(
        string candidate,
        bool expected)
    {
        Assert.Equal(
            expected,
            PackageFolderNames.IsSafeSegment(candidate));
    }

    [Fact]
    public void EnqueuePackages_SetsPackageFoldersWhenOrganized()
    {
        LinkAnalysisResult analysis = LinkAnalyzer.Analyze(
            "https://example.test/a.zip\n" +
            "https://other.test/b.zip");

        IReadOnlyList<LinkPackage> packages =
            LinkPackager.GroupByHost(analysis);

        var queue = new DownloadQueue();

        DownloadQueueImportResult result =
            queue.EnqueuePackages(packages, organizeIntoSiteFolders: true);

        Assert.Equal(2, result.AddedCount);

        Assert.All(
            queue.Items,
            item =>
            {
                Assert.NotNull(item.PackageName);
                Assert.Equal(item.PackageName, item.RelativeFolder);
            });
    }

    [Fact]
    public void EnqueuePackages_OmitsFoldersWhenDisabled()
    {
        LinkAnalysisResult analysis = LinkAnalyzer.Analyze(
            "https://example.test/a.zip");

        IReadOnlyList<LinkPackage> packages =
            LinkPackager.GroupByHost(analysis);

        var queue = new DownloadQueue();

        queue.EnqueuePackages(packages, organizeIntoSiteFolders: false);

        DownloadQueueItem item = Assert.Single(queue.Items);

        Assert.Equal("example.test", item.PackageName);
        Assert.Null(item.RelativeFolder);
    }
}
