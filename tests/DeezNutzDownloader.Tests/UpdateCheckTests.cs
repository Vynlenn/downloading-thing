using DeezNutzDownloader.Core.Updates;

namespace DeezNutzDownloader.Tests;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("owner/repo", true)]
    [InlineData("  owner-name/repo.name  ", true)]
    [InlineData("", false)]
    [InlineData("just-owner", false)]
    [InlineData("a/b/c", false)]
    [InlineData("owner/re po", false)]
    public void IsValidRepository_ValidatesShape(
        string value,
        bool expected)
    {
        Assert.Equal(expected, UpdateCheck.IsValidRepository(value));
    }

    [Fact]
    public void ParseRelease_ReadsTagAndAssets()
    {
        const string json =
            "{\"tag_name\":\"v1.2.3\",\"name\":\"Big one\"," +
            "\"body\":\"Notes here\",\"draft\":false," +
            "\"prerelease\":false," +
            "\"assets\":[" +
            "{\"name\":\"DeezNutzDownloader-portable-1.2.3.zip\"," +
            "\"browser_download_url\":\"https://x.test/u.zip\"," +
            "\"size\":123}," +
            "{\"name\":\"notes.txt\"," +
            "\"browser_download_url\":\"https://x.test/n.txt\"}]}";

        GitHubReleaseInfo? release = UpdateCheck.ParseRelease(json);

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("Notes here", release.Body);
        Assert.Equal(2, release.Assets.Count);
    }

    [Fact]
    public void ParseRelease_BadPayload_ReturnsNull()
    {
        Assert.Null(UpdateCheck.ParseRelease("nope"));
        Assert.Null(UpdateCheck.ParseRelease("{\"tag_name\":\"\"}"));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2")]
    [InlineData("V2.0.0-beta", "2.0.0")]
    public void TryParseVersion_NormalizesTags(
        string tag,
        string expected)
    {
        Assert.Equal(
            Version.Parse(expected),
            UpdateCheck.TryParseVersion(tag));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    public void TryParseVersion_RejectsJunk(string tag)
    {
        Assert.Null(UpdateCheck.TryParseVersion(tag));
    }

    [Fact]
    public void IsNewer_ComparesNumerically()
    {
        Assert.True(UpdateCheck.IsNewer("1.2.3", "v1.2.4"));
        Assert.False(UpdateCheck.IsNewer("1.2.4", "1.2.4"));
        Assert.False(UpdateCheck.IsNewer("2.0.0", "1.9.9"));
        Assert.False(UpdateCheck.IsNewer("junk", "1.0.0"));
    }

    [Fact]
    public void Decide_SkipsDraftsAndPrereleases()
    {
        var draft = new GitHubReleaseInfo(
            "v9.9.9", "d", string.Empty, true, false,
            new[] { ZipAsset() });

        var pre = draft with { Draft = false, Prerelease = true };

        Assert.Null(UpdateCheck.Decide("1.0.0", draft));
        Assert.Null(UpdateCheck.Decide("1.0.0", pre));
        Assert.Null(UpdateCheck.Decide("1.0.0", null));
    }

    [Fact]
    public void Decide_PrefersPortableZip()
    {
        var release = new GitHubReleaseInfo(
            "v1.1.0",
            "r",
            "notes",
            false,
            false,
            new[]
            {
                new ReleaseAsset(
                    "other.zip",
                    "https://x.test/o.zip",
                    1),
                new ReleaseAsset(
                    "DeezNutzDownloader-portable-1.1.0.zip",
                    "https://x.test/p.zip",
                    2),
            });

        UpdateAvailable? decision =
            UpdateCheck.Decide("1.0.0", release);

        Assert.NotNull(decision);
        Assert.Equal("1.1.0", decision.Version);
        Assert.Equal("https://x.test/p.zip", decision.Asset.DownloadUrl);
    }

    [Fact]
    public void Decide_NoZipOrCurrent_Null()
    {
        var noZip = new GitHubReleaseInfo(
            "v2.0.0", "r", string.Empty, false, false,
            new[] { new ReleaseAsset("n.txt", "https://x.test/n", 1) });

        Assert.Null(UpdateCheck.Decide("1.0.0", noZip));

        var current = new GitHubReleaseInfo(
            "v1.0.0", "r", string.Empty, false, false,
            new[] { ZipAsset() });

        Assert.Null(UpdateCheck.Decide("v1.0.0", current));
    }

    [Fact]
    public void InstallerScript_WaitsCopiesRestarts()
    {
        string script = UpdateCheck.BuildInstallerScript(
            4242,
            "C:\\app\\DeezNutzDownloader.App.exe",
            "C:\\temp\\pending",
            "C:\\app");

        Assert.Contains("4242", script);
        Assert.Contains("xcopy", script);
        Assert.Contains("pending", script);
        Assert.Contains("DeezNutzDownloader.App.exe", script);
        Assert.Contains("waitloop", script);
    }

    private static ReleaseAsset ZipAsset()
    {
        return new ReleaseAsset(
            "app-portable-9.9.9.zip",
            "https://x.test/z.zip",
            1);
    }
}
