using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Core.Downloads;

public enum DownloadQueueItemState
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Canceled
}

public sealed record DownloadQueueItem(
    Guid Id,
    string Url,
    string DisplayName,
    DownloadQueueItemState State =
        DownloadQueueItemState.Queued,
    long BytesReceived = 0,
    long? TotalBytes = null,
    string? DestinationPath = null,
    string? SavedFileName = null,
    string? ErrorMessage = null)
{
    public string Status => State.ToString();

    public bool IsActive =>
        State == DownloadQueueItemState.Downloading;

    public bool CanRemove => !IsActive;

    public bool IsIndeterminate =>
        State == DownloadQueueItemState.Downloading &&
        TotalBytes is null;

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(SavedFileName)
            ? DisplayName
            : SavedFileName;

    public double ProgressPercentage
    {
        get
        {
            if (State == DownloadQueueItemState.Completed)
            {
                return 100d;
            }

            if (TotalBytes is null)
            {
                return 0d;
            }

            if (TotalBytes.Value == 0)
            {
                return BytesReceived == 0
                    ? 100d
                    : 0d;
            }

            return Math.Clamp(
                BytesReceived * 100d / TotalBytes.Value,
                0d,
                100d);
        }
    }

    public string ProgressText =>
        State switch
        {
            DownloadQueueItemState.Queued =>
                "Waiting to start",

            DownloadQueueItemState.Downloading
                when TotalBytes is long totalBytes =>
                $"{FormatBytes(BytesReceived)} of " +
                $"{FormatBytes(totalBytes)} · " +
                $"{ProgressPercentage:0.0}%",

            DownloadQueueItemState.Downloading =>
                $"{FormatBytes(BytesReceived)} downloaded",

            DownloadQueueItemState.Completed =>
                $"Saved as {EffectiveDisplayName} · " +
                $"{FormatBytes(BytesReceived)}",

            DownloadQueueItemState.Failed =>
                BytesReceived > 0
                    ? $"Failed after {FormatBytes(BytesReceived)}"
                    : "Download failed",

            DownloadQueueItemState.Canceled =>
                BytesReceived > 0
                    ? $"Canceled after {FormatBytes(BytesReceived)}"
                    : "Canceled",

            _ =>
                string.Empty
        };

    private static string FormatBytes(long byteCount)
    {
        string[] units =
        {
            "B",
            "KB",
            "MB",
            "GB",
            "TB"
        };

        double value = Math.Max(0, byteCount);
        int unitIndex = 0;

        while (
            value >= 1024d &&
            unitIndex < units.Length - 1
        )
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
    }
}

public sealed record DownloadQueueImportResult(
    int AddedCount,
    int AlreadyQueuedCount,
    int IgnoredCount);

public sealed class DownloadQueue
{
    private readonly object _sync = new();
    private readonly List<DownloadQueueItem> _items = new();

    private readonly HashSet<string> _queuedUrls =
        new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public IReadOnlyList<DownloadQueueItem> Items
    {
        get
        {
            lock (_sync)
            {
                return _items.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public DownloadQueueImportResult Enqueue(
        LinkAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        DownloadQueueImportResult result;

        lock (_sync)
        {
            int addedCount = 0;
            int alreadyQueuedCount = 0;
            int ignoredCount = 0;

            foreach (
                LinkAnalysisItem candidate in analysis.Items
            )
            {
                if (
                    candidate.Status !=
                        LinkAnalysisStatus.Valid ||
                    string.IsNullOrWhiteSpace(
                        candidate.NormalizedUrl) ||
                    !TryCreateSupportedUri(
                        candidate.NormalizedUrl,
                        out Uri normalizedUri)
                )
                {
                    ignoredCount++;
                    continue;
                }

                string normalizedUrl =
                    candidate.NormalizedUrl;

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

            result = new DownloadQueueImportResult(
                addedCount,
                alreadyQueuedCount,
                ignoredCount);
        }

        if (result.AddedCount > 0)
        {
            OnChanged();
        }

        return result;
    }

    public bool Remove(Guid itemId)
    {
        bool removed;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                _items[index].State ==
                    DownloadQueueItemState.Downloading
            )
            {
                return false;
            }

            DownloadQueueItem item = _items[index];

            _items.RemoveAt(index);
            _queuedUrls.Remove(item.Url);

            removed = true;
        }

        if (removed)
        {
            OnChanged();
        }

        return removed;
    }

    public int Clear()
    {
        int removedCount;

        lock (_sync)
        {
            if (
                _items.Any(
                    item =>
                        item.State ==
                        DownloadQueueItemState.Downloading)
            )
            {
                return 0;
            }

            removedCount = _items.Count;

            if (removedCount == 0)
            {
                return 0;
            }

            _items.Clear();
            _queuedUrls.Clear();
        }

        OnChanged();

        return removedCount;
    }

    internal IReadOnlyList<DownloadQueueItem>
        GetStartableItems()
    {
        lock (_sync)
        {
            return _items
                .Where(item => IsStartable(item.State))
                .ToArray();
        }
    }

    internal bool TryMarkDownloading(Guid itemId)
    {
        bool changed;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                !IsStartable(_items[index].State)
            )
            {
                return false;
            }

            DownloadQueueItem current = _items[index];

            _items[index] = current with
            {
                State =
                    DownloadQueueItemState.Downloading,
                BytesReceived = 0,
                TotalBytes = null,
                DestinationPath = null,
                SavedFileName = null,
                ErrorMessage = null
            };

            changed = true;
        }

        if (changed)
        {
            OnChanged();
        }

        return true;
    }

    internal bool TryReportProgress(
        Guid itemId,
        DownloadTransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        bool accepted;
        bool changed = false;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                _items[index].State !=
                    DownloadQueueItemState.Downloading
            )
            {
                return false;
            }

            accepted = true;

            DownloadQueueItem current = _items[index];

            long bytesReceived = Math.Max(
                current.BytesReceived,
                Math.Max(0, progress.BytesReceived));

            long? reportedTotal = progress.TotalBytes;

            if (reportedTotal is < 0)
            {
                reportedTotal = null;
            }

            long? totalBytes =
                reportedTotal ?? current.TotalBytes;

            if (
                bytesReceived != current.BytesReceived ||
                totalBytes != current.TotalBytes
            )
            {
                _items[index] = current with
                {
                    BytesReceived = bytesReceived,
                    TotalBytes = totalBytes
                };

                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }

        return accepted;
    }

    internal bool TryMarkCompleted(
        Guid itemId,
        DownloadTransferResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        bool changed;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                _items[index].State !=
                    DownloadQueueItemState.Downloading
            )
            {
                return false;
            }

            DownloadQueueItem current = _items[index];

            long? totalBytes = result.TotalBytes;

            if (totalBytes is < 0)
            {
                totalBytes = current.TotalBytes;
            }

            _items[index] = current with
            {
                State =
                    DownloadQueueItemState.Completed,
                BytesReceived =
                    Math.Max(0, result.BytesWritten),
                TotalBytes = totalBytes,
                DestinationPath =
                    result.DestinationPath,
                SavedFileName = result.FileName,
                ErrorMessage = null
            };

            changed = true;
        }

        if (changed)
        {
            OnChanged();
        }

        return true;
    }

    internal bool TryMarkFailed(
        Guid itemId,
        string errorMessage)
    {
        bool changed;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                _items[index].State !=
                    DownloadQueueItemState.Downloading
            )
            {
                return false;
            }

            DownloadQueueItem current = _items[index];

            _items[index] = current with
            {
                State = DownloadQueueItemState.Failed,
                DestinationPath = null,
                SavedFileName = null,
                ErrorMessage =
                    NormalizeErrorMessage(errorMessage)
            };

            changed = true;
        }

        if (changed)
        {
            OnChanged();
        }

        return true;
    }

    internal bool TryMarkCanceled(Guid itemId)
    {
        bool changed;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                _items[index].State !=
                    DownloadQueueItemState.Downloading
            )
            {
                return false;
            }

            DownloadQueueItem current = _items[index];

            _items[index] = current with
            {
                State =
                    DownloadQueueItemState.Canceled,
                DestinationPath = null,
                SavedFileName = null,
                ErrorMessage = null
            };

            changed = true;
        }

        if (changed)
        {
            OnChanged();
        }

        return true;
    }

    private static bool IsStartable(
        DownloadQueueItemState state)
    {
        return state is
            DownloadQueueItemState.Queued or
            DownloadQueueItemState.Failed or
            DownloadQueueItemState.Canceled;
    }

    private static string NormalizeErrorMessage(
        string? errorMessage)
    {
        const int maximumLength = 500;

        string message = string.IsNullOrWhiteSpace(
            errorMessage)
                ? "The download failed."
                : errorMessage.Trim();

        return message.Length <= maximumLength
            ? message
            : message[..maximumLength];
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

    private void OnChanged()
    {
        EventHandler? handlers = Changed;

        if (handlers is null)
        {
            return;
        }

        foreach (
            Delegate subscriber in
            handlers.GetInvocationList()
        )
        {
            try
            {
                ((EventHandler)subscriber)(
                    this,
                    EventArgs.Empty);
            }
            catch
            {
                // A presentation observer must not corrupt
                // queue state or fail an active transfer.
            }
        }
    }
}
