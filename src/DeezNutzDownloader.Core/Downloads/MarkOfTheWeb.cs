using System.Text;

namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// Tags finished downloads with the Windows "Mark of the Web"
/// (a Zone.Identifier alternate data stream, ZoneId=3 Internet),
/// exactly as browsers do. That is what makes SmartScreen check an
/// .exe before it runs, Office open documents in Protected View,
/// and Explorer warn on scripts — protections a downloader that
/// skips the mark silently turns off.
/// Best effort: non-Windows, FAT/exFAT drives, or a locked file
/// simply leave the file unmarked and never fail the download.
/// </summary>
public static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    public static bool TryApply(
        string filePath,
        Uri? hostUrl,
        Uri? referrerUrl = null)
    {
        if (
            !OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            File.WriteAllText(
                filePath + StreamSuffix,
                BuildContent(hostUrl, referrerUrl),
                Encoding.ASCII);

            return true;
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks every file under a folder (MEGA folders, extracted
    /// archives). Returns how many files were marked.
    /// </summary>
    public static int TryApplyToDirectory(
        string directoryPath,
        Uri? hostUrl,
        Uri? referrerUrl = null)
    {
        if (
            !OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(directoryPath) ||
            !Directory.Exists(directoryPath))
        {
            return 0;
        }

        int marked = 0;

        try
        {
            foreach (
                string file in Directory.EnumerateFiles(
                    directoryPath,
                    "*",
                    SearchOption.AllDirectories))
            {
                if (TryApply(file, hostUrl, referrerUrl))
                {
                    marked++;
                }
            }
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException)
        {
            // Partial marking still beats none.
        }

        return marked;
    }

    /// <summary>
    /// Copies the mark from an archive to a file extracted from it,
    /// as Explorer does, so unpacking cannot launder the warning.
    /// </summary>
    public static bool TryPropagate(
        string sourceFilePath,
        string targetFilePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            string content =
                File.ReadAllText(sourceFilePath + StreamSuffix);

            if (!content.Contains("[ZoneTransfer]", StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(
                targetFilePath + StreamSuffix,
                content,
                Encoding.ASCII);

            return true;
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
        {
            return false;
        }
    }

    public static bool IsMarked(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return File.ReadAllText(filePath + StreamSuffix)
                .Contains("ZoneId=3", StringComparison.Ordinal);
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
        {
            return false;
        }
    }

    internal static string BuildContent(
        Uri? hostUrl,
        Uri? referrerUrl)
    {
        var builder = new StringBuilder();
        builder.Append("[ZoneTransfer]\r\n");
        builder.Append("ZoneId=3\r\n");

        string? referrer = StripForPrivacy(referrerUrl);
        string? host = StripForPrivacy(hostUrl);

        if (referrer is not null)
        {
            builder.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
        }

        if (host is not null)
        {
            builder.Append("HostUrl=").Append(host).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Scheme, host, port and path only: user info, query strings
    /// and fragments often carry tokens or signed-URL secrets that
    /// must not be copied onto disk next to the file.
    /// </summary>
    internal static string? StripForPrivacy(Uri? uri)
    {
        if (
            uri is null ||
            !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
    }
}
