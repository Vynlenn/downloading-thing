using System.Text.RegularExpressions;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Resolves MediaFire share pages (/file/key/name) by reading the
/// page's own download control. No partner API key is used on
/// purpose: the public get_links endpoint is not verifiably
/// anonymous, while the share page is the documented human route.
/// Direct links are temporary, so resolution happens at download
/// time and expired links surface as retryable failures.
/// </summary>
public static partial class MediaFireResolverPatterns
{
    // The share page's download control, as observed in shipping
    // downloaders (mediafire-dl, JDownloader). Markers may drift;
    // resolution tries each pattern in order and fails loudly
    // rather than guessing.
    [GeneratedRegex(
        """<a\b[^>]*?\bid\s*=\s*["']downloadButton["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 2000)]
    public static partial Regex DownloadButtonTag();

    [GeneratedRegex(
        """href\s*=\s*["'](?<url>[^"'<>]+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 2000)]
    public static partial Regex HrefValue();

    [GeneratedRegex(
        """<a\b[^>]*?aria-label\s*=\s*["']Download file["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 2000)]
    public static partial Regex AriaDownloadTag();
}

public sealed class MediaFireResolver : IHostFileResolver
{
    public bool CanResolve(Uri page)
    {
        return
            page is not null &&
            IsMediaFireHost(page.Host) &&
            TryParseFileKey(page) is not null;
    }

    public async Task<ResolvedHostFile> ResolveAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(httpClient);

        string? key = TryParseFileKey(page);

        if (key is null)
        {
            throw new ArgumentException(
                "Not a MediaFire share address.",
                nameof(page));
        }

        string html;

        try
        {
            using var request =
                new HttpRequestMessage(HttpMethod.Get, page);

            using HttpResponseMessage response =
                await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (
                response.StatusCode ==
                    System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode ==
                    System.Net.HttpStatusCode.Unauthorized)
            {
                throw await BuildChallengeOrFailureAsync(
                        response,
                        page)
                    .ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();

            // Share pages are markup-small; 2 MB is plenty for the
            // download control and stops pathological pages.
            html = await ReadPageAsync(
                    response.Content,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"MediaFire share {key} could not be reached. " +
                "Check the connection and try again.",
                exception);
        }

        if (MediaFirePage.IsPasswordLocked(html))
        {
            throw new ChallengeDetectedException(
                page.Host,
                ChallengeKind.LoginWall,
                page.AbsoluteUri,
                ChallengeDetector.FriendlyMessage(
                    page.Host,
                    ChallengeKind.LoginWall,
                    page.AbsoluteUri));
        }

        string? direct = MediaFirePage.FindDownloadUrl(html, page);

        if (direct is null)
        {
            throw new InvalidOperationException(
                $"MediaFire share {key} did not expose a download " +
                "link. It may be removed, private, or the page " +
                "changed — the item stays queued for retry.");
        }

        return new ResolvedHostFile(
            new Uri(direct, UriKind.Absolute),
            SuggestedName(page),
            TotalBytes: null);
    }

    public static string? TryParseFileKey(Uri page)
    {
        if (page is null || !IsMediaFireHost(page.Host))
        {
            return null;
        }

        string[] segments = page.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /file/{key}[/{name}...]
        if (
            segments.Length >= 2 &&
            segments[0] == "file")
        {
            string key = segments[1].Trim();

            if (
                key.Length is >= 4 and <= 128 &&
                key.All(character =>
                    char.IsLetterOrDigit(character) ||
                    character is '-' or '_'))
            {
                return key;
            }
        }

        return null;
    }

    private static async Task<string> ReadPageAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumChars = 2_000_000;

        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

        using var reader = new StreamReader(
            stream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        char[] buffer = new char[16_384];
        var builder = new System.Text.StringBuilder();
        int total = 0;

        while (total < maximumChars)
        {
            int read = await reader.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            int take = Math.Min(read, maximumChars - total);
            builder.Append(buffer, 0, take);
            total += take;
        }

        if (total >= maximumChars)
        {
            throw new InvalidOperationException(
                "The MediaFire share page is unexpectedly large. " +
                "The item stays queued for retry.");
        }

        return builder.ToString();
    }

    internal static async Task<Exception> BuildChallengeOrFailureAsync(
        HttpResponseMessage response,
        Uri page)
    {
        string snippet = string.Empty;

        try
        {
            snippet = await ErrorSnippetReader.ReadSnippetAsync(
                    response.Content)
                .ConfigureAwait(false);
        }
        catch
        {
            // Fall through to the generic error below.
        }

        ChallengeKind? challenge = ChallengeDetector.Detect(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            snippet,
            page.Host);

        if (challenge.HasValue)
        {
            return new ChallengeDetectedException(
                page.Host,
                challenge.Value,
                page.AbsoluteUri,
                ChallengeDetector.FriendlyMessage(
                    page.Host,
                    challenge.Value,
                    page.AbsoluteUri),
                isSolvableInBrowser:
                    challenge.Value != ChallengeKind.QuotaWall);
        }

        return new HttpRequestException(
            $"MediaFire refused the share page ({(int)response.StatusCode}).");
    }

    private static bool IsMediaFireHost(string host)
    {
        return
            host.Equals("mediafire.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SuggestedName(Uri page)
    {
        string[] segments = page.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 3)
        {
            string candidate = Uri.UnescapeDataString(
                segments[^1].Trim());

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Pure share-page parsing, tested without network access.
/// </summary>
public static class MediaFirePage
{
    public static bool IsPasswordLocked(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return
            html.Contains("password", StringComparison.OrdinalIgnoreCase) &&
            (html.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("type='password'", StringComparison.OrdinalIgnoreCase));
    }

    /// <returns>The absolute direct URL, or null.</returns>
    public static string? FindDownloadUrl(string html, Uri page)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (System.Text.RegularExpressions.Match tag in
            MediaFireResolverPatterns.DownloadButtonTag().Matches(html))
        {
            string? url = HrefIn(tag.Value);

            if (AbsoluteHttp(url, page) is string absolute)
            {
                return absolute;
            }
        }

        foreach (System.Text.RegularExpressions.Match tag in
            MediaFireResolverPatterns.AriaDownloadTag().Matches(html))
        {
            string? url = HrefIn(tag.Value);

            if (AbsoluteHttp(url, page) is string absolute)
            {
                return absolute;
            }
        }

        return null;
    }

    private static string? HrefIn(string tag)
    {
        System.Text.RegularExpressions.Match match =
            MediaFireResolverPatterns.HrefValue().Match(tag);

        return match.Success
            ? match.Groups["url"].Value.Trim()
            : null;
    }

    private static string? AbsoluteHttp(string? raw, Uri page)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (
            !Uri.TryCreate(page, raw, out Uri? resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp &&
             resolved.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(resolved.Host))
        {
            return null;
        }

        // A download control pointing back at MediaFire share
        // pages is navigation, not the file. Download traffic
        // lives on sibling subdomains (download*.mediafire.com).
        if (
            resolved.Host.Equals(
                "mediafire.com",
                StringComparison.OrdinalIgnoreCase) ||
            resolved.Host.Equals(
                "www.mediafire.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return resolved.AbsoluteUri;
    }
}
