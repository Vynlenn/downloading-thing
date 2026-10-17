namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// Quiet-hours speed policy: inside the window downloads run at
/// the capped rate, outside it they run full speed. Overnight
/// windows (22:00-07:00) work — the interval wraps midnight.
/// Pure and testable; the clock arrives as a parameter.
/// </summary>
public sealed record QuietHoursSchedule(
    bool Enabled,
    TimeSpan Start,
    TimeSpan End,
    long CappedBytesPerSecond)
{
    public static QuietHoursSchedule Disabled { get; } =
        new(false, TimeSpan.Zero, TimeSpan.Zero, 0);

    /// <returns>Capped bytes/sec when quiet now, else null (= full).</returns>
    public long? EffectiveCapBytesPerSecond(TimeOnly now)
    {
        if (!Enabled || CappedBytesPerSecond <= 0)
        {
            return null;
        }

        TimeOnly start = TimeOnly.FromTimeSpan(Start);
        TimeOnly end = TimeOnly.FromTimeSpan(End);

        if (start == end)
        {
            return null;
        }

        bool inside = start <= end
            ? now >= start && now < end
            : now >= start || now < end;

        return inside ? CappedBytesPerSecond : null;
    }

    public static string FormatRate(long bytesPerSecond)
    {
        const double oneKb = 1024d;
        const double oneMb = oneKb * 1024d;

        return bytesPerSecond switch
        {
            >= (long)oneMb => $"{bytesPerSecond / oneMb:0.#} MB/s",
            >= (long)oneKb => $"{bytesPerSecond / oneKb:0.#} KB/s",
            _ => $"{bytesPerSecond} B/s",
        };
    }
}

/// <summary>
/// Paces a byte stream at or under a cap. Call after each chunk
/// with the window's byte total and elapsed time; the returned
/// delay is zero when under budget.
/// </summary>
public static class TransferThrottler
{
    public static TimeSpan ComputeDelay(
        long bytesInWindow,
        TimeSpan elapsedInWindow,
        long? capBytesPerSecond)
    {
        if (
            capBytesPerSecond is null or <= 0 ||
            bytesInWindow <= 0)
        {
            return TimeSpan.Zero;
        }

        double expectedSeconds =
            (double)bytesInWindow / capBytesPerSecond.Value;

        double waitSeconds =
            expectedSeconds - elapsedInWindow.TotalSeconds;

        if (waitSeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        // Clamp single waits so cancellation stays responsive.
        return TimeSpan.FromSeconds(Math.Min(waitSeconds, 2d));
    }
}
