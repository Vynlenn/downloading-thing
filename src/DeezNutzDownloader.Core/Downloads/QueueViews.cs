namespace DeezNutzDownloader.Core.Downloads;

/// <summary>Which rows the queue shows.</summary>
public enum QueueFilterKind
{
    All,
    Active,
    Waiting,
    Paused,
    Completed,
    Failed,
    Flagged,
    LinkProblems
}

public sealed record QueueSummary(
    int Total,
    int Active,
    int Waiting,
    int Paused,
    int Completed,
    int Failed,
    long BytesPerSecond,
    TimeSpan? EstimatedRemaining)
{
    public static QueueSummary Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// Queue filtering and the status-bar summary, kept out of the
/// window so they are plain, testable functions.
/// </summary>
public static class QueueViews
{
    public static bool Matches(
        DownloadQueueItem item,
        string? searchText,
        QueueFilterKind kind)
    {
        ArgumentNullException.ThrowIfNull(item);

        bool kindMatches = kind switch
        {
            QueueFilterKind.Active =>
                item.State == DownloadQueueItemState.Downloading,
            QueueFilterKind.Waiting =>
                item.State == DownloadQueueItemState.Queued,
            QueueFilterKind.Paused =>
                item.State == DownloadQueueItemState.Paused,
            QueueFilterKind.Completed =>
                item.State == DownloadQueueItemState.Completed,
            QueueFilterKind.Failed =>
                item.State is DownloadQueueItemState.Failed or DownloadQueueItemState.Canceled,
            QueueFilterKind.Flagged =>
                item.Safety.Level != FileSafetyLevel.None,
            QueueFilterKind.LinkProblems =>
                item.LinkStatus is
                    LinkCheckStatus.Offline or
                    LinkCheckStatus.NeedsAttention or
                    LinkCheckStatus.Limited,
            _ => true
        };

        if (!kindMatches)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        string needle = searchText.Trim();

        return
            Contains(item.EffectiveDisplayName, needle) ||
            Contains(item.Url, needle) ||
            Contains(item.PackageName, needle) ||
            (Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? uri) &&
                Contains(uri.Host, needle));
    }

    public static IReadOnlyList<DownloadQueueItem> Filter(
        IReadOnlyList<DownloadQueueItem> items,
        string? searchText,
        QueueFilterKind kind)
    {
        if (kind == QueueFilterKind.All && string.IsNullOrWhiteSpace(searchText))
        {
            return items;
        }

        return items
            .Where(item => Matches(item, searchText, kind))
            .ToArray();
    }

    public static QueueSummary Summarize(IReadOnlyList<DownloadQueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int active = 0, waiting = 0, paused = 0, completed = 0, failed = 0;
        long speed = 0;
        long remainingKnown = 0;
        bool remainingUnknown = false;

        foreach (DownloadQueueItem item in items)
        {
            switch (item.State)
            {
                case DownloadQueueItemState.Downloading:
                    active++;
                    speed += Math.Max(0, item.SpeedBytesPerSecond ?? 0);

                    if (item.TotalBytes is long total)
                    {
                        remainingKnown += Math.Max(0, total - item.BytesReceived);
                    }
                    else
                    {
                        remainingUnknown = true;
                    }

                    break;

                case DownloadQueueItemState.Queued:
                    waiting++;
                    break;

                case DownloadQueueItemState.Paused:
                    paused++;
                    break;

                case DownloadQueueItemState.Completed:
                    completed++;
                    break;

                case DownloadQueueItemState.Failed:
                case DownloadQueueItemState.Canceled:
                    failed++;
                    break;
            }
        }

        // An ETA is only honest for what is downloading right now
        // and only when every active size is known.
        TimeSpan? eta =
            active > 0 && speed > 0 && !remainingUnknown
                ? TimeSpan.FromSeconds(remainingKnown / (double)speed)
                : null;

        return new QueueSummary(
            items.Count,
            active,
            waiting,
            paused,
            completed,
            failed,
            speed,
            eta);
    }

    public static string FormatSummary(QueueSummary summary)
    {
        if (summary.Total == 0)
        {
            return "Queue is empty";
        }

        var parts = new List<string>();

        if (summary.Active > 0)
        {
            parts.Add(
                $"{summary.Active} downloading at {FormatRate(summary.BytesPerSecond)}" +
                (summary.EstimatedRemaining is TimeSpan eta ? $" · {FormatEta(eta)} left" : string.Empty));
        }

        if (summary.Waiting > 0)
        {
            parts.Add($"{summary.Waiting} waiting");
        }

        if (summary.Paused > 0)
        {
            parts.Add($"{summary.Paused} paused");
        }

        if (summary.Completed > 0)
        {
            parts.Add($"{summary.Completed} done");
        }

        if (summary.Failed > 0)
        {
            parts.Add($"{summary.Failed} failed or canceled");
        }

        return parts.Count == 0 ? $"{summary.Total} items" : string.Join(" · ", parts);
    }

    public static string FormatRate(long bytesPerSecond)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double value = Math.Max(0, bytesPerSecond);
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours}h {eta.Minutes}m";
        }

        if (eta.TotalMinutes >= 1)
        {
            return $"{eta.Minutes}m {eta.Seconds}s";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))}s";
    }

    private static bool Contains(string? haystack, string needle)
    {
        return haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
    }
}
