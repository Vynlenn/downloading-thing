using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

public sealed record LinkCheckResult(
    LinkCheckStatus Status,
    string Message,
    string? FileName = null,
    long? SizeBytes = null);

/// <summary>
/// Checks queued links before downloading: online (with real name
/// and size), removed, behind a challenge, or limited by the host.
/// Uses the same resolvers as downloads (cheap info calls) and a
/// HEAD request for plain links. Never downloads file contents.
/// </summary>
public sealed class LinkChecker
{
    private const int MaximumParallelChecks = 4;

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IHostFileResolver> _resolvers;

    public LinkChecker(
        HttpClient httpClient,
        IEnumerable<IHostFileResolver>? resolvers = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _resolvers = resolvers?.ToArray() ?? Array.Empty<IHostFileResolver>();
    }

    /// <summary>
    /// Checks every link with bounded parallelism; single-lane free
    /// hosts are checked one at a time so a check never trips their
    /// per-IP limits. Results arrive through <paramref name="onResult"/>.
    /// </summary>
    public async Task CheckAllAsync(
        IReadOnlyList<(Guid Id, Uri Url)> links,
        Action<Guid, LinkCheckResult> onResult,
        CancellationToken cancellationToken = default)
    {
        using var parallel = new SemaphoreSlim(MaximumParallelChecks);
        var hostLanes = new ConcurrentDictionary<string, SemaphoreSlim>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            await Task.WhenAll(
                    links.Select(async link =>
                    {
                        SemaphoreSlim? lane =
                            HostThrottlePolicy.IsSingleLaneHost(link.Url.Host)
                                ? hostLanes.GetOrAdd(link.Url.Host, _ => new SemaphoreSlim(1, 1))
                                : null;

                        if (lane is not null)
                        {
                            await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        try
                        {
                            await parallel.WaitAsync(cancellationToken).ConfigureAwait(false);

                            try
                            {
                                LinkCheckResult result =
                                    await CheckAsync(link.Url, cancellationToken)
                                        .ConfigureAwait(false);

                                onResult(link.Id, result);
                            }
                            finally
                            {
                                parallel.Release();
                            }
                        }
                        finally
                        {
                            lane?.Release();
                        }
                    }))
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (SemaphoreSlim lane in hostLanes.Values)
            {
                lane.Dispose();
            }
        }
    }

    public async Task<LinkCheckResult> CheckAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (MegaLinks.IsLink(url))
        {
            return new LinkCheckResult(
                LinkCheckStatus.Unknown,
                "MEGA links are checked when they download.");
        }

        foreach (IHostFileResolver resolver in _resolvers)
        {
            if (!resolver.CanResolve(url))
            {
                continue;
            }

            try
            {
                ResolvedHostFile resolved =
                    await resolver.ResolveAsync(url, _httpClient, cancellationToken)
                        .ConfigureAwait(false);

                return new LinkCheckResult(
                    LinkCheckStatus.Online,
                    "Online",
                    HttpDownloadTransferService.SanitizeFileName(resolved.SuggestedFileName),
                    resolved.TotalBytes);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                return Classify(exception);
            }
        }

        if (HostThrottlePolicy.IsSingleLaneHost(url.Host))
        {
            // A share page we have no resolver for (folders, lists):
            // probing it would only fetch an HTML page.
            return new LinkCheckResult(
                LinkCheckStatus.Unknown,
                "Checked when it downloads (folder or share page).");
        }

        return await CheckPlainLinkAsync(url, cancellationToken).ConfigureAwait(false);
    }

    internal static LinkCheckResult Classify(Exception exception)
    {
        string message = exception.Message;

        return exception switch
        {
            HostThrottledException => new LinkCheckResult(
                LinkCheckStatus.Limited,
                message),

            ChallengeDetectedException => new LinkCheckResult(
                LinkCheckStatus.NeedsAttention,
                message),

            FolderExpansionNeededException => new LinkCheckResult(
                LinkCheckStatus.Online,
                "Folder: expands into its files when the batch starts."),

            _ when LooksRemoved(message) => new LinkCheckResult(
                LinkCheckStatus.Offline,
                message),

            _ => new LinkCheckResult(
                LinkCheckStatus.Unknown,
                message)
        };
    }

    private async Task<LinkCheckResult> CheckPlainLinkAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage head =
                await SendAsync(HttpMethod.Head, url, rangeProbe: false, cancellationToken)
                    .ConfigureAwait(false);

            // Plenty of servers reject HEAD; a one-byte GET answers
            // the same question without downloading the file.
            if (
                head.StatusCode is HttpStatusCode.MethodNotAllowed or
                    HttpStatusCode.NotImplemented or
                    HttpStatusCode.Forbidden)
            {
                using HttpResponseMessage probe =
                    await SendAsync(HttpMethod.Get, url, rangeProbe: true, cancellationToken)
                        .ConfigureAwait(false);

                return FromResponse(probe, url);
            }

            return FromResponse(head, url);
        }
        catch (HttpRequestException exception)
        {
            return new LinkCheckResult(
                LinkCheckStatus.Unknown,
                $"Could not reach {url.Host}: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LinkCheckResult(
                LinkCheckStatus.Unknown,
                $"{url.Host} did not answer in time.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri url,
        bool rangeProbe,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);

        if (rangeProbe)
        {
            request.Headers.Range = new RangeHeaderValue(0, 0);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        // Headers only: the body (if any) is never read.
        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
    }

    internal static LinkCheckResult FromResponse(HttpResponseMessage response, Uri url)
    {
        HttpStatusCode status = response.StatusCode;

        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new LinkCheckResult(
                LinkCheckStatus.Offline,
                $"{url.Host} says the file is gone ({(int)status}).");
        }

        if (HostThrottlePolicy.IsThrottleStatusCode(status))
        {
            return new LinkCheckResult(
                LinkCheckStatus.Limited,
                $"{url.Host} is limiting requests ({(int)status}); try again later.");
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new LinkCheckResult(
                LinkCheckStatus.NeedsAttention,
                $"{url.Host} requires a login or blocked the check ({(int)status}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new LinkCheckResult(
                LinkCheckStatus.Unknown,
                $"{url.Host} answered HTTP {(int)status}.");
        }

        HttpContentHeaders headers = response.Content.Headers;
        string? mediaType = headers.ContentType?.MediaType;

        if (
            mediaType is "text/html" or "application/xhtml+xml" &&
            headers.ContentDisposition is null)
        {
            return new LinkCheckResult(
                LinkCheckStatus.NeedsAttention,
                "This link opens a web page, not a file. Use Crawl page to find the files on it.");
        }

        long? size = headers.ContentRange?.Length ?? (
            status == HttpStatusCode.PartialContent ? null : headers.ContentLength);

        Uri answered = response.RequestMessage?.RequestUri ?? url;

        return new LinkCheckResult(
            LinkCheckStatus.Online,
            "Online",
            HttpDownloadTransferService.SuggestFileName(headers, answered),
            size);
    }

    private static bool LooksRemoved(string message)
    {
        return
            message.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
