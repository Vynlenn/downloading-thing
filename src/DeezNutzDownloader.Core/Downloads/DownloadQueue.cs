using System.Collections.ObjectModel;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Core.Downloads;

public sealed record DownloadQueueItem(
    Guid Id,
    string Url,
    string DisplayName)
{
    public string Status => "Queued";
}

public sealed record DownloadQueueImportResult(
    int AddedCount,
    int AlreadyQueuedCount,
    int IgnoredCount);

public sealed class DownloadQueue
{
    private readonly List<DownloadQueueItem> _items = new();
    private readonly HashSet<string> _queuedUrls =
        new(StringComparer.Ordinal);

    private readonly ReadOnlyCollection<DownloadQueueItem>
        _readOnlyItems;

    public DownloadQueue()
    {
        _readOnlyItems = _items.AsReadOnly();
    }

    public IReadOnlyList<DownloadQueueItem> Items =>
        _readOnlyItems;

    public int Count => _items.Count;

    public DownloadQueueImportResult Enqueue(
        LinkAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        int addedCount = 0;
        int alreadyQueuedCount = 0;
        int ignoredCount = 0;

        foreach (LinkAnalysisItem candidate in analysis.Items)
        {
            if (
                candidate.Status != LinkAnalysisStatus.Valid ||
                string.IsNullOrWhiteSpace(candidate.NormalizedUrl) ||
                !TryCreateSupportedUri(
                    candidate.NormalizedUrl,
                    out Uri normalizedUri)
            )
            {
                ignoredCount++;
                continue;
            }

            string normalizedUrl = candidate.NormalizedUrl;

            if (!_queuedUrls.Add(normalizedUrl))
            {
                alreadyQueuedCount++;
                continue;
            }

            _items.Add(
                new DownloadQueueItem(
                    Guid.NewGuid(),
                    normalizedUrl,
                    GetDisplayName(normalizedUri)));

            addedCount++;
        }

        return new DownloadQueueImportResult(
            addedCount,
            alreadyQueuedCount,
            ignoredCount);
    }

    public bool Remove(Guid itemId)
    {
        int index = _items.FindIndex(
            item => item.Id == itemId);

        if (index < 0)
        {
            return false;
        }

        DownloadQueueItem item = _items[index];

        _items.RemoveAt(index);
        _queuedUrls.Remove(item.Url);

        return true;
    }

    public int Clear()
    {
        int removedCount = _items.Count;

        _items.Clear();
        _queuedUrls.Clear();

        return removedCount;
    }

    private static bool TryCreateSupportedUri(
        string value,
        out Uri uri)
    {
        if (
            !Uri.TryCreate(
                value,
                UriKind.Absolute,
                out Uri? candidate) ||
            (
                !string.Equals(
                    candidate.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    candidate.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            uri = null!;
            return false;
        }

        uri = candidate;
        return true;
    }

    private static string GetDisplayName(Uri uri)
    {
        if (uri.Segments.Length == 0)
        {
            return uri.Host;
        }

        string escapedName =
            uri.Segments[^1].Trim('/');

        if (string.IsNullOrWhiteSpace(escapedName))
        {
            return uri.Host;
        }

        string displayName;

        try
        {
            displayName =
                Uri.UnescapeDataString(escapedName);
        }
        catch (UriFormatException)
        {
            displayName = escapedName;
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? uri.Host
            : displayName;
    }
}
