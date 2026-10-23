using System.Net;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class HostThrottlePolicyTests
{
    [Theory]
    [InlineData("pixeldrain.com", true)]
    [InlineData("dl.pixeldrain.com", true)]
    [InlineData("gofile.io", true)]
    [InlineData("mega.nz", true)]
    [InlineData("1fichier.com", true)]
    [InlineData("example.test", false)]
    [InlineData("youtube.com", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSingleLaneHost_ClassifiesHosts(
        string? host,
        bool expected)
    {
        Assert.Equal(expected, HostThrottlePolicy.IsSingleLaneHost(host));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void IsThrottleStatusCode_Only429And503(
        HttpStatusCode status,
        bool expected)
    {
        Assert.Equal(
            expected,
            HostThrottlePolicy.IsThrottleStatusCode(status));
    }

    [Fact]
    public void TryParseRetryAfter_ParsesSeconds()
    {
        using var response =
            new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        response.Headers.TryAddWithoutValidation("Retry-After", "42");

        TimeSpan? parsed =
            HostThrottlePolicy.TryParseRetryAfter(
                response.Headers,
                DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(42), parsed);
    }

    [Fact]
    public void TryParseRetryAfter_ParsesHttpDate()
    {
        using var response =
            new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset retryAt = now.AddSeconds(30);

        response.Headers.TryAddWithoutValidation(
            "Retry-After",
            retryAt.ToString("R"));

        TimeSpan? parsed =
            HostThrottlePolicy.TryParseRetryAfter(
                response.Headers,
                now);

        Assert.NotNull(parsed);
        Assert.InRange(
            parsed.Value.TotalSeconds,
            25,
            35);
    }

    [Fact]
    public void TryParseRetryAfter_MissingHeader_ReturnsNull()
    {
        using var response =
            new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Null(
            HostThrottlePolicy.TryParseRetryAfter(
                response.Headers,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ComputeWaitBeforeRetry_HonorsShortServerWait()
    {
        TimeSpan? wait =
            HostThrottlePolicy.ComputeWaitBeforeRetry(
                attempt: 1,
                retryAfter: TimeSpan.FromSeconds(5),
                random: new Random(1));

        Assert.Equal(TimeSpan.FromSeconds(5), wait);
    }

    [Fact]
    public void ComputeWaitBeforeRetry_LongServerWait_ReturnsNull()
    {
        // A multi-hour quota wait must surface to the user,
        // not sleep inside the transfer service.
        TimeSpan? wait =
            HostThrottlePolicy.ComputeWaitBeforeRetry(
                attempt: 1,
                retryAfter: TimeSpan.FromHours(6),
                random: new Random(1));

        Assert.Null(wait);
    }

    [Fact]
    public void ComputeWaitBeforeRetry_WithoutHeader_BackoffGrows()
    {
        var random = new Random(7);

        TimeSpan? first =
            HostThrottlePolicy.ComputeWaitBeforeRetry(
                1,
                null,
                random);

        TimeSpan? second =
            HostThrottlePolicy.ComputeWaitBeforeRetry(
                2,
                null,
                random);

        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.InRange(first.Value.TotalMilliseconds, 1000, 1600);
        Assert.InRange(second.Value.TotalMilliseconds, 2000, 2600);
    }

    [Fact]
    public void FriendlyMessage_Pixeldrain403_ExplainsQuota()
    {
        string message =
            HostThrottlePolicy.FriendlyMessage(
                "pixeldrain.com",
                HttpStatusCode.Forbidden,
                retryAfter: null,
                isPixeldrainQuotaBody: true);

        Assert.Contains("quota", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Premium", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendlyMessage_429WithRetryAfter_StatesWait()
    {
        string message =
            HostThrottlePolicy.FriendlyMessage(
                "gofile.io",
                HttpStatusCode.TooManyRequests,
                TimeSpan.FromSeconds(30));

        Assert.Contains("429", message, StringComparison.Ordinal);
        Assert.Contains("30", message, StringComparison.Ordinal);
    }
}
