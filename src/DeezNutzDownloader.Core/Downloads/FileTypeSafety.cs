namespace DeezNutzDownloader.Core.Downloads;

public enum FileSafetyLevel
{
    None,

    /// <summary>Can run code or hide programs; fine if trusted.</summary>
    Caution,

    /// <summary>A program dressed up as something harmless.</summary>
    Danger
}

public sealed record FileSafetyAssessment(
    FileSafetyLevel Level,
    string? Message)
{
    public static FileSafetyAssessment Safe { get; } =
        new(FileSafetyLevel.None, null);
}

/// <summary>
/// Flags file names that can run code when opened, plus programs
/// disguised with a harmless-looking extension ("invoice.pdf.exe").
/// Pure name check: it never opens the file, and the Mark of the
/// Web still lets Windows do its own checks on top.
/// </summary>
public static class FileTypeSafety
{
    // Opening these runs code (directly, via a script host, an
    // installer, or a shortcut target).
    private static readonly HashSet<string> RunsCode =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "exe", "scr", "com", "pif", "bat", "cmd", "msi", "msp",
            "msix", "msixbundle", "appx", "appxbundle", "application",
            "appref-ms", "ps1", "psm1", "psd1", "vbs", "vbe", "js",
            "jse", "wsf", "wsh", "hta", "cpl", "jar", "reg", "scf",
            "lnk", "url", "msc", "gadget", "inf", "chm",
            "settingcontent-ms", "dll", "xll", "library-ms",
            "searchconnector-ms"
        };

    private static readonly HashSet<string> MacroDocuments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "docm", "dotm", "xlsm", "xltm", "xlam", "pptm", "potm",
            "ppam", "ppsm", "sldm"
        };

    // Disk images mount as drives; files inside have historically
    // escaped Windows' downloaded-file warnings.
    private static readonly HashSet<string> DiskImages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "iso", "img", "vhd", "vhdx"
        };

    // Extensions people expect to be harmless: seeing one right
    // before a program extension is the classic disguise.
    private static readonly HashSet<string> DecoyExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt",
            "rtf", "odt", "csv", "jpg", "jpeg", "png", "gif", "bmp",
            "webp", "heic", "svg", "mp3", "wav", "flac", "m4a", "ogg",
            "mp4", "mkv", "avi", "mov", "wmv", "webm", "zip", "rar",
            "7z", "html", "htm", "srt", "epub"
        };

    public static FileSafetyAssessment Assess(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileSafetyAssessment.Safe;
        }

        string name = Path.GetFileName(fileName.Trim()).TrimEnd('.', ' ');
        string extension = ExtensionOf(name);

        if (extension.Length == 0)
        {
            return FileSafetyAssessment.Safe;
        }

        string stem = name[..^(extension.Length + 1)].TrimEnd(' ', '.');
        string decoy = ExtensionOf(stem);

        bool runsCode = RunsCode.Contains(extension);
        bool macro = MacroDocuments.Contains(extension);

        if (
            (runsCode || macro) &&
            decoy.Length > 0 &&
            DecoyExtensions.Contains(decoy))
        {
            return new FileSafetyAssessment(
                FileSafetyLevel.Danger,
                $"Disguised file: looks like a .{decoy.ToLowerInvariant()} " +
                $"but is a .{extension.ToLowerInvariant()} " +
                (runsCode ? "program" : "macro document") +
                ". Don't open it unless you know exactly why it's named this way.");
        }

        if (runsCode)
        {
            return new FileSafetyAssessment(
                FileSafetyLevel.Caution,
                $"Program file (.{extension.ToLowerInvariant()}): " +
                "opening it runs code. Only open it if you trust where it came from.");
        }

        if (macro)
        {
            return new FileSafetyAssessment(
                FileSafetyLevel.Caution,
                $"Office file with macros (.{extension.ToLowerInvariant()}): " +
                "keep macros disabled unless you trust where it came from.");
        }

        if (DiskImages.Contains(extension))
        {
            return new FileSafetyAssessment(
                FileSafetyLevel.Caution,
                $"Disk image (.{extension.ToLowerInvariant()}): programs inside " +
                "may skip Windows download warnings. Check before running anything in it.");
        }

        return FileSafetyAssessment.Safe;
    }

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');

        // A leading dot (".bashrc") is a name, not an extension.
        return dot <= 0 || dot == name.Length - 1
            ? string.Empty
            : name[(dot + 1)..].Trim();
    }
}
