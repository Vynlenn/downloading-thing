using System.Net;

namespace DeezNutzDownloader.Core.Challenges;

/// <summary>
/// The kind of wall a host put up. Quota walls cannot be solved
/// in a browser (they need waiting or Premium); the rest can be
/// solved once by the user and handed back as cookies.
/// </summary>
public enum ChallengeKind
{
    Captcha,
    BrowserCheck,
    LoginWall,
    QuotaWall,
}

/// <summary>
/// Thrown instead of a generic HTTP error when a host answers with
/// a solvable challenge (CAPTCHA, browser check, login page).
/// The UI treats this as stop-and-prompt: pause the host lane and
/// offer the solve window — never hammer retries at it.
/// </summary>
public sealed class ChallengeDetectedException : Exception
{
    public string Host { get; }

    public ChallengeKind Kind { get; }

    public string PageUrl { get; }

    /// <summary>
    /// False for quota walls: solving in a browser will not help,
    /// so the UI should offer waiting/Premium instead of a browser.
    /// </summary>
    public bool IsSolvableInBrowser { get; }

    public ChallengeDetectedException(
        string host,
        ChallengeKind kind,
        string pageUrl,
        string message,
        bool isSolvableInBrowser = true)
        : base(message)
    {
        Host = host;
        Kind = kind;
        PageUrl = pageUrl;
        IsSolvableInBrowser = isSolvableInBrowser;
    }
}

/// <summary>
/// Pure, testable challenge detection from a status code plus a
/// bounded body snippet. The caller reads only the first kilobytes
/// of an error page — never a full download — before asking.
/// </summary>
public static class ChallengeDetector
{
    private static readonly string[] CaptchaMarkers =
    {
        "g-recaptcha",
        "grecaptcha",
        "cf-turnstile",
        "data-sitekey",
        "hcaptcha.com",
        "recaptcha/api",
        "captcha",
    };

    private static readonly string[] BrowserCheckMarkers =
    {
        "cf-challenge",
        "challenge-platform",
        "cf_clearance",
        "attention required",
        "just a moment",
        "checking your browser",
        "verify you are human",
    };

    private static readonly string[] LoginMarkers =
    {
        "type=\"password\"",
        "type='password'",
        "name=\"login\"",
        "sign in to download",
        "log in to download",
        "premium account",
    };

    /// <returns>The detected challenge, or null when the response
    /// looks like an ordinary error.</returns>
    public static ChallengeKind? Detect(
        HttpStatusCode status,
        string? contentType,
        string? bodySnippet,
        string host)
    {
        // Pixeldrain answers quota exhaustion as 403 on its API/file
        // hosts. That wall cannot be solved in a browser.
        if (
            status == HttpStatusCode.Forbidden &&
            IsPixeldrainHost(host))
        {
            return ChallengeKind.QuotaWall;
        }

        if (
            status != HttpStatusCode.Forbidden &&
            status != HttpStatusCode.Unauthorized)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(bodySnippet))
        {
            return null;
        }

        string body = bodySnippet;

        if (ContainsAny(body, CaptchaMarkers))
        {
            return ChallengeKind.Captcha;
        }

        if (ContainsAny(body, BrowserCheckMarkers))
        {
            return ChallengeKind.BrowserCheck;
        }

        if (ContainsAny(body, LoginMarkers))
        {
            return ChallengeKind.LoginWall;
        }

        return null;
    }

    public static string FriendlyMessage(
        string host,
        ChallengeKind kind,
        string pageUrl)
    {
        string shortHost = string.IsNullOrWhiteSpace(host)
            ? "This host"
            : host;

        _ = pageUrl;

        return kind switch
        {
            ChallengeKind.Captcha =>
                $"{shortHost} is showing a CAPTCHA. Solve it once " +
                $"in the app browser and the queue resumes — " +
                $"nothing is lost.",

            ChallengeKind.BrowserCheck =>
                $"{shortHost} wants a browser check. Complete it " +
                $"once in the app browser and downloads resume.",

            ChallengeKind.LoginWall =>
                $"{shortHost} needs a login for this file. Sign in " +
                $"in the app browser (or import browser cookies) " +
                $"and the queue resumes.",

            ChallengeKind.QuotaWall =>
                $"{shortHost} free quota is exhausted for this IP. " +
                $"Wait for reset or use Premium — a browser cannot " +
                $"solve this one.",

            _ => $"{shortHost} refused the download.",
        };
    }

    private static bool ContainsAny(string body, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (
                body.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPixeldrainHost(string? host)
    {
        return
            !string.IsNullOrWhiteSpace(host) &&
            host.Contains(
                "pixeldrain",
                StringComparison.OrdinalIgnoreCase);
    }
}
