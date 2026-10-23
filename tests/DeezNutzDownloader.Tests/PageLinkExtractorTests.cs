using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class PageLinkExtractorTests
{
    private const string PageUrl = "https://forum.test/thread/42";

    [Fact]
    public void Extract_KeepsFileHosts_DropsChrome()
    {
        const string html = """
            <html><body>
            <a href="https://pixeldrain.com/u/abc123">Mirror 1</a>
            <a href="https://gofile.io/d/xyz789">Mirror 2</a>
            <a href="/avatar/user1.png">avatar</a>
            <a href="https://forum.test/logo.svg">logo</a>
            <a href="https://forum.test/styles/main.css">css</a>
            <a href="https://forum.test/thread/43">next thread</a>
            <a href="https://forum.test/members/alice">profile</a>
            <a href="mailto:mod@forum.test">contact</a>
            <a href="javascript:void(0)">menu</a>
            <a href="#comments">comments</a>
            <a href="https://facebook.com/sharer/sharer.php?u=x">share</a>
            <a href="https://img.forum.test/gravatar/abc">gravatar</a>
            </body></html>
            """;

        PageExtractionResult result =
            PageLinkExtractor.Extract(html, PageUrl);

        Assert.Equal(2, result.Files.Count);
        Assert.Contains(
            result.Files,
            file => file.Url == "https://pixeldrain.com/u/abc123");
        Assert.Contains(
            result.Files,
            file => file.Url == "https://gofile.io/d/xyz789");
        Assert.Equal(12, result.AnchorsExamined);
        Assert.Equal(10, result.SkippedCount);
    }

    [Fact]
    public void Extract_KeepsDirectArchives_ResolvesRelative()
    {
        const string html = """
            <html><body>
            <a href="/files/pack.part1.rar">part 1</a>
            <a href="https://cdn.test/vods/episode-7.mkv?token=a1b2">ep7</a>
            <a href="/files/pack.part1.rar">dupe</a>
            <a href="https://cdn.test/cover.jpg">cover art</a>
            </body></html>
            """;

        PageExtractionResult result =
            PageLinkExtractor.Extract(html, PageUrl);

        Assert.Equal(2, result.Files.Count);
        Assert.Contains(
            result.Files,
            file =>
                file.Url ==
                "https://forum.test/files/pack.part1.rar" &&
                file.Reason == "Direct .rar link");
        Assert.Contains(
            result.Files,
            file =>
                file.Url ==
                "https://cdn.test/vods/episode-7.mkv?token=a1b2");
    }

    [Fact]
    public void Extract_PixeldrainImageLink_IsContentNotChrome()
    {
        const string html = """
            <html><body>
            <a href="https://pixeldrain.com/u/scan01">page 1</a>
            </body></html>
            """;

        PageExtractionResult result =
            PageLinkExtractor.Extract(html, PageUrl);

        PageLinkCandidate single = Assert.Single(result.Files);
        Assert.Equal("https://pixeldrain.com/u/scan01", single.Url);
    }

    [Fact]
    public void Extract_SelfLink_IsDropped()
    {
        string html =
            $"<html><body><a href=\"{PageUrl}#reply\">reply</a>" +
            "<a href=\"https://pixeldrain.com/u/keep\">file</a>" +
            "</body></html>";

        PageExtractionResult result =
            PageLinkExtractor.Extract(html, PageUrl);

        PageLinkCandidate single = Assert.Single(result.Files);
        Assert.Equal("https://pixeldrain.com/u/keep", single.Url);
    }

    [Fact]
    public void Extract_BadPageUrl_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PageLinkExtractor.Extract("<a href=x>", "notaurl"));
    }
}
