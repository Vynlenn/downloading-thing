namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// When to try again after a host limit (quota, 429, 503).
/// The app waits out limits the host sets; it never tries to
/// dodge them. Partial files are kept, so a wait costs no data.
/// </summary>
public static class ThrottleRetryPlanner
{
    public const int MaximumAutomaticRetries = 6;

    // Daily quotas (Pixeldrain ~5 GB/day per IP) reset slowly.
    private static readonly TimeSpan[] QuotaWaits =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(4),
    };

    private static readonly TimeSpan[] RateLimitWaits =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(30),
    };

    private static readonly TimeSpan LongestHonoredRetryAfter =
        TimeSpan.FromHours(12);

    /// <returns>
    /// The time of the next attempt, or null when automatic retries
    /// are used up and the item should fail for the user to decide.
    /// </returns>
    public static DateTimeOffset? NextAttempt(
        HostThrottledException exception,
        int retriesSoFar,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (retriesSoFar < 0 || retriesSoFar >= MaximumAutomaticRetries)
        {
            return null;
        }

        TimeSpan wait;

        if (exception.RetryAfter is TimeSpan retryAfter && retryAfter > TimeSpan.Zero)
        {
            // The host said exactly when: wait that long (plus a
            // little slack), never shorter.
            wait = retryAfter > LongestHonoredRetryAfter
                ? LongestHonoredRetryAfter
                : retryAfter + TimeSpan.FromSeconds(5);
        }
        else
        {
            wait = exception.IsQuotaExhausted
                ? QuotaWaits[retriesSoFar]
                : RateLimitWaits[retriesSoFar];
        }

        return nowUtc + wait;
    }

    public static string DescribeWait(
        HostThrottledException exception,
        DateTimeOffset retryAtUtc)
    {
        string when = retryAtUtc.ToLocalTime().ToString("HH:mm");

        string reason = exception.IsQuotaExhausted
            ? $"{exception.Host} quota reached"
            : $"{exception.Host} is limiting downloads";

        return $"{reason}; partial file kept. Retrying automatically at {when}.";
    }
}
