using System.Text.Json;

namespace DeezNutzDownloader.Core.Downloads;

/// <summary>A byte range of a segmented download and how much of it is on disk.</summary>
public sealed record PartialSegment(long Start, long Confirmed, long End);

/// <summary>
/// What a resumable download needs to continue after a cancel,
/// pause, quota stop, crash or app restart. Written next to the
/// .part file; the If-Range validator makes sure the bytes on disk
/// still belong to the same version of the file on the server.
/// </summary>
public sealed record PartialDownloadState(
    int Version,
    string SourceUrl,
    long? TotalBytes,
    string? ETag,
    DateTimeOffset? LastModified,
    string? FileName,
    long ContiguousBytes,
    IReadOnlyList<PartialSegment>? Segments)
{
    public const int CurrentVersion = 1;

    /// <summary>Bytes already saved, whichever mode wrote them.</summary>
    public long ConfirmedBytes =>
        Segments is { Count: > 0 } segments
            ? segments.Sum(segment =>
                Math.Max(0, segment.Confirmed - segment.Start))
            : ContiguousBytes;

    public bool HasValidator =>
        !string.IsNullOrEmpty(ETag) || LastModified.HasValue;
}

/// <summary>
/// File naming and persistence for resumable downloads. The key is
/// the queue item's id, so a partial file can always be traced back
/// to (and cleaned up with) its queue row.
/// </summary>
public static class PartialDownloadFiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string KeyFor(Guid itemId) => itemId.ToString("N");

    /// <summary>32 hex characters: keeps keys from ever forming a path.</summary>
    public static bool IsValidKey(string? key)
    {
        return
            key is { Length: 32 } &&
            key.All(Uri.IsHexDigit);
    }

    public static string PartPath(string directory, string key)
    {
        RequireKey(key);
        return Path.Combine(directory, $".download-{key}.part");
    }

    public static string StatePath(string directory, string key)
    {
        RequireKey(key);
        return Path.Combine(directory, $".download-{key}.state.json");
    }

    public static PartialDownloadState? TryLoad(string directory, string key)
    {
        try
        {
            string statePath = StatePath(directory, key);

            if (
                !File.Exists(statePath) ||
                !File.Exists(PartPath(directory, key)))
            {
                return null;
            }

            PartialDownloadState? state =
                JsonSerializer.Deserialize<PartialDownloadState>(
                    File.ReadAllText(statePath),
                    JsonOptions);

            return state is { Version: PartialDownloadState.CurrentVersion }
                ? state
                : null;
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Atomic replace so a crash mid-save never leaves half a file.</summary>
    public static void Save(
        string directory,
        string key,
        PartialDownloadState state)
    {
        string statePath = StatePath(directory, key);
        string temporary = statePath + ".tmp";

        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(state, JsonOptions));

        File.Move(temporary, statePath, overwrite: true);
    }

    public static bool TrySave(
        string directory,
        string key,
        PartialDownloadState state)
    {
        try
        {
            Save(directory, key, state);
            return true;
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes the partial data and its state. Best effort.</summary>
    public static void Delete(string directory, string key)
    {
        if (!IsValidKey(key))
        {
            return;
        }

        foreach (
            string path in new[]
            {
                PartPath(directory, key),
                StatePath(directory, key),
                StatePath(directory, key) + ".tmp"
            })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
                when (
                    exception is IOException or
                    UnauthorizedAccessException)
            {
            }
        }
    }

    public static void DeleteState(string directory, string key)
    {
        try
        {
            string statePath = StatePath(directory, key);

            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException)
        {
        }
    }

    private static void RequireKey(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException(
                "A resume key must be 32 hex characters.",
                nameof(key));
        }
    }
}
