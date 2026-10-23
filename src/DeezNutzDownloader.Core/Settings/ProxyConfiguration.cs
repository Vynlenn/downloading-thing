using System.Net;

namespace DeezNutzDownloader.Core.Settings;

/// <summary>
/// How the app should route traffic for the current settings.
/// </summary>
public enum ProxyResolutionKind
{
    Direct,
    SystemDefault,
    Manual,
}

public sealed record ResolvedProxy(
    ProxyResolutionKind Kind,
    IWebProxy? Proxy,
    bool UseDefaultCredentials)
{
    public static ResolvedProxy Direct { get; } =
        new(ProxyResolutionKind.Direct, null, false);

    public static ResolvedProxy SystemDefault { get; } =
        new(ProxyResolutionKind.SystemDefault, null, true);
}

/// <summary>
/// Pure, testable proxy resolution. OS-specific secret handling
/// (DPAPI unprotect) stays with the caller: the password arrives
/// here already unprotected and is never persisted by this type.
/// </summary>
public static class ProxyConfiguration
{
    public static ResolvedProxy Resolve(
        AppSettings settings,
        string? unprotectedPassword = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.NetworkMode)
        {
            case NetworkMode.Direct:
                return ResolvedProxy.Direct;

            case NetworkMode.WindowsSettings:
                return ResolvedProxy.SystemDefault;

            case NetworkMode.ManualProxy:
                return ResolveManual(settings, unprotectedPassword);

            default:
                throw new InvalidOperationException(
                    "The saved network mode is unsupported.");
        }
    }

    /// <summary>
    /// Builds the `--proxy` URL for yt-dlp, or null when traffic
    /// should not use a manual proxy. Credentials are URL-encoded.
    /// </summary>
    public static string? BuildYtDlpProxyUrl(
        AppSettings settings,
        string? unprotectedPassword = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.NetworkMode != NetworkMode.ManualProxy)
        {
            return null;
        }

        Uri address = BuildManualAddress(settings);
        string username = settings.ProxyUsername?.Trim() ?? string.Empty;

        if (username.Length == 0)
        {
            return address.AbsoluteUri;
        }

        string userInfo =
            Uri.EscapeDataString(username) +
            ":" +
            Uri.EscapeDataString(unprotectedPassword ?? string.Empty);

        var builder = new UriBuilder(address) { UserName = null, Password = null };

        // UriBuilder would escape for us, but it cannot express
        // an empty-password userinfo the same way; build manually.
        return $"{builder.Scheme}://{userInfo}@{builder.Host}:{builder.Port}/";
    }

    private static ResolvedProxy ResolveManual(
        AppSettings settings,
        string? unprotectedPassword)
    {
        Uri address = BuildManualAddress(settings);

        var proxy = new WebProxy(address)
        {
            BypassProxyOnLocal = true,
        };

        string username = settings.ProxyUsername?.Trim() ?? string.Empty;

        if (username.Length > 0)
        {
            proxy.Credentials =
                new NetworkCredential(
                    username,
                    unprotectedPassword ?? string.Empty);
        }

        return new ResolvedProxy(
            ProxyResolutionKind.Manual,
            proxy,
            UseDefaultCredentials: false);
    }

    private static Uri BuildManualAddress(AppSettings settings)
    {
        if (
            settings.ProxyType is ProxyType.Socks4 or ProxyType.Socks5
        )
        {
            throw new InvalidOperationException(
                "SOCKS proxies are not supported in this version. " +
                "Choose HTTP or HTTPS as the proxy type.");
        }

        if (string.IsNullOrWhiteSpace(settings.ProxyHost))
        {
            throw new InvalidOperationException(
                "Enter a proxy server name or IP address.");
        }

        if (settings.ProxyPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "The proxy port must be between 1 and 65535.");
        }

        string host = settings.ProxyHost.Trim();

        if (
            host.Contains("://", StringComparison.Ordinal) ||
            host.IndexOfAny(new[] { '/', '\\', '?', '#', '@' }) >= 0 ||
            host.Any(char.IsWhiteSpace)
        )
        {
            throw new InvalidOperationException(
                "Enter only the proxy server name or IP " +
                "address; choose its type and port separately.");
        }

        string scheme = settings.ProxyType switch
        {
            ProxyType.Http => "http",
            ProxyType.Https => "https",
            _ => throw new InvalidOperationException(
                "The saved proxy type is unsupported."),
        };

        try
        {
            Uri address =
                new UriBuilder(scheme, host, settings.ProxyPort).Uri;

            if (string.IsNullOrWhiteSpace(address.Host))
            {
                throw new UriFormatException(
                    "The proxy host was empty.");
            }

            return address;
        }
        catch (Exception exception)
            when (
                exception is ArgumentException or
                UriFormatException
            )
        {
            throw new InvalidOperationException(
                "The proxy server name or IP address is invalid.",
                exception);
        }
    }
}
