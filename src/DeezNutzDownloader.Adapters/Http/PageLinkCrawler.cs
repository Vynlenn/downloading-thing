using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// One gallery walk: deduplicated file links across every page
/// visited, in walk order.
/// </summary>
public sealed record PageCrawlResult(
    IReadOnlyList<PageLinkCandidate> Files,
    int PagesVisited,
    int AnchorsExamined,
    int SkippedCount);

/// <summary>
/// Fetches a user-pasted page (or walks its gallery pagination)
/// and extracts file-host links. The injected HttpClient carries
/// the app's proxy, cookies and timeout policy. Pagination never
/// leaves the start host and never revisits a page.
/// </summary>
public sealed class PageLinkCrawler
{
    public const int DefaultMaxPages = 10;

    private const int MaximumHtmlBytes = 5_000_000;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public PageLinkCrawler(HttpClient httpClient)
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<PageExtractionResult> ExtractFromPageAsync(
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        Uri page = ValidatePageUrl(pageUrl);

        string html = await FetchHtmlAsync(page, cancellationToken)
            .ConfigureAwait(false);

        return PageLinkExtractor.Extract(html, page.AbsoluteUri);
    }

    /// <summary>
    /// Walks gallery pagination from the pasted page, merging
    /// deduplicated file links. Stops at maxPages, a page without
    /// a next link, a repeated page, or a challenge (which throws
    /// typed, so the UI can solve and the user retries).
    /// </summary>
    public async Task<PageCrawlResult> ExtractFromPagesAsync(
        string pageUrl,
        int maxPages = DefaultMaxPages,
        CancellationToken cancellationToken = default)
    {
        Uri start = ValidatePageUrl(pageUrl);

        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPages),
                "At least one page must be visited.");
        }

        var files = new List<PageLinkCandidate>();
        var seenUrls = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        int anchorsExamined = 0;
        int skippedCount = 0;
        string? next = start.AbsoluteUri;

        while (
            next is not null &&
            visited.Count < maxPages)
        {
            if (!visited.Add(next))
            {
                break;
            }

            Uri page = new(next, UriKind.Absolute);

            string html = await FetchHtmlAsync(page, cancellationToken)
                .ConfigureAwait(false);

            PageExtractionResult result =
                PageLinkExtractor.Extract(html, page.AbsoluteUri);

            anchorsExamined += result.AnchorsExamined;
            skippedCount += result.SkippedCount;

            foreach (PageLinkCandidate file in result.Files)
            {
                if (seenUrls.Add(file.Url))
                {
                    files.Add(file);
                }
            }

            next = NormalizeNext(result.NextPageUrl, start);
        }

        return new PageCrawlResult(
            files.AsReadOnly(),
            visited.Count,
            anchorsExamined,
            skippedCount);
    }

    private static Uri ValidatePageUrl(string pageUrl)
    {
        if (
            !Uri.TryCreate(pageUrl?.Trim(), UriKind.Absolute, out Uri? page) ||
            (page.Scheme != Uri.UriSchemeHttp &&
             page.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new ArgumentException(
                "Enter a single http(s) page address to crawl.",
                nameof(pageUrl));
        }

        return page;
    }

    private static string? NormalizeNext(
        string? nextPageUrl,
        Uri start)
    {
        if (string.IsNullOrWhiteSpace(nextPageUrl))
        {
            return null;
        }

        if (
            !Uri.TryCreate(nextPageUrl, UriKind.Absolute, out Uri? next) ||
            (next.Scheme != Uri.UriSchemeHttp &&
             next.Scheme != Uri.UriSchemeHttps) ||
            !next.Host.Equals(
                start.Host,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return next.AbsoluteUri;
    }

    private async Task<string> FetchHtmlAsync(
        Uri page,
        CancellationToken cancellationToken)
    {
        using var timeoutSource =
            new CancellationTokenSource(FetchTimeout);

        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

        CancellationToken linkedToken = linkedSource.Token;

        try
        {
            using var request =
                new HttpRequestMessage(HttpMethod.Get, page);

            using HttpResponseMessage response =
                await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedToken)
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

            string? mediaType =
                response.Content.Headers.ContentType?.MediaType;

            if (
                mediaType is not null &&
                !mediaType.Contains(
                    "html",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "That address is a file, not a page. " +
                    "Add it with Analyze links instead.");
            }

            if (
                response.Content.Headers.ContentLength >
                MaximumHtmlBytes
            )
            {
                throw new InvalidOperationException(
                    "That page is too large to scan (over 5 MB of HTML).");
            }

            return await ReadCappedAsync(
                    response.Content,
                    linkedToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The page took too long to respond (30s limit).");
        }
    }

    /// <summary>
    /// Turns a 403/401 crawl response into a typed challenge when
    /// the page carries challenge markers, otherwise the usual
    /// HTTP error. Never throws itself.
    /// </summary>
    public static async Task<Exception> BuildChallengeOrFailureAsync(
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
            $"The page request failed ({(int)response.StatusCode}).");
    }

    private static async Task<string> ReadCappedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

        using var reader =
            new StreamReader(
                stream,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);

        char[] buffer = new char[16_384];
        var builder = new System.Text.StringBuilder();
        int totalChars = 0;

        // Roughly 5 MB of single-byte HTML.
        const int maximumChars = 5_000_000;

        while (true)
        {
            int read = await reader.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            totalChars += read;

            if (totalChars > maximumChars)
            {
                throw new InvalidOperationException(
                    "That page is too large to scan (over 5 MB of HTML).");
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }
}
