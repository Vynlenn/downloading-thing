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
    string? ErrorMessage = null,
    long? SpeedBytesPerSecond = null,
    string? PackageName = null,
    string? RelativeFolder = null)
{
    public string Status => State.ToString();

    public bool IsActive =>
        State == DownloadQueueItemState.Downloading;

    public bool CanRemove => !IsActive;

    public bool CanRetry =>
        State is
            DownloadQueueItemState.Failed or
            DownloadQueueItemState.Canceled;

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

    public string? EstimatedRemainingText
    {
        get
        {
            if (
                State !=
                    DownloadQueueItemState.Downloading ||
                SpeedBytesPerSecond is not > 0 ||
                TotalBytes is not long totalBytes
            )
            {
                return null;
            }

            long remainingBytes =
                Math.Max(0, totalBytes - BytesReceived);

            if (remainingBytes == 0)
            {
                return "finishing…";
            }

            double remainingSeconds =
                remainingBytes /
                (double)SpeedBytesPerSecond;

            return FormatDuration(remainingSeconds) +
                " left";
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
                $"{ProgressPercentage:0.0}%" +
                FormatSpeedSuffix() +
                FormatEtaSuffix(),

            DownloadQueueItemState.Downloading =>
                $"{FormatBytes(BytesReceived)} downloaded" +
                FormatSpeedSuffix(),

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

    private string FormatSpeedSuffix()
    {
        return SpeedBytesPerSecond is > 0
            ? $" · {FormatBytes(SpeedBytesPerSecond.Value)}/s"
            : string.Empty;
    }

    private string FormatEtaSuffix()
    {
        return EstimatedRemainingText is string eta
            ? $" · {eta}"
            : string.Empty;
    }

    private static string FormatDuration(
        double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) ||
            double.IsInfinity(totalSeconds))
        {
            return "calculating…";
        }

        long seconds =
            Math.Max(0, (long)Math.Ceiling(totalSeconds));

        if (seconds < 60)
        {
            return $"{seconds}s";
        }

        long minutes = seconds / 60;
        long leftoverSeconds = seconds % 60;

        if (minutes < 60)
        {
            return leftoverSeconds == 0
                ? $"{minutes}m"
                : $"{minutes}m {leftoverSeconds}s";
        }

        long hours = minutes / 60;
        long leftoverMinutes = minutes % 60;

        return leftoverMinutes == 0
            ? $"{hours}h"
            : $"{hours}h {leftoverMinutes}m";
    }

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

    private readonly Dictionary<Guid, SpeedSample>
        _speedSamples = new();

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
                        GetDisplayName(normalizedUri),
                        PackageName: normalizedUri.Host
                            .ToLowerInvariant()));

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

    public DownloadQueueImportResult EnqueuePackages(
        IReadOnlyList<LinkPackage> packages,
        bool organizeIntoSiteFolders)
    {
        ArgumentNullException.ThrowIfNull(packages);

        DownloadQueueImportResult result;

        lock (_sync)
        {
            int addedCount = 0;
            int alreadyQueuedCount = 0;
            int ignoredCount = 0;

            foreach (LinkPackage package in packages)
            {
                string? folder =
                    organizeIntoSiteFolders
                        ? PackageFolderNames.Sanitize(
                            package.Name)
                        : null;

                foreach (
                    LinkAnalysisItem candidate in package.Links
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
                            GetDisplayName(normalizedUri),
                            PackageName: package.Name,
                            RelativeFolder: folder));

                    addedCount++;
                }
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
                ErrorMessage = null,
                SpeedBytesPerSecond = null
            };

            _speedSamples[current.Id] =
                new SpeedSample(
                    0,
                    DateTimeOffset.UtcNow,
                    0d);

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

            long? speedBytesPerSecond =
                UpdateSpeedSample(
                    current.Id,
                    bytesReceived);

            if (
                bytesReceived != current.BytesReceived ||
                totalBytes != current.TotalBytes ||
                speedBytesPerSecond !=
                    current.SpeedBytesPerSecond
            )
            {
                _items[index] = current with
                {
                    BytesReceived = bytesReceived,
                    TotalBytes = totalBytes,
                    SpeedBytesPerSecond =
                        speedBytesPerSecond
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
                ErrorMessage = null,
                SpeedBytesPerSecond = null
            };

            _speedSamples.Remove(current.Id);

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
                    NormalizeErrorMessage(errorMessage),
                SpeedBytesPerSecond = null
            };

            _speedSamples.Remove(current.Id);

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
                ErrorMessage = null,
                SpeedBytesPerSecond = null
            };

            _speedSamples.Remove(current.Id);

            changed = true;
        }

        if (changed)
        {
            OnChanged();
        }

        return true;
    }

    public bool TryResetForRetry(Guid itemId)
    {
        bool reset;

        lock (_sync)
        {
            int index = _items.FindIndex(
                item => item.Id == itemId);

            if (
                index < 0 ||
                !CanRetryState(_items[index].State)
            )
            {
                return false;
            }

            DownloadQueueItem current = _items[index];

            _items[index] = current with
            {
                State = DownloadQueueItemState.Queued,
                BytesReceived = 0,
                TotalBytes = null,
                DestinationPath = null,
                SavedFileName = null,
                ErrorMessage = null,
                SpeedBytesPerSecond = null
            };

            _speedSamples.Remove(current.Id);

            reset = true;
        }

        if (reset)
        {
            OnChanged();
        }

        return true;
    }

    public int ClearCompleted()
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

            int beforeCount = _items.Count;

            _items.RemoveAll(
                item =>
                    item.State is
                        DownloadQueueItemState.Completed or
                        DownloadQueueItemState.Failed or
                        DownloadQueueItemState.Canceled);

            removedCount = beforeCount - _items.Count;

            if (removedCount == 0)
            {
                return 0;
            }

            RebuildUrlIndexLocked();
            _speedSamples.Clear();
        }

        OnChanged();

        return removedCount;
    }

    public void RestoreSnapshot(
        IReadOnlyList<DownloadQueueItem> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            _items.Clear();
            _queuedUrls.Clear();
            _speedSamples.Clear();

            foreach (DownloadQueueItem persisted in snapshot)
            {
                if (
                    string.IsNullOrWhiteSpace(persisted.Url) ||
                    !TryCreateSupportedUri(
                        persisted.Url,
                        out Uri normalizedUri)
                )
                {
                    continue;
                }

                string normalizedUrl = persisted.Url.Trim();

                if (!_queuedUrls.Add(normalizedUrl))
                {
                    continue;
                }

                DownloadQueueItemState state = persisted.State;

                if (
                    state is DownloadQueueItemState.Downloading ||
                    !Enum.IsDefined(state)
                )
                {
                    state = DownloadQueueItemState.Queued;
                }

                string displayName =
                    string.IsNullOrWhiteSpace(
                        persisted.DisplayName)
                        ? GetDisplayName(normalizedUri)
                        : persisted.DisplayName;

                _items.Add(
                    new DownloadQueueItem(
                        persisted.Id == Guid.Empty
                            ? Guid.NewGuid()
                            : persisted.Id,
                        normalizedUrl,
                        displayName,
                        state,
                        state == DownloadQueueItemState.Queued
                            ? 0
                            : Math.Max(0, persisted.BytesReceived),
                        state == DownloadQueueItemState.Queued
                            ? null
                            : NormalizeTotalBytes(
                                persisted.TotalBytes),
                        state
                            is DownloadQueueItemState.Completed
                            ? persisted.DestinationPath
                            : null,
                        state
                            is DownloadQueueItemState.Completed
                            ? persisted.SavedFileName
                            : null,
                        state is DownloadQueueItemState.Failed
                            ? NormalizeErrorMessage(
                                persisted.ErrorMessage)
                            : null,
                        null,
                        string.IsNullOrWhiteSpace(
                            persisted.PackageName)
                            ? null
                            : persisted.PackageName.Trim(),
                        PackageFolderNames.IsSafeSegment(
                            persisted.RelativeFolder)
                            ? persisted.RelativeFolder
                            : null));
            }
        }

        OnChanged();
    }

    private static bool CanRetryState(
        DownloadQueueItemState state)
    {
        return state is
            DownloadQueueItemState.Failed or
            DownloadQueueItemState.Canceled;
    }

    private static long? NormalizeTotalBytes(
        long? totalBytes)
    {
        return totalBytes is < 0 ? null : totalBytes;
    }

    private void RebuildUrlIndexLocked()
    {
        _queuedUrls.Clear();

        foreach (DownloadQueueItem item in _items)
        {
            _queuedUrls.Add(item.Url);
        }
    }

    private long? UpdateSpeedSample(
        Guid itemId,
        long bytesReceived)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!_speedSamples.TryGetValue(
                itemId,
                out SpeedSample? sample))
        {
            _speedSamples[itemId] =
                new SpeedSample(
                    bytesReceived,
                    now,
                    0d);

            return null;
        }

        double elapsedSeconds =
            (now - sample.LastSeen).TotalSeconds;

        long byteDelta =
            bytesReceived - sample.LastBytes;

        if (elapsedSeconds < 0.05 || byteDelta <= 0)
        {
            return sample.SmoothedBytesPerSecond > 0
                ? (long)sample.SmoothedBytesPerSecond
                : null;
        }

        double instantSpeed =
            byteDelta / elapsedSeconds;

        double smoothed =
            sample.SmoothedBytesPerSecond <= 0
                ? instantSpeed
                : sample.SmoothedBytesPerSecond * 0.7 +
                    instantSpeed * 0.3;

        _speedSamples[itemId] =
            new SpeedSample(
                bytesReceived,
                now,
                smoothed);

        return smoothed > 0
            ? (long)smoothed
            : null;
    }

    private sealed class SpeedSample
    {
        public SpeedSample(
            long lastBytes,
            DateTimeOffset lastSeen,
            double smoothedBytesPerSecond)
        {
            LastBytes = lastBytes;
            LastSeen = lastSeen;
            SmoothedBytesPerSecond =
                smoothedBytesPerSecond;
        }

        public long LastBytes { get; }

        public DateTimeOffset LastSeen { get; }

        public double SmoothedBytesPerSecond { get; }
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
