using System.Text.Json;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Infrastructure.Configuration;
using DeezNutzDownloader.Infrastructure.Security;

namespace DeezNutzDownloader.Infrastructure.Challenges;

/// <summary>
/// File-backed challenge sessions. Cookie values are DPAPI-encrypted
/// (current user) before they touch disk; the file never holds a
/// cleartext secret. Expired cookies/sessions are pruned on load.
/// </summary>
public sealed class ChallengeSessionStore : IChallengeSessionStore
{
    private readonly string _sessionsFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChallengeSessionStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _sessionsFile = paths.ChallengeSessionsFile;
        paths.EnsureDirectoriesExist();
    }

    public ChallengeSessionStore(string sessionsFile)
    {
        if (string.IsNullOrWhiteSpace(sessionsFile))
        {
            throw new ArgumentException(
                "A sessions file path is required.",
                nameof(sessionsFile));
        }

        _sessionsFile = Path.GetFullPath(sessionsFile);

        string? directory = Path.GetDirectoryName(_sessionsFile);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task SaveAsync(
        ChallengeSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        string host = NormalizeHost(session.Host);

        if (host.Length == 0)
        {
            throw new ArgumentException(
                "A session host is required.",
                nameof(session));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<StoredSession> stored = await LoadCoreAsync()
                .ConfigureAwait(false);

            stored.RemoveAll(
                candidate =>
                    string.Equals(
                        candidate.Host,
                        host,
                        StringComparison.OrdinalIgnoreCase));

            stored.Add(ToStored(session, host));

            await SaveCoreAsync(stored).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChallengeSession?> GetAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeHost(host);

        if (normalized.Length == 0)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<StoredSession> stored = await LoadCoreAsync()
                .ConfigureAwait(false);

            StoredSession? match = stored.FirstOrDefault(
                candidate =>
                    string.Equals(
                        candidate.Host,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return null;
            }

            ChallengeSession? session = FromStored(match);

            if (session is null)
            {
                // Undecryptable or fully expired: drop it so a
                // stale entry can never wedge the queue.
                stored.Remove(match);
                await SaveCoreAsync(stored).ConfigureAwait(false);
                return null;
            }

            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeHost(host);

        if (normalized.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<StoredSession> stored = await LoadCoreAsync()
                .ConfigureAwait(false);

            int removed = stored.RemoveAll(
                candidate =>
                    string.Equals(
                        candidate.Host,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                await SaveCoreAsync(stored).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await SaveCoreAsync(new List<StoredSession>())
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<StoredSession>> LoadCoreAsync()
    {
        if (!File.Exists(_sessionsFile))
        {
            return new List<StoredSession>();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_sessionsFile)
                .ConfigureAwait(false);

            var stored = JsonSerializer.Deserialize<List<StoredSession>>(json);

            return stored ?? new List<StoredSession>();
        }
        catch
        {
            // A corrupt sessions file must never wedge downloads:
            // start empty (cookies are re-solvable).
            return new List<StoredSession>();
        }
    }

    private async Task SaveCoreAsync(List<StoredSession> stored)
    {
        string json = JsonSerializer.Serialize(
            stored,
            new JsonSerializerOptions { WriteIndented = true });

        string temporaryFile = _sessionsFile + ".tmp";
        await File.WriteAllTextAsync(temporaryFile, json)
            .ConfigureAwait(false);
        File.Move(temporaryFile, _sessionsFile, true);
    }

    private static StoredSession ToStored(
        ChallengeSession session,
        string normalizedHost)
    {
        var cookies = session.Cookies
            .Where(cookie =>
                !string.IsNullOrWhiteSpace(cookie.Name) &&
                !cookie.IsExpired(DateTimeOffset.UtcNow))
            .Select(cookie => new StoredCookie(
                cookie.Name,
                ProxyCredentialProtector.Protect(cookie.Value),
                cookie.Path,
                cookie.ExpiresUtc))
            .ToList();

        return new StoredSession(
            normalizedHost,
            cookies,
            session.AcquiredUtc,
            session.Source.ToString());
    }

    private static ChallengeSession? FromStored(StoredSession stored)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cookies = new List<ChallengeCookie>();

        foreach (StoredCookie cookie in stored.Cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name))
            {
                continue;
            }

            string? value;

            try
            {
                value = ProxyCredentialProtector.Unprotect(
                    cookie.ProtectedValue);
            }
            catch (InvalidOperationException)
            {
                // Another user/PC or corrupt entry: the whole
                // session is unusable.
                return null;
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var restored = new ChallengeCookie(
                cookie.Name,
                value,
                string.IsNullOrWhiteSpace(cookie.Path)
                    ? "/"
                    : cookie.Path,
                cookie.ExpiresUtc);

            if (!restored.IsExpired(now))
            {
                cookies.Add(restored);
            }
        }

        if (cookies.Count == 0)
        {
            return null;
        }

        _ = Enum.TryParse(
            stored.Source,
            out ChallengeSessionSource source);

        return new ChallengeSession(
            stored.Host,
            cookies.AsReadOnly(),
            stored.AcquiredUtc,
            source);
    }

    private static string NormalizeHost(string? host)
    {
        host = host?.Trim().TrimEnd('.') ?? string.Empty;

        if (
            Uri.TryCreate(
                host,
                UriKind.Absolute,
                out Uri? uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host.ToLowerInvariant();
        }

        return host.ToLowerInvariant();
    }

    private sealed record StoredSession(
        string Host,
        List<StoredCookie> Cookies,
        DateTimeOffset AcquiredUtc,
        string Source);

    private sealed record StoredCookie(
        string Name,
        string ProtectedValue,
        string Path,
        DateTimeOffset? ExpiresUtc);
}
