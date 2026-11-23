namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// A link answered with a web page where a file was expected (a
/// share page, an album page, a login wall). Saving that page as a
/// "completed download" hid the problem, so it is an error instead.
/// </summary>
public sealed class NotAFileException : Exception
{
    public NotAFileException(string host, string message)
        : base(message)
    {
        Host = host;
    }

    public string Host { get; }
}

/// <summary>
/// Gives extension-less downloads the right extension from their
/// first bytes (or the server's content type), so a PNG saved as
/// "89d50f21" becomes "89d50f21.png" and opens with a double-click.
/// Only ever adds media, document and archive extensions: never one
/// that would make a file executable.
/// </summary>
public static class FileTypeSniffer
{
    public const int HeaderBytesNeeded = 64;

    private static readonly Dictionary<string, string> MediaTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/avif"] = ".avif",
            ["image/heic"] = ".heic",
            ["image/bmp"] = ".bmp",
            ["video/mp4"] = ".mp4",
            ["video/webm"] = ".webm",
            ["video/x-matroska"] = ".mkv",
            ["video/quicktime"] = ".mov",
            ["video/x-msvideo"] = ".avi",
            ["audio/mpeg"] = ".mp3",
            ["audio/mp4"] = ".m4a",
            ["audio/ogg"] = ".ogg",
            ["audio/flac"] = ".flac",
            ["audio/wav"] = ".wav",
            ["application/pdf"] = ".pdf",
            ["application/zip"] = ".zip",
            ["application/x-7z-compressed"] = ".7z",
            ["application/vnd.rar"] = ".rar",
            ["application/x-rar-compressed"] = ".rar",
            ["application/gzip"] = ".gz",
            ["text/plain"] = ".txt",
        };

    /// <summary>
    /// The extension for this content, or null when unknown. Magic
    /// bytes win over the content type, which servers often get wrong.
    /// </summary>
    public static string? SuggestExtension(
        ReadOnlySpan<byte> header,
        string? mediaType)
    {
        string? sniffed = Sniff(header);

        if (sniffed is not null)
        {
            return sniffed;
        }

        return
            !string.IsNullOrWhiteSpace(mediaType) &&
            MediaTypes.TryGetValue(mediaType.Trim(), out string? fromType)
                ? fromType
                : null;
    }

    /// <summary>
    /// Adds an extension when the name has none; names that already
    /// have one are left exactly as the server sent them.
    /// </summary>
    public static string EnsureExtension(
        string fileName,
        ReadOnlySpan<byte> header,
        string? mediaType)
    {
        if (Path.HasExtension(fileName))
        {
            return fileName;
        }

        string? extension = SuggestExtension(header, mediaType);
        return extension is null ? fileName : fileName + extension;
    }

    /// <summary>
    /// True for an HTML page that was not explicitly offered as a
    /// file (no attachment name, URL not ending in .html).
    /// </summary>
    public static bool IsWebPage(
        string? mediaType,
        bool hasAttachmentName,
        Uri source)
    {
        if (hasAttachmentName || string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        bool html =
            mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

        if (!html)
        {
            return false;
        }

        string path = source.AbsolutePath;

        return !(
            path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mhtml", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4)
        {
            return null;
        }

        if (StartsWith(h, 0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (StartsWith(h, 0xFF, 0xD8, 0xFF)) return ".jpg";
        if (StartsWith(h, 0x47, 0x49, 0x46, 0x38)) return ".gif";
        if (StartsWith(h, 0x25, 0x50, 0x44, 0x46)) return ".pdf";
        if (StartsWith(h, 0x50, 0x4B, 0x03, 0x04)) return ".zip";
        if (StartsWith(h, 0x52, 0x61, 0x72, 0x21)) return ".rar";
        if (StartsWith(h, 0x37, 0x7A, 0xBC, 0xAF)) return ".7z";
        if (StartsWith(h, 0x1F, 0x8B)) return ".gz";
        if (StartsWith(h, 0x4F, 0x67, 0x67, 0x53)) return ".ogg";
        if (StartsWith(h, 0x66, 0x4C, 0x61, 0x43)) return ".flac";
        if (StartsWith(h, 0x49, 0x44, 0x33)) return ".mp3";

        if (StartsWith(h, 0x1A, 0x45, 0xDF, 0xA3))
        {
            // EBML: WebM declares its doc type near the start.
            return Contains(h, "webm"u8) ? ".webm" : ".mkv";
        }

        if (h.Length >= 12 && StartsWith(h, 0x52, 0x49, 0x46, 0x46))
        {
            ReadOnlySpan<byte> kind = h.Slice(8, 4);
            if (kind.SequenceEqual("WEBP"u8)) return ".webp";
            if (kind.SequenceEqual("AVI "u8)) return ".avi";
            if (kind.SequenceEqual("WAVE"u8)) return ".wav";
        }

        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            ReadOnlySpan<byte> brand = h.Slice(8, 4);
            if (brand.SequenceEqual("qt  "u8)) return ".mov";
            if (brand.SequenceEqual("M4A "u8)) return ".m4a";
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)) return ".heic";
            if (brand.SequenceEqual("avif"u8)) return ".avif";
            return ".mp4";
        }

        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, params byte[] prefix)
    {
        return data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
    }

    private static bool Contains(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        return data.IndexOf(value) >= 0;
    }
}
