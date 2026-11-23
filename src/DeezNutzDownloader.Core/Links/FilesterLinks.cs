using System.Text.RegularExpressions;

namespace DeezNutzDownloader.Core.Links;

/// <summary>
/// Filester share links, from its own upload script: files are
/// shared as /d/{slug} (on filester.me and sister domains), direct
/// "hotlinks" are /h/{slug}, and /t/{id} is only a thumbnail preview
/// whose id cannot be turned back into the file.
/// </summary>
public static partial class FilesterLinks
{
    public static bool IsFilesterHost(string? host)
    {
        return !string.IsNullOrWhiteSpace(host) &&
            FilesterHost().IsMatch(host.Trim().TrimEnd('.'));
    }

    public static bool IsFilePage(Uri? uri) => Segment(uri) == "d";

    public static bool IsHotlink(Uri? uri) => Segment(uri) == "h";

    public static bool IsThumbnail(Uri? uri) => Segment(uri) == "t";

    private static string? Segment(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || !IsFilesterHost(uri.Host))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 ? segments[0].ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^(www\.)?filester\.[a-z]{2,8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilesterHost();
}
