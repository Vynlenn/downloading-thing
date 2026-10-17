using System.Net.Http.Headers;
using DeezNutzDownloader.Core.Challenges;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Replays solved-challenge cookies for matching hosts. Read-only
/// and best effort: a missing/expired session simply means no
/// Cookie header, and a 403/401 downstream still surfaces as a
/// typed <see cref="ChallengeDetectedException"/> from the
/// transfer service so the UI can prompt a fresh solve.
/// </summary>
public sealed class ChallengeCookieHandler : DelegatingHandler
{
    private readonly IChallengeSessionStore? _sessionStore;

    public ChallengeCookieHandler(
        IChallengeSessionStore? sessionStore,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _sessionStore = sessionStore;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (
            _sessionStore is not null &&
            request.RequestUri is { IsAbsoluteUri: true } uri &&
            !request.Headers.Contains("Cookie"))
        {
            try
            {
                // Walk up: dl.pixeldrain.com reuses a
                // pixeldrain.com solve.
                foreach (string candidate in CandidateHosts(uri.Host))
                {
                    ChallengeSession? session =
                        await _sessionStore.GetAsync(
                                candidate,
                                cancellationToken)
                            .ConfigureAwait(false);

                    string? header = session?.ToCookieHeader(
                        DateTimeOffset.UtcNow);

                    if (!string.IsNullOrWhiteSpace(header))
                    {
                        request.Headers.TryAddWithoutValidation(
                            "Cookie",
                            header);
                        break;
                    }
                }
            }
            catch
            {
                // Cookie replay must never break a download.
                // Without cookies the request proceeds as before.
            }
        }

        return await base.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the header value without sending traffic (for tests
    /// and diagnostics).
    /// </summary>
    public static string? BuildCookieHeader(
        ChallengeSession? session,
        DateTimeOffset nowUtc)
    {
        string? header = session?.ToCookieHeader(nowUtc);
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }

    /// <summary>
    /// Candidate lookup hosts, most specific first:
    /// dl.pixeldrain.com, pixeldrain.com, com is skipped.
    /// </summary>
    public static IEnumerable<string> CandidateHosts(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            yield break;
        }

        string current = host.Trim().TrimEnd('.').ToLowerInvariant();

        while (true)
        {
            yield return current;

            int dot = current.IndexOf('.');

            if (dot < 0)
            {
                yield break;
            }

            current = current[(dot + 1)..];

            // A bare TLD ("com") is never a session host.
            if (!current.Contains('.'))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// True when the host part of the URL matches the session host
    /// (exact or subdomain), so dl.pixeldrain.com reuses a
    /// pixeldrain.com solve.
    /// </summary>
    public static bool HostMatches(
        string? requestHost,
        string? sessionHost)
    {
        if (
            string.IsNullOrWhiteSpace(requestHost) ||
            string.IsNullOrWhiteSpace(sessionHost))
        {
            return false;
        }

        string request = requestHost.Trim().TrimEnd('.');
        string session = sessionHost.Trim().TrimEnd('.');

        return
            request.Equals(session, StringComparison.OrdinalIgnoreCase) ||
            request.EndsWith(
                "." + session,
                StringComparison.OrdinalIgnoreCase);
    }
}
