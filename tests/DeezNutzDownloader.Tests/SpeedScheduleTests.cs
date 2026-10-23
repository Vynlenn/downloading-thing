using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class SpeedScheduleTests
{
    [Fact]
    public void DisabledSchedule_NeverCaps()
    {
        Assert.Null(
            QuietHoursSchedule.Disabled.EffectiveCapBytesPerSecond(
                new TimeOnly(2, 0)));
    }

    [Fact]
    public void DayWindow_CapsInsideOnly()
    {
        var schedule = new QuietHoursSchedule(
            true,
            new TimeSpan(1, 0, 0),
            new TimeSpan(6, 0, 0),
            512_000);

        Assert.Equal(
            512_000,
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(3, 0)));
        Assert.Null(
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(7, 0)));
        Assert.Null(
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(0, 30)));
    }

    [Fact]
    public void OvernightWindow_WrapsMidnight()
    {
        var schedule = new QuietHoursSchedule(
            true,
            new TimeSpan(22, 0, 0),
            new TimeSpan(7, 0, 0),
            256_000);

        Assert.Equal(
            256_000,
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(23, 30)));
        Assert.Equal(
            256_000,
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(3, 0)));
        Assert.Null(
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(12, 0)));
    }

    [Fact]
    public void ZeroCap_MeansUncapped()
    {
        var schedule = new QuietHoursSchedule(
            true,
            TimeSpan.Zero,
            new TimeSpan(23, 59, 59),
            0);

        Assert.Null(
            schedule.EffectiveCapBytesPerSecond(new TimeOnly(12, 0)));
    }

    [Fact]
    public void FormatRate_ReadableUnits()
    {
        Assert.Equal("512 KB/s", QuietHoursSchedule.FormatRate(524_288));
        Assert.Equal("2 MB/s", QuietHoursSchedule.FormatRate(2_097_152));
    }

    [Fact]
    public void Throttler_UnderBudget_NoWait()
    {
        Assert.Equal(
            TimeSpan.Zero,
            TransferThrottler.ComputeDelay(
                1_000,
                TimeSpan.FromSeconds(5),
                1_000_000));
    }

    [Fact]
    public void Throttler_OverBudget_WaitsDifference()
    {
        TimeSpan wait = TransferThrottler.ComputeDelay(
            2_000_000,
            TimeSpan.FromSeconds(1),
            1_000_000);

        // 2 MB at 1 MB/s needs 2s; 1s elapsed => ~1s wait.
        Assert.InRange(wait.TotalSeconds, 0.9, 1.1);
    }

    [Fact]
    public void Throttler_NullCap_NoWait()
    {
        Assert.Equal(
            TimeSpan.Zero,
            TransferThrottler.ComputeDelay(
                10_000_000,
                TimeSpan.Zero,
                null));
    }

    [Fact]
    public void BuildDownload_WithRateLimit_EmitsFlag()
    {
        List<string> arguments =
            DeezNutzDownloader.Adapters.Media.YtDlpArgumentBuilder
                .BuildDownload(
                    "https://video.test/v/1",
                    formatId: null,
                    outputTemplate: "out",
                    ffmpegPath: string.Empty,
                    limitBytesPerSecond: 524_288);

        int index = arguments.IndexOf("--limit-rate");
        Assert.True(index >= 0);
        Assert.Equal("524288", arguments[index + 1]);
    }

    [Fact]
    public void BuildDownload_WithoutRateLimit_OmitsFlag()
    {
        List<string> arguments =
            DeezNutzDownloader.Adapters.Media.YtDlpArgumentBuilder
                .BuildDownload(
                    "https://video.test/v/1",
                    formatId: null,
                    outputTemplate: "out",
                    ffmpegPath: string.Empty,
                    limitBytesPerSecond: null);

        Assert.DoesNotContain("--limit-rate", arguments);
    }

    [Fact]
    public void Throttler_WaitClamped_ForResponsiveness()
    {
        TimeSpan wait = TransferThrottler.ComputeDelay(
            100_000_000,
            TimeSpan.Zero,
            1_000);

        Assert.True(wait <= TimeSpan.FromSeconds(2.01));
    }
}
