using System.Net;
using DeezNutzDownloader.Core.Challenges;

namespace DeezNutzDownloader.Tests;

public sealed class ChallengeDetectorTests
{
    [Fact]
    public void Detect_CloudflareCaptchaPage_ReturnsCaptcha()
    {
        string body =
            "<html><div class=\"g-recaptcha\" " +
            "data-sitekey=\"abc\"></div></html>";

        Assert.Equal(
            ChallengeKind.Captcha,
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "text/html",
                body,
                "example.com"));
    }

    [Fact]
    public void Detect_TurnstilePage_ReturnsCaptcha()
    {
        string body =
            "<html><div class=\"cf-turnstile\"></div></html>";

        Assert.Equal(
            ChallengeKind.Captcha,
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "text/html",
                body,
                "files.example.com"));
    }

    [Fact]
    public void Detect_JustAMomentPage_ReturnsBrowserCheck()
    {
        string body =
            "<html><title>Just a moment...</title>" +
            "<div id=\"challenge-platform\"></div></html>";

        Assert.Equal(
            ChallengeKind.BrowserCheck,
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "text/html",
                body,
                "example.com"));
    }

    [Fact]
    public void Detect_LoginForm_ReturnsLoginWall()
    {
        string body =
            "<html><form><input type=\"password\" " +
            "name=\"pw\"></form></html>";

        Assert.Equal(
            ChallengeKind.LoginWall,
            ChallengeDetector.Detect(
                HttpStatusCode.Unauthorized,
                "text/html",
                body,
                "example.com"));
    }

    [Fact]
    public void Detect_Pixeldrain403_IsAlwaysQuotaWall()
    {
        // Pixeldrain quota exhaustion must never open a browser:
        // solving cannot help, only waiting or Premium.
        Assert.Equal(
            ChallengeKind.QuotaWall,
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "application/json",
                "{\"success\":false}",
                "pixeldrain.com"));

        Assert.Equal(
            ChallengeKind.QuotaWall,
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "text/html",
                "<div class=\"g-recaptcha\"></div>",
                "dl.pixeldrain.com"));
    }

    [Fact]
    public void Detect_Plain403WithoutMarkers_ReturnsNull()
    {
        Assert.Null(
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "text/html",
                "<html><h1>Forbidden</h1></html>",
                "example.com"));
    }

    [Fact]
    public void Detect_EmptyBody_ReturnsNull()
    {
        Assert.Null(
            ChallengeDetector.Detect(
                HttpStatusCode.Forbidden,
                "text/html",
                string.Empty,
                "example.com"));
    }

    [Fact]
    public void Detect_Non403Status_ReturnsNull()
    {
        Assert.Null(
            ChallengeDetector.Detect(
                HttpStatusCode.ServiceUnavailable,
                "text/html",
                "<div class=\"g-recaptcha\"></div>",
                "example.com"));
    }

    [Fact]
    public void FriendlyMessage_Captcha_MentionsSolveOnce()
    {
        string message = ChallengeDetector.FriendlyMessage(
            "example.com",
            ChallengeKind.Captcha,
            "https://example.com/f/1");

        Assert.Contains("CAPTCHA", message);
        Assert.Contains("example.com", message);
    }

    [Fact]
    public void FriendlyMessage_QuotaWall_SaysBrowserCannotHelp()
    {
        string message = ChallengeDetector.FriendlyMessage(
            "pixeldrain.com",
            ChallengeKind.QuotaWall,
            "https://pixeldrain.com/api/file/1");

        Assert.Contains("Premium", message);
    }

    [Fact]
    public void Session_IsUsable_RequiresLiveCookie()
    {
        var expired = new ChallengeSession(
            "example.com",
            new[]
            {
                new ChallengeCookie(
                    "cf_clearance",
                    "gone",
                    "/",
                    DateTimeOffset.UtcNow.AddMinutes(-1)),
            },
            DateTimeOffset.UtcNow.AddHours(-1),
            ChallengeSessionSource.BrowserHandoff);

        Assert.False(expired.IsUsable(DateTimeOffset.UtcNow));
        Assert.Equal(string.Empty, expired.ToCookieHeader(DateTimeOffset.UtcNow));

        var live = new ChallengeSession(
            "example.com",
            new[]
            {
                new ChallengeCookie("a", "1"),
                new ChallengeCookie(
                    "b",
                    "2",
                    "/",
                    DateTimeOffset.UtcNow.AddHours(1)),
            },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff);

        Assert.True(live.IsUsable(DateTimeOffset.UtcNow));
        Assert.Equal("a=1; b=2", live.ToCookieHeader(DateTimeOffset.UtcNow));
    }
}
