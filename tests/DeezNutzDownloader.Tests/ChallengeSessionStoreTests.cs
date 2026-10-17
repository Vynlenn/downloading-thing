using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Infrastructure.Challenges;

namespace DeezNutzDownloader.Tests;

public sealed class ChallengeSessionStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly ChallengeSessionStore _store;

    public ChallengeSessionStoreTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dnd-challenge-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);

        _store = new ChallengeSessionStore(
            Path.Combine(_directory, "sessions.json"));
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
    public async Task SaveAndGet_RoundTripsCookies()
    {
        var session = new ChallengeSession(
            "Example.COM.",
            new[]
            {
                new ChallengeCookie("cf_clearance", "abc123"),
                new ChallengeCookie(
                    "sess",
                    "xyz",
                    "/",
                    DateTimeOffset.UtcNow.AddHours(2)),
            },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff);

        await _store.SaveAsync(session);

        ChallengeSession? loaded =
            await _store.GetAsync("https://example.com/f/1");

        Assert.NotNull(loaded);
        Assert.Equal("example.com", loaded.Host);
        Assert.Equal("cf_clearance=abc123; sess=xyz",
            loaded.ToCookieHeader(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SavedFile_HoldsNoCleartextSecrets()
    {
        var session = new ChallengeSession(
            "example.com",
            new[] { new ChallengeCookie("sekret", "super-secret-value") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff);

        await _store.SaveAsync(session);

        string raw = await File.ReadAllTextAsync(
            Path.Combine(_directory, "sessions.json"));

        Assert.DoesNotContain("super-secret-value", raw);
    }

    [Fact]
    public async Task Get_ExpiredSession_ReturnsNull()
    {
        var session = new ChallengeSession(
            "example.com",
            new[]
            {
                new ChallengeCookie(
                    "old",
                    "gone",
                    "/",
                    DateTimeOffset.UtcNow.AddMinutes(-5)),
            },
            DateTimeOffset.UtcNow.AddHours(-1),
            ChallengeSessionSource.BrowserHandoff);

        await _store.SaveAsync(session);

        // Save prunes expired cookies at write; nothing usable
        // remains, so the read reports no session.
        Assert.Null(await _store.GetAsync("example.com"));
    }

    [Fact]
    public async Task Remove_DeletesHostSession()
    {
        var session = new ChallengeSession(
            "example.com",
            new[] { new ChallengeCookie("a", "1") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.ImportedBrowser);

        await _store.SaveAsync(session);
        Assert.NotNull(await _store.GetAsync("example.com"));

        await _store.RemoveAsync("example.com");
        Assert.Null(await _store.GetAsync("example.com"));
    }

    [Fact]
    public async Task Clear_RemovesAllSessions()
    {
        await _store.SaveAsync(new ChallengeSession(
            "a.com",
            new[] { new ChallengeCookie("a", "1") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff));

        await _store.SaveAsync(new ChallengeSession(
            "b.com",
            new[] { new ChallengeCookie("b", "2") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff));

        await _store.ClearAsync();

        Assert.Null(await _store.GetAsync("a.com"));
        Assert.Null(await _store.GetAsync("b.com"));
    }

    [Fact]
    public async Task Save_ReplacesExistingHostSession()
    {
        await _store.SaveAsync(new ChallengeSession(
            "example.com",
            new[] { new ChallengeCookie("a", "1") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.BrowserHandoff));

        await _store.SaveAsync(new ChallengeSession(
            "example.com",
            new[] { new ChallengeCookie("b", "2") },
            DateTimeOffset.UtcNow,
            ChallengeSessionSource.ImportedBrowser));

        ChallengeSession? loaded =
            await _store.GetAsync("example.com");

        Assert.NotNull(loaded);
        Assert.Equal(
            "b=2",
            loaded.ToCookieHeader(DateTimeOffset.UtcNow));
        Assert.Equal(
            ChallengeSessionSource.ImportedBrowser,
            loaded.Source);
    }
}
