using System.Net;
using System.Net.Http;
using DeezNutzDownloader.Core.Settings;
using DeezNutzDownloader.Infrastructure.Security;

namespace DeezNutzDownloader.App.Networking;

internal static class DownloadHttpClientFactory
{
    public static HttpClient Create(
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? unprotectedPassword =
            ProxyCredentialProtector.Unprotect(
                settings.ProxyPasswordProtected);

        ResolvedProxy resolved =
            ProxyConfiguration.Resolve(
                settings,
                unprotectedPassword);

        bool useProxy =
            resolved.Kind != ProxyResolutionKind.Direct;

        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            Proxy = resolved.Proxy,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime =
                TimeSpan.FromMinutes(10)
        };

        if (resolved.UseDefaultCredentials)
        {
            handler.DefaultProxyCredentials =
                CredentialCache.DefaultCredentials;
        }

        return new HttpClient(
            handler,
            disposeHandler: true)
        {
            // Large downloads must not be terminated by
            // HttpClient's default 100-second overall timeout.
            // The orchestrator's CancellationToken remains
            // authoritative.
            Timeout =
                System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Validates the proxy settings without sending traffic.
    /// Throws a user-facing message when invalid.
    /// </summary>
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? unprotectedPassword =
            ProxyCredentialProtector.Unprotect(
                settings.ProxyPasswordProtected);

        _ = ProxyConfiguration.Resolve(
            settings,
            unprotectedPassword);

        _ = ProxyConfiguration.BuildYtDlpProxyUrl(
            settings,
            unprotectedPassword);
    }
}

