namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// Polite per-host throttling policy for free file hosts.
/// These limits are enforced server-side per IP — the client
/// cannot lift them, it can only back off and give a clear message.
/// </summary>
public sealed class HostThrottledException : Exception
{
    public string Host { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsQuotaExhausted { get; }

    public HostThrottledException(
        string host,
        string message,
        TimeSpan? retryAfter = null,
        bool isQuotaExhausted = false)
        : base(message)
    {
        Host = host;
        RetryAfter = retryAfter;
        IsQuotaExhausted = isQuotaExhausted;
    }
}

public static class HostThrottlePolicy
{
    private static readonly HashSet<string> SingleLaneHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pixeldrain.com",
            "pixeldrain.nl",
            "gofile.io",
            "mega.nz",
            "mega.io",
            "mediafire.com",
            "www.mediafire.com",
            "1fichier.com",
        };

    private const int MaxAttempts = 4;
    private const int BaseDelayMs = 1_000;
    private const int MaxDelayMs = 30_000;
    private const int MaxHonorRetryAfterSeconds = 60;

    public static bool IsSingleLaneHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string normalized = host.Trim().TrimEnd('.');

        if (SingleLaneHosts.Contains(normalized))
        {
            return true;
        }

        // Subdomains inherit the policy, e.g. dl.pixeldrain.com.
        foreach (string gated in SingleLaneHosts)
        {
            if (
                normalized.EndsWith(
                    "." + gated,
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsThrottleStatusCode(
        System.Net.HttpStatusCode status)
    {
        return
            status == System.Net.HttpStatusCode.TooManyRequests ||
            status == System.Net.HttpStatusCode.ServiceUnavailable;
    }

    public static bool ShouldRetryAttempt(
        System.Net.HttpStatusCode status,
        int attempt)
    {
        return IsThrottleStatusCode(status) && attempt < MaxAttempts;
    }

    public static TimeSpan? TryParseRetryAfter(
        System.Net.Http.Headers.HttpResponseHeaders? headers,
        DateTimeOffset nowUtc)
    {
        if (headers is null)
        {
            return null;
        }

        if (!headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        string? raw =
            values.FirstOrDefault()?.Trim().Trim('"');

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (
            int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int seconds)
        )
        {
            if (seconds < 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(
                Math.Min(seconds, 24 * 3600));
        }

        if (
            DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset retryDate)
        )
        {
            TimeSpan delay = retryDate - nowUtc;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    /// <summary>
    /// Honor a short server-directed wait, otherwise exponential
    /// backoff with jitter. Returns null when the wait is so long
    /// the caller should surface it instead of sleeping.
    /// </summary>
    public static TimeSpan? ComputeWaitBeforeRetry(
        int attempt,
        TimeSpan? retryAfter,
        Random? random = null)
    {
        if (
            retryAfter.HasValue &&
            retryAfter.Value.TotalSeconds > MaxHonorRetryAfterSeconds
        )
        {
            return null;
        }

        if (retryAfter.HasValue)
        {
            return retryAfter.Value;
        }

        double exponential =
            BaseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));

        double capped = Math.Min(exponential, MaxDelayMs);
        double jitter =
            (random ?? Random.Shared).NextDouble() * 500d;

        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    public static string FriendlyMessage(
        string host,
        System.Net.HttpStatusCode status,
        TimeSpan? retryAfter,
        bool isPixeldrainQuotaBody = false)
    {
        string shortHost = string.IsNullOrWhiteSpace(host)
            ? "This host"
            : host;

        if (
            isPixeldrainQuotaBody ||
            (IsPixeldrainHost(host) &&
             status == System.Net.HttpStatusCode.Forbidden)
        )
        {
            return
                $"{shortHost} free quota is exhausted for this IP " +
                $"(Pixeldrain: ~5 GB/day, then ~1 MiB/s). " +
                $"Wait for reset or use Premium. " +
                $"Queued items stay retryable — no need to re-add them.";
        }

        if (status == System.Net.HttpStatusCode.TooManyRequests)
        {
            return retryAfter.HasValue
                ? $"{shortHost} is rate limiting (429). " +
                  $"Retry in {FormatWait(retryAfter.Value)}. " +
                  $"Queued — will succeed if you retry after the wait."
                : $"{shortHost} is rate limiting (429). " +
                  $"Wait a minute and retry. " +
                  $"Lower concurrency to 1 for this host.";
        }

        if (status == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            return $"{shortHost} is temporarily unavailable (503). " +
                   $"Retry shortly — item stays queued.";
        }

        if (status == System.Net.HttpStatusCode.Forbidden)
        {
            return $"{shortHost} refused the download (403). " +
                   $"This is usually a host limit (concurrency, " +
                   $"hotlink protection, or quota), not a bug. " +
                   $"Wait / complete any CAPTCHA in a browser, " +
                   $"or use Premium.";
        }

        return $"{shortHost} throttled the request ({(int)status}). " +
               $"Wait and retry — item stays queued.";
    }

    private static bool IsPixeldrainHost(string? host)
    {
        return
            !string.IsNullOrWhiteSpace(host) &&
            host.Contains(
                "pixeldrain",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatWait(TimeSpan wait)
    {
        if (wait.TotalHours >= 1)
        {
            return $"{wait.TotalHours:0.#}h";
        }

        if (wait.TotalMinutes >= 1)
        {
            return $"{wait.TotalMinutes:0.#} min";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))}s";
    }
}
