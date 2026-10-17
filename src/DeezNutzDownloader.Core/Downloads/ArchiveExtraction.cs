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

        Directory.CreateDirectory(output);

        int extracted = 0;
        int skipped = 0;

        using ZipArchive archive = ZipFile.OpenRead(fullArchive);

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

            if (File.Exists(destination))
            {
                skipped++;
                continue;
            }

            entry.ExtractToFile(destination, overwrite: false);
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
            normalized.Contains("..") ||
            Path.IsPathRooted(normalized) ||
            normalized.IndexOf('\0') >= 0)
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

        return normalized;
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
