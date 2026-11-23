using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Expands a Bunkr album (/a/{id}) into one queue row per file
/// (/f/{slug}) with its name and size, read from the album page's
/// server-rendered grid. The files themselves download through the
/// embedded browser (see IBrowserDownloader).
/// </summary>
public sealed partial class BunkrAlbumResolver : IHostFolderResolver
{
    private const int MaximumPages = 50;

    public bool CanResolveFolder(Uri page) => BunkrLinks.IsAlbum(page);

    public async Task<ResolvedHostFolder> ResolveFolderAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(httpClient);

        string firstHtml = await FetchAsync(page, httpClient, cancellationToken)
            .ConfigureAwait(false);

        BunkrAlbum album = BunkrAlbumParser.Parse(firstHtml, page);
        var files = new List<BunkrAlbumFile>(album.Files);

        // Large albums may paginate (?page=2...).
        foreach (int number in album.OtherPages.Take(MaximumPages))
        {
            var pageUri = new UriBuilder(page) { Query = $"page={number}" }.Uri;
            string html = await FetchAsync(pageUri, httpClient, cancellationToken)
                .ConfigureAwait(false);

            files.AddRange(BunkrAlbumParser.Parse(html, pageUri).Files);
        }

        List<BunkrAlbumFile> unique = files
            .GroupBy(file => file.Page.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (unique.Count == 0)
        {
            throw new InvalidOperationException(
                $"Bunkr album {page.AbsolutePath} lists no files. " +
                "It may be empty, private, or removed.");
        }

        return new ResolvedHostFolder(
            album.Title ?? $"bunkr-{page.Segments[^1].Trim('/')}",
            unique
                .Select(file => new ResolvedHostFile(file.Page, file.Name, file.SizeBytes))
                .ToArray());
    }

    private static async Task<string> FetchAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(page, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new InvalidOperationException(
                $"Bunkr album {page.AbsolutePath} was not found. It may be removed.");
        }

        if (HostThrottlePolicy.IsThrottleStatusCode(response.StatusCode))
        {
            throw new HostThrottledException(
                page.Host,
                $"{page.Host} is limiting requests ({(int)response.StatusCode}); the album stays queued.",
                HostThrottlePolicy.TryParseRetryAfter(response.Headers, DateTimeOffset.UtcNow));
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record BunkrAlbumFile(Uri Page, string? Name, long? SizeBytes);

public sealed record BunkrAlbum(
    string? Title,
    IReadOnlyList<BunkrAlbumFile> Files,
    IReadOnlyList<int> OtherPages);

/// <summary>Pure parser for the album page's file grid.</summary>
public static partial class BunkrAlbumParser
{
    public static BunkrAlbum Parse(string html, Uri albumPage)
    {
        ArgumentNullException.ThrowIfNull(albumPage);
        html ??= string.Empty;

        var files = new List<BunkrAlbumFile>();
        MatchCollection items = ItemStart().Matches(html);

        for (int index = 0; index < items.Count; index++)
        {
            int start = items[index].Index;
            int end = index + 1 < items.Count ? items[index + 1].Index : html.Length;
            string item = html[start..end];

            Match link = FileLink().Match(item);

            if (!link.Success)
            {
                continue;
            }

            var page = new Uri(albumPage, link.Groups["path"].Value);

            if (!BunkrLinks.IsFilePage(page))
            {
                continue;
            }

            string? name =
                Text(NameField().Match(item)) ??
                Attribute(TitleAttribute().Match(item));

            files.Add(new BunkrAlbumFile(
                page,
                SafeName(name),
                ParseSize(Text(SizeField().Match(item)))));
        }

        // A grid without item wrappers: fall back to plain file links.
        if (files.Count == 0)
        {
            foreach (Match link in FileLink().Matches(html))
            {
                var page = new Uri(albumPage, link.Groups["path"].Value);

                if (BunkrLinks.IsFilePage(page))
                {
                    files.Add(new BunkrAlbumFile(page, null, null));
                }
            }
        }

        string? title =
            Attribute(OgTitle().Match(html)) ??
            Text(Heading().Match(html));

        int[] pages = PageLink().Matches(html)
            .Select(match => int.TryParse(match.Groups["n"].Value, out int n) ? n : 0)
            .Where(n => n > 1)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        return new BunkrAlbum(CleanTitle(title), files, pages);
    }

    /// <summary>"42.76 MB" to bytes (binary units, as Bunkr shows them).</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = SizeText().Match(text);

        if (
            !match.Success ||
            !double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
        {
            return null;
        }

        double multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 0
        };

        return multiplier == 0 ? null : (long)Math.Round(value * multiplier);
    }

    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Album names are labels, not paths: never let them create
        // subfolders. The download pipeline sanitizes the rest.
        return name.Replace('/', '_').Replace('\\', '_').Trim();
    }

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // og:title often carries the site name: "Album | Bunkr".
        int bar = title.LastIndexOf('|');
        string cleaned = bar > 0 ? title[..bar] : title;
        return cleaned.Trim().Length == 0 ? null : cleaned.Trim();
    }

    private static string? Text(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        string value = WebUtility.HtmlDecode(match.Groups["text"].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? Attribute(Match match) => Text(match);

    [GeneratedRegex(@"class=""[^""]*\btheItem\b", RegexOptions.CultureInvariant)]
    private static partial Regex ItemStart();

    [GeneratedRegex(@"href=""(?<path>/[fvi]/[A-Za-z0-9_-]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FileLink();

    [GeneratedRegex(@"class=""[^""]*\btheName\b[^""]*""[^>]*>(?<text>[^<]*)<", RegexOptions.CultureInvariant)]
    private static partial Regex NameField();

    [GeneratedRegex(@"\btitle=""(?<text>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex TitleAttribute();

    [GeneratedRegex(@"class=""[^""]*\btheSize\b[^""]*""[^>]*>(?<text>[^<]*)<", RegexOptions.CultureInvariant)]
    private static partial Regex SizeField();

    [GeneratedRegex(@"<meta\s+property=""og:title""\s+content=""(?<text>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OgTitle();

    [GeneratedRegex(@"<h1[^>]*>(?<text>[^<]*)<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"href=""\?page=(?<n>\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex PageLink();

    [GeneratedRegex(@"(?<value>\d+(?:\.\d+)?)\s*(?<unit>[KMGT]?B)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeText();
}
