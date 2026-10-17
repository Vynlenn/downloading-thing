using System.Text.RegularExpressions;

namespace DeezNutzDownloader.Core.Links;

/// <summary>
/// A file link found while crawling a pasted page.
/// </summary>
public sealed record PageLinkCandidate(
    string Url,
    string Reason);

public sealed record PageExtractionResult(
    IReadOnlyList<PageLinkCandidate> Files,
    int AnchorsExamined,
    int SkippedCount,
    string? NextPageUrl = null);

/// <summary>
/// Finds downloadable file links on a pasted page and filters out
/// page chrome: avatars, logos, icons, stylesheets, scripts,
/// navigation, share buttons, and other non-content links.
/// Only anchors are considered; bare images never start downloads.
/// </summary>
public static partial class PageLinkExtractor
{
    private static readonly HashSet<string> KnownFileHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pixeldrain.com",
            "pixeldrain.nl",
            "gofile.io",
            "mega.nz",
            "mega.io",
            "mediafire.com",
            "www.mediafire.com",
            "1fichier.com",
            "rapidgator.net",
            "nitroflare.com",
            "katfile.com",
            "uploaded.net",
            "turbobit.net",
            "ddownload.com",
            "filefactory.com",
            "usersdrive.com",
            "filerio.com",
            "bowfile.com",
        };

    private static readonly HashSet<string> DownloadableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv",
            ".webm", ".m4v", ".ts",
            ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".opus",
            ".pdf", ".epub", ".mobi", ".cbz", ".cbr",
            ".iso", ".img", ".exe", ".msi", ".dmg", ".apk",
            ".torrent", ".nzb", ".srt", ".sub",
            ".psd", ".ai", ".blend", ".fbx", ".obj",
        };

    // Page chrome (avatars, logos, icons, stylesheets, scripts,
    // navigation) never survives: only known file-host links and
    // direct downloadable extensions are kept, everything else is
    // dropped by Classify below.
    private static readonly string[] SharePrefixes =
    {
        "facebook.com/sharer",
        "twitter.com/intent",
        "x.com/intent",
        "reddit.com/submit",
        "t.me/share",
        "api.whatsapp.com/send",
        "pinterest.com/pin/create",
    };

    [GeneratedRegex(
        """<a\b(?<tag>[^>]*?)\bhref\s*=\s*["'](?<url>[^"'<>]+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex AnchorHrefRegex();

    [GeneratedRegex(
        """<base\b[^>]*?\bhref\s*=\s*["'](?<url>[^"'<>]+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex BaseHrefRegex();

    public static PageExtractionResult Extract(
        string html,
        string pageUrl)
    {
        if (
            !Uri.TryCreate(pageUrl?.Trim(), UriKind.Absolute, out Uri? page) ||
            (page.Scheme != Uri.UriSchemeHttp &&
             page.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new ArgumentException(
                "A valid http(s) page address is required.",
                nameof(pageUrl));
        }

        html ??= string.Empty;

        Uri baseUri = ResolveBaseUri(html, page);

        var files = new List<PageLinkCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int examined = 0;
        string? nextPage = null;

        foreach (Match match in AnchorHrefRegex().Matches(html))
        {
            string raw = match.Groups["url"].Value.Trim();

            if (raw.Length == 0)
            {
                continue;
            }

            examined++;

            string? normalized = NormalizeLink(raw, baseUri, page);

            if (normalized is null)
            {
                continue;
            }

            // Pagination is discovered alongside files so one pass
            // serves single pages and gallery walks alike. First
            // rel=next (or obvious page-N link) wins.
            if (
                nextPage is null &&
                IsNextPageLink(
                    normalized,
                    match.Groups["tag"].Value,
                    page))
            {
                nextPage = normalized;
            }

            string? reason = Classify(normalized);

            if (reason is null)
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                files.Add(new PageLinkCandidate(normalized, reason));
            }
        }

        return new PageExtractionResult(
            files.AsReadOnly(),
            examined,
            Math.Max(0, examined - files.Count),
            nextPage);
    }

    private static Uri ResolveBaseUri(string html, Uri page)
    {
        Match baseMatch = BaseHrefRegex().Match(html);

        if (
            baseMatch.Success &&
            Uri.TryCreate(
                page,
                baseMatch.Groups["url"].Value.Trim(),
                out Uri? baseUri) &&
            (baseUri.Scheme == Uri.UriSchemeHttp ||
             baseUri.Scheme == Uri.UriSchemeHttps)
        )
        {
            return baseUri;
        }

        return page;
    }

    private static string? NormalizeLink(
        string raw,
        Uri baseUri,
        Uri page)
    {
        string lower = raw.ToLowerInvariant();

        if (
            lower.StartsWith("javascript:", StringComparison.Ordinal) ||
            lower.StartsWith("data:", StringComparison.Ordinal) ||
            lower.StartsWith("mailto:", StringComparison.Ordinal) ||
            lower.StartsWith("tel:", StringComparison.Ordinal) ||
            lower.StartsWith("magnet:", StringComparison.Ordinal) ||
            raw.StartsWith('#')
        )
        {
            return null;
        }

        if (
            !Uri.TryCreate(baseUri, raw, out Uri? resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp &&
             resolved.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(resolved.Host)
        )
        {
            return null;
        }

        var builder = new UriBuilder(resolved) { Fragment = string.Empty };
        Uri normalized = builder.Uri;

        // Same-page anchors and self links are navigation, not files.
        if (
            string.Equals(
                normalized.AbsoluteUri,
                new UriBuilder(page) { Fragment = string.Empty }.Uri.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        foreach (string prefix in SharePrefixes)
        {
            if (
                (normalized.Host + normalized.AbsolutePath)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            )
            {
                return null;
            }
        }

        return normalized.AbsoluteUri;
    }

    private static string? Classify(string url)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        )
        {
            return null;
        }

        string host = uri.Host;
        string path = uri.AbsolutePath;

        if (IsKnownFileHost(host))
        {
            return $"{TrimWww(host)} link";
        }

        string extension = Path.GetExtension(path);

        if (
            !string.IsNullOrEmpty(extension) &&
            DownloadableExtensions.Contains(extension)
        )
        {
            return $"Direct {extension} link";
        }

        return null;
    }

    /// <summary>
    /// True for a same-host pagination link: an explicit
    /// rel="next" anchor, or an obvious page-N address
    /// (?page=2, /page/3, ?offset=…). Never cross-host, never the
    /// page itself — gallery walks stay on rails.
    /// </summary>
    public static bool IsNextPageLink(
        string normalizedUrl,
        string anchorTag,
        Uri page)
    {
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? link))
        {
            return false;
        }

        if (
            !link.Host.Equals(page.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (
            string.Equals(
                link.AbsoluteUri,
                page.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A known file link is content, never pagination.
        if (Classify(normalizedUrl) is not null)
        {
            return false;
        }

        if (
            anchorTag.Contains("rel", StringComparison.OrdinalIgnoreCase) &&
            anchorTag.Contains("next", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string path = link.AbsolutePath;
        string query = link.Query;

        if (
            System.Text.RegularExpressions.Regex.IsMatch(
                path,
                @"/page[=/]\d+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(
                query,
                @"[?&](page|p|pg|offset)=\d+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsKnownFileHost(string host)
    {
        string normalized = host.Trim().TrimEnd('.');

        foreach (string known in KnownFileHosts)
        {
            if (
                string.Equals(normalized, known, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(
                    "." + known,
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimWww(string host)
    {
        return host.StartsWith(
                "www.",
                StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;
    }
}
