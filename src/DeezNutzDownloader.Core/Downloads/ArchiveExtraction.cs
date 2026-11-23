using System.IO.Compression;

namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// Post-download archive handling. ZIP only (built in): RAR/7z
/// stay as files with a clear message. Extraction is
/// traversal-guarded — entries escaping the folder are skipped,
/// never written.
/// </summary>
public sealed record ArchiveExtractionResult(
    string ArchivePath,
    string OutputDirectory,
    int ExtractedCount,
    int SkippedCount);

public static class ArchiveExtraction
{
    private const int MaximumEntryCount = 100_000;

    private static readonly HashSet<string> ReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9"
        };

    public static bool IsSupportedArchive(string? path)
    {
        return
            !string.IsNullOrWhiteSpace(path) &&
            path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnsupportedArchive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return
            path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts a ZIP next to itself (&lt;name&gt;.extracted/) or into
    /// outputDirectory. Never overwrites existing files — those
    /// count as skipped.
    /// </summary>
    public static ArchiveExtractionResult ExtractZip(
        string archivePath,
        string? outputDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException(
                "An archive path is required.",
                nameof(archivePath));
        }

        string fullArchive = Path.GetFullPath(archivePath);

        if (!File.Exists(fullArchive))
        {
            throw new FileNotFoundException(
                "Archive not found.", fullArchive);
        }

        string output = outputDirectory is null
            ? Path.Combine(
                Path.GetDirectoryName(fullArchive) ?? ".",
                Path.GetFileNameWithoutExtension(fullArchive) +
                    ".extracted")
            : Path.GetFullPath(outputDirectory);

        using ZipArchive archive = ZipFile.OpenRead(fullArchive);

        // Zip-bomb / disk-fill guard: refuse up front when the
        // archive declares more than the drive can take, and cap the
        // entry count. Each entry is also held to its declared size
        // while copying, so a lying header cannot run past it.
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException(
                $"The archive has {archive.Entries.Count:N0} entries " +
                $"(limit {MaximumEntryCount:N0}); it was not extracted.");
        }

        long declaredTotal = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            declaredTotal = SaturatingAdd(declaredTotal, entry.Length);
        }

        Directory.CreateDirectory(output);

        long? freeBytes = TryGetFreeSpace(output);

        if (freeBytes is long free && declaredTotal > free)
        {
            throw new IOException(
                $"The archive unpacks to {FormatBytes(declaredTotal)} " +
                $"but only {FormatBytes(free)} is free on that drive; " +
                "it was not extracted.");
        }

        int extracted = 0;
        int skipped = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string? safeRelative = SafeRelativePath(entry.FullName);

            if (safeRelative is null)
            {
                skipped++;
                continue;
            }

            string destination = Path.GetFullPath(
                Path.Combine(output, safeRelative));

            if (!IsInside(output, destination))
            {
                skipped++;
                continue;
            }

            // Directory entries end in / — ensure and move on.
            if (
                entry.FullName.EndsWith('/') ||
                entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            string? parent = Path.GetDirectoryName(destination);

            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                skipped++;
                continue;
            }

            if (!TryExtractBounded(entry, destination))
            {
                skipped++;
                continue;
            }

            // Explorer carries an archive's Mark of the Web onto what
            // it unpacks; do the same so SmartScreen still checks
            // an .exe that arrived inside a downloaded ZIP.
            MarkOfTheWeb.TryPropagate(fullArchive, destination);
            extracted++;
        }

        return new ArchiveExtractionResult(
            fullArchive,
            output,
            extracted,
            skipped);
    }

    /// <returns>Null when the entry path is unsafe or empty.</returns>
    public static string? SafeRelativePath(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return null;
        }

        string normalized = entryName.Replace('\\', '/').Trim();

        if (
            normalized.Length == 0 ||
            normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            normalized.IndexOf('\0') >= 0 ||
            // "a.txt:payload" writes an alternate data stream.
            normalized.IndexOf(':') >= 0)
        {
            return null;
        }

        foreach (char character in Path.GetInvalidPathChars())
        {
            if (normalized.Contains(character))
            {
                return null;
            }
        }

        string[] segments = normalized
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        foreach (string segment in segments)
        {
            if (!IsSafeSegment(segment))
            {
                return null;
            }
        }

        return normalized;
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment is "." or ".." || segment.Trim().Length == 0)
        {
            return false;
        }

        // Windows silently strips trailing dots/spaces, so "evil.exe."
        // and "evil.exe " alias other names; refuse them.
        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return false;
        }

        foreach (char character in segment)
        {
            if (
                char.IsControl(character) ||
                character is '<' or '>' or '"' or '|' or '?' or '*')
            {
                return false;
            }
        }

        string deviceStem = segment.Split('.', 2)[0].TrimEnd(' ');

        return !ReservedDeviceNames.Contains(deviceStem);
    }

    private static bool TryExtractBounded(
        ZipArchiveEntry entry,
        string destination)
    {
        bool completed = false;

        try
        {
            using Stream source = entry.Open();
            using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920);

            byte[] buffer = new byte[81_920];
            long remaining = entry.Length;

            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);

                if (read == 0)
                {
                    break;
                }

                remaining -= read;

                if (remaining < 0)
                {
                    // More data than the header declared: a bomb or
                    // a corrupt entry. Never let it keep writing.
                    return false;
                }

                target.Write(buffer, 0, read);
            }

            completed = true;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    File.Delete(destination);
                }
                catch (Exception exception)
                    when (
                        exception is IOException or
                        UnauthorizedAccessException)
                {
                    // Best effort cleanup of the partial entry.
                }
            }
        }
    }

    private static long? TryGetFreeSpace(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(directory);

            return string.IsNullOrEmpty(root)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException or
                ArgumentException)
        {
            return null;
        }
    }

    private static long SaturatingAdd(long total, long value)
    {
        if (value <= 0)
        {
            return total;
        }

        return total > long.MaxValue - value
            ? long.MaxValue
            : total + value;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static bool IsInside(string root, string candidate)
    {
        string rooted = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(
            rooted,
            StringComparison.OrdinalIgnoreCase);
    }
}
