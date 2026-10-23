using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Challenges;

namespace DeezNutzDownloader.Tests;

public sealed class ChallengeCookieHandlerTests
{
    [Fact]
    public void CandidateHosts_WalksUpWithoutBareTld()
    {
        Assert.Equal(
            new[] { "dl.pixeldrain.com", "pixeldrain.com" },
            ChallengeCookieHandler.CandidateHosts("dl.pixeldrain.com"));

        Assert.Equal(
            new[] { "example.com" },
            ChallengeCookieHandler.CandidateHosts("example.com"));

        Assert.Empty(
            ChallengeCookieHandler.CandidateHosts("  "));
    }

    [Fact]
    public void HostMatches_CoversExactAndSubdomain()
    {
        Assert.True(
            ChallengeCookieHandler.HostMatches(
                "pixeldrain.com", "pixeldrain.com"));

        Assert.True(
            ChallengeCookieHandler.HostMatches(
                "dl.pixeldrain.com", "pixeldrain.com"));

        Assert.False(
            ChallengeCookieHandler.HostMatches(
                "notpixeldrain.com", "pixeldrain.com"));

        Assert.False(
            ChallengeCookieHandler.HostMatches(null, "pixeldrain.com"));
    }

    [Fact]
    public void BuildCookieHeader_SkipsExpired()
    {
        var session = new ChallengeSession(
            "example.com",
            new[]
            {
                new ChallengeCookie("live", "1"),
                new ChallengeCookie(
                    "dead",
                    "2",
                    "/",
                    DateTimeOffset.UtcNow.AddMinutes(-1)),
            },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff);

        Assert.Equal(
            "live=1",
            ChallengeCookieHandler.BuildCookieHeader(
                session,
                DateTimeOffset.UtcNow));

        Assert.Null(
            ChallengeCookieHandler.BuildCookieHeader(
                null,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SendAsync_AttachesStoredCookies()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(new ChallengeSession(
            "example.com",
            new[] { new ChallengeCookie("cf_clearance", "solved!") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff));

        var inner = new CaptureHandler();
        var handler = new ChallengeCookieHandler(store, inner);
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(
            "https://example.com/file/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "cf_clearance=solved!",
            inner.LastRequest?.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task SendAsync_Subdomain_ReusesParentSolve()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(new ChallengeSession(
            "pixeldrain.com",
            new[] { new ChallengeCookie("pd", "1") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff));

        var inner = new CaptureHandler();
        using var client = new HttpClient(
            new ChallengeCookieHandler(store, inner));

        await client.GetAsync("https://dl.pixeldrain.com/file/1");

        Assert.Equal(
            "pd=1",
            inner.LastRequest?.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task SendAsync_NoSession_SendsNoCookieHeader()
    {
        var inner = new CaptureHandler();
        using var client = new HttpClient(
            new ChallengeCookieHandler(
                new InMemorySessionStore(),
                inner));

        await client.GetAsync("https://example.com/file/1");

        Assert.False(
            inner.LastRequest!.Headers.Contains("Cookie"));
    }

    private sealed class InMemorySessionStore : IChallengeSessionStore
    {
        private readonly Dictionary<string, ChallengeSession> _sessions =
            new(StringComparer.OrdinalIgnoreCase);

        public Task SaveAsync(
            ChallengeSession session,
            CancellationToken cancellationToken = default)
        {
            _sessions[session.Host.Trim().ToLowerInvariant()] = session;
            return Task.CompletedTask;
        }

        public Task<ChallengeSession?> GetAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            _sessions.TryGetValue(
                host.Trim().ToLowerInvariant(),
                out ChallengeSession? session);

            return Task.FromResult(
                session is not null &&
                session.IsUsable(DateTimeOffset.UtcNow)
                    ? session
                    : null);
        }

        public Task RemoveAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            _sessions.Remove(host.Trim().ToLowerInvariant());
            return Task.CompletedTask;
        }

        public Task ClearAsync(
            CancellationToken cancellationToken = default)
        {
            _sessions.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
