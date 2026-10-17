using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Fetches one user-pasted page (never a recursive spider) and
/// extracts its file-host links. The injected HttpClient carries
/// the app's proxy and timeout policy.
/// </summary>
public sealed class PageLinkCrawler
{
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

        using var timeoutSource =
            new CancellationTokenSource(FetchTimeout);

        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

        CancellationToken linkedToken = linkedSource.Token;

        string html;
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

            response.EnsureSuccessStatusCode();

            string? mediaType =
                response.Content.Headers.ContentType?.MediaType;

            if (
                mediaType is not null &&
                !mediaType.Contains(
                    "html",
                    StringComparison.OrdinalIgnoreCase)
            )
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

            html = await ReadCappedAsync(
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

        return PageLinkExtractor.Extract(html, page.AbsoluteUri);
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
