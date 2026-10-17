namespace DeezNutzDownloader.Core.Challenges;

/// <summary>
/// One cookie captured from a solved challenge. Values are
/// DPAPI-encrypted at rest; this record only carries them in memory.
/// </summary>
public sealed record ChallengeCookie(
    string Name,
    string Value,
    string Path = "/",
    DateTimeOffset? ExpiresUtc = null)
{
    public bool IsExpired(DateTimeOffset nowUtc)
    {
        return
            ExpiresUtc.HasValue &&
            ExpiresUtc.Value <= nowUtc;
    }
}

/// <summary>
/// Where a session came from. BrowserHandoff is the app's own
/// solve window; ImportedBrowser is the user's installed browser.
/// </summary>
public enum ChallengeSessionSource
{
    BrowserHandoff,
    ImportedBrowser,
}

/// <summary>
/// The solved state for one host: cookies to replay on download
/// and crawl requests until they expire.
/// </summary>
public sealed record ChallengeSession(
    string Host,
    IReadOnlyList<ChallengeCookie> Cookies,
    DateTimeOffset AcquiredUtc,
    ChallengeSessionSource Source)
{
    public bool IsUsable(DateTimeOffset nowUtc)
    {
        if (Cookies.Count == 0)
        {
            return false;
        }

        foreach (ChallengeCookie cookie in Cookies)
        {
            if (!cookie.IsExpired(nowUtc))
            {
                return true;
            }
        }

        return false;
    }

    public string ToCookieHeader(DateTimeOffset nowUtc)
    {
        return string.Join(
            "; ",
            Cookies
                .Where(cookie => !cookie.IsExpired(nowUtc))
                .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }
}

/// <summary>
/// Persisted, host-scoped challenge sessions. Implementations keep
/// cookie values encrypted and prune expired sessions.
/// </summary>
public interface IChallengeSessionStore
{
    Task SaveAsync(
        ChallengeSession session,
        CancellationToken cancellationToken = default);

    Task<ChallengeSession?> GetAsync(
        string host,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string host,
        CancellationToken cancellationToken = default);

    Task ClearAsync(
        CancellationToken cancellationToken = default);
}
