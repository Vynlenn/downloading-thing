using DeezNutzDownloader.Infrastructure.Security;

namespace DeezNutzDownloader.Tests;

public sealed class ProxyCredentialProtectorTests
{
    [Fact]
    public void RoundTrip_RestoresPassword()
    {
        string protectedValue =
            ProxyCredentialProtector.Protect("s3cr3t-p@ss!");

        Assert.NotEmpty(protectedValue);
        Assert.DoesNotContain("s3cr3t", protectedValue);

        Assert.Equal(
            "s3cr3t-p@ss!",
            ProxyCredentialProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Empty_StaysEmpty()
    {
        Assert.Equal(
            string.Empty,
            ProxyCredentialProtector.Protect(string.Empty));

        Assert.Null(
            ProxyCredentialProtector.Unprotect(string.Empty));

        Assert.Null(
            ProxyCredentialProtector.Unprotect(null));
    }

    [Fact]
    public void CorruptValue_ThrowsFriendlyError()
    {
        // Valid base64, but not DPAPI ciphertext.
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => ProxyCredentialProtector.Unprotect("aGk="));

        Assert.Contains("proxy password", exception.Message);
    }
}
