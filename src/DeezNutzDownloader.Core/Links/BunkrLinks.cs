using System.Text.RegularExpressions;

namespace DeezNutzDownloader.Core.Links;

/// <summary>
/// Bunkr runs on many interchangeable domains (bunkr.cr, bunkr.si,
/// bunkrr.su, ...). Albums live at /a/{id}, files at /f/{slug}
/// (older /v/ and /i/ links are file pages too).
/// </summary>
public static partial class BunkrLinks
{
    public static bool IsBunkrHost(string? host)
    {
        return !string.IsNullOrWhiteSpace(host) &&
            BunkrHost().IsMatch(host.Trim().TrimEnd('.'));
    }

    public static bool IsAlbum(Uri? uri)
    {
        return FirstSegment(uri) == "a";
    }

    public static bool IsFilePage(Uri? uri)
    {
        return FirstSegment(uri) is "f" or "v" or "i";
    }

    private static string? FirstSegment(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || !IsBunkrHost(uri.Host))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 ? segments[0].ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^(www\.)?bunkrr?\.[a-z]{2,8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BunkrHost();
}
