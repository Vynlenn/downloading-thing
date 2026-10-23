namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// One finished download, remembered forever (until the user
/// clears history). Powers cross-session duplicate detection:
/// re-added links that already completed are skipped as
/// "already downloaded" instead of fetching them again.
/// </summary>
public sealed record DownloadHistoryEntry(
    string Url,
    string? FileName,
    long? BytesWritten,
    DateTimeOffset CompletedUtc);

/// <summary>
/// Thread-safe in-memory history with a size cap. Persistence
/// lives in Infrastructure; the queue consults Contains through
/// the already-have predicate at enqueue time.
/// </summary>
public sealed class DownloadHistory
{
    public const int MaximumEntries = 5_000;

    private readonly object _sync = new();
    private readonly Dictionary<string, DownloadHistoryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public bool Contains(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        lock (_sync)
        {
            return _entries.ContainsKey(url.Trim());
        }
    }

    /// <returns>False when the URL was already recorded.</returns>
    public bool Record(DownloadHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Url))
        {
            return false;
        }

        lock (_sync)
        {
            if (_entries.ContainsKey(entry.Url))
            {
                return false;
            }

            if (_entries.Count >= MaximumEntries)
            {
                // Drop the oldest completion, not the newest.
                string? oldest = _entries
                    .OrderBy(pair => pair.Value.CompletedUtc)
                    .FirstOrDefault().Key;

                if (oldest is not null)
                {
                    _entries.Remove(oldest);
                }
            }

            _entries[entry.Url] = entry;
            return true;
        }
    }

    public int Clear()
    {
        lock (_sync)
        {
            int count = _entries.Count;
            _entries.Clear();
            return count;
        }
    }

    public IReadOnlyList<DownloadHistoryEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.Values
                .OrderByDescending(entry => entry.CompletedUtc)
                .ToList()
                .AsReadOnly();
        }
    }

    public void Load(IEnumerable<DownloadHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_sync)
        {
            _entries.Clear();

            foreach (DownloadHistoryEntry entry in entries)
            {
                if (
                    string.IsNullOrWhiteSpace(entry.Url) ||
                    _entries.Count >= MaximumEntries)
                {
                    continue;
                }

                _entries.TryAdd(entry.Url, entry);
            }
        }
    }
}
