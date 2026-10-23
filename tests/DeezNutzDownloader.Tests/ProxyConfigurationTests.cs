using System.Net;
using DeezNutzDownloader.Core.Settings;

namespace DeezNutzDownloader.Tests;

public sealed class ProxyConfigurationTests
{
    [Fact]
    public void Resolve_Direct_DisablesProxy()
    {
        var settings = ManualSettings();
        settings.NetworkMode = NetworkMode.Direct;

        ResolvedProxy resolved =
            ProxyConfiguration.Resolve(settings);

        Assert.Equal(ProxyResolutionKind.Direct, resolved.Kind);
        Assert.Null(resolved.Proxy);
        Assert.False(resolved.UseDefaultCredentials);
    }

    [Fact]
    public void Resolve_WindowsSettings_UsesSystemDefault()
    {
        var settings = ManualSettings();
        settings.NetworkMode = NetworkMode.WindowsSettings;

        ResolvedProxy resolved =
            ProxyConfiguration.Resolve(settings);

        Assert.Equal(
            ProxyResolutionKind.SystemDefault,
            resolved.Kind);
        Assert.True(resolved.UseDefaultCredentials);
    }

    [Fact]
    public void Resolve_ManualNoAuth_BuildsAddressAndBypassesLocal()
    {
        ResolvedProxy resolved =
            ProxyConfiguration.Resolve(ManualSettings());

        Assert.Equal(ProxyResolutionKind.Manual, resolved.Kind);

        var proxy = Assert.IsType<WebProxy>(resolved.Proxy);

        Assert.Equal(
            new Uri("http://proxy.test:8080/"),
            proxy.Address);
        Assert.True(proxy.BypassProxyOnLocal);
        Assert.Null(proxy.Credentials);
    }

    [Fact]
    public void Resolve_ManualWithLogin_AttachesCredentials()
    {
        var settings = ManualSettings();
        settings.ProxyUsername = "bob";

        ResolvedProxy resolved =
            ProxyConfiguration.Resolve(settings, "s3cr3t");

        var proxy = Assert.IsType<WebProxy>(resolved.Proxy);
        var credentials =
            Assert.IsType<NetworkCredential>(proxy.Credentials);

        Assert.Equal("bob", credentials.UserName);
        Assert.Equal("s3cr3t", credentials.Password);
    }

    [Fact]
    public void Resolve_MissingHost_Throws()
    {
        var settings = ManualSettings();
        settings.ProxyHost = "   ";

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => ProxyConfiguration.Resolve(settings));

        Assert.Contains("proxy server", exception.Message);
    }

    [Fact]
    public void Resolve_BadPort_Throws()
    {
        var settings = ManualSettings();
        settings.ProxyPort = 99999;

        Assert.Throws<InvalidOperationException>(
            () => ProxyConfiguration.Resolve(settings));
    }

    [Fact]
    public void Resolve_Socks5_ThrowsNotSupported()
    {
        var settings = ManualSettings();
        settings.ProxyType = ProxyType.Socks5;

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => ProxyConfiguration.Resolve(settings));

        Assert.Contains("SOCKS", exception.Message);
    }

    [Fact]
    public void BuildYtDlpProxyUrl_Direct_ReturnsNull()
    {
        var settings = ManualSettings();
        settings.NetworkMode = NetworkMode.Direct;

        Assert.Null(
            ProxyConfiguration.BuildYtDlpProxyUrl(settings));
    }

    [Fact]
    public void BuildYtDlpProxyUrl_NoAuth_ReturnsPlainUrl()
    {
        Assert.Equal(
            "http://proxy.test:8080/",
            ProxyConfiguration.BuildYtDlpProxyUrl(
                ManualSettings()));
    }

    [Fact]
    public void BuildYtDlpProxyUrl_WithLogin_EncodesCredentials()
    {
        var settings = ManualSettings();
        settings.ProxyUsername = "bob";

        Assert.Equal(
            "http://bob:p%40ss%3Aword@proxy.test:8080/",
            ProxyConfiguration.BuildYtDlpProxyUrl(
                settings,
                "p@ss:word"));
    }

    private static AppSettings ManualSettings()
    {
        return new AppSettings
        {
            NetworkMode = NetworkMode.ManualProxy,
            ProxyType = ProxyType.Http,
            ProxyHost = "proxy.test",
            ProxyPort = 8080,
        };
    }
}
