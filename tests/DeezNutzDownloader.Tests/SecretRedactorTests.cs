using DeezNutzDownloader.Infrastructure.Logging;

namespace DeezNutzDownloader.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void RedactsSensitiveQueryValues()
    {
        const string input =
            "https://example.test/file?token=abc123&name=photo.jpg";

        string result = SecretRedactor.Redact(input);

        Assert.DoesNotContain("abc123", result);
        Assert.Contains("token=[REDACTED]", result);
        Assert.Contains("name=photo.jpg", result);
    }

    [Fact]
    public void RedactsCredentialsEmbeddedInUrl()
    {
        const string input =
            "http://username:password@proxy.example.test:8080";

        string result = SecretRedactor.Redact(input);

        Assert.DoesNotContain("username", result);
        Assert.DoesNotContain("password", result);
        Assert.Contains("[REDACTED]", result);
    }
}
