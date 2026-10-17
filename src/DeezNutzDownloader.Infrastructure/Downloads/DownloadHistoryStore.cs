using System.Text.Json;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Infrastructure.Configuration;

namespace DeezNutzDownloader.Infrastructure.Downloads;

/// <summary>
/// Persists download history as JSON beside the queue file.
/// Atomic temp-file writes; corrupt files reset to empty rather
/// than wedging the app (history is re-derivable from future
/// downloads).
/// </summary>
public sealed class DownloadHistoryStore
{
    private readonly AppPaths? _paths;
    private readonly string _historyFile;

    public DownloadHistoryStore(AppPaths paths)
    {
        _paths = paths ??
            throw new ArgumentNullException(nameof(paths));
        _historyFile = paths.DownloadHistoryFile;
    }

    public DownloadHistoryStore(string historyFile)
    {
        if (string.IsNullOrWhiteSpace(historyFile))
        {
            throw new ArgumentException(
                "A history file path is required.",
                nameof(historyFile));
        }

        _historyFile = Path.GetFullPath(historyFile);

        string? directory = Path.GetDirectoryName(_historyFile);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string HistoryFile => _historyFile;

    public async Task<DownloadHistory> LoadAsync()
    {
        var history = new DownloadHistory();

        string file = HistoryFile;

        if (!File.Exists(file))
        {
            return history;
        }

        try
        {
            string json = await File.ReadAllTextAsync(file)
                .ConfigureAwait(false);

            var entries =
                JsonSerializer.Deserialize<
                    List<DownloadHistoryEntry>>(json);

            if (entries is not null)
            {
                history.Load(entries);
            }
        }
        catch
        {
            // Corrupt history resets to empty; future downloads
            // rebuild it.
        }

        return history;
    }

    public async Task SaveAsync(DownloadHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        string file = HistoryFile;

        string? directory = Path.GetDirectoryName(file);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Persist newest-first, capped, so the file stays small.
        List<DownloadHistoryEntry> entries = history
            .Snapshot()
            .Take(DownloadHistory.MaximumEntries)
            .ToList();

        string json = JsonSerializer.Serialize(
            entries,
            new JsonSerializerOptions { WriteIndented = true });

        string temporaryFile = file + ".tmp";
        await File.WriteAllTextAsync(temporaryFile, json)
            .ConfigureAwait(false);
        File.Move(temporaryFile, file, true);
    }
}
