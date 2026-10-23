using System.Net;
using System.Net.Sockets;
using System.Text;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Settings;
using DeezNutzDownloader.Infrastructure.Security;

namespace DeezNutzDownloader.Tests;

/// <summary>
/// Proves the real settings-to-wire path: DPAPI protect, resolve
/// the proxy, run a download through a loopback forward proxy,
/// and confirm the proxy actually saw the request.
/// </summary>
public sealed class ProxyLoopbackTests
{
    [Fact]
    public async Task DownloadAsync_ViaManualProxy_RoutesThroughProxy()
    {
        byte[] payload = CreatePayload(32_768);

        using var proxy = new MinimalForwardProxy(payload);

        var settings = new AppSettings
        {
            NetworkMode = NetworkMode.ManualProxy,
            ProxyType = ProxyType.Http,
            ProxyHost = "127.0.0.1",
            ProxyPort = proxy.Port,
        };

        using HttpClient client = CreateProxiedClient(settings);

        var service = new HttpDownloadTransferService(client);
        string directory = CreateTemporaryDirectory();

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri("http://origin.test/file.bin"),
                        directory));

            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(
                    result.DestinationPath));

            // Absolute-URI request-targets are only sent to proxies.
            Assert.NotEmpty(proxy.RequestTargets);
            Assert.All(
                proxy.RequestTargets,
                target => Assert.StartsWith(
                    "http://origin.test/",
                    target,
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_ViaAuthenticatedProxy_SendsCredentials()
    {
        byte[] payload = CreatePayload(8_192);

        using var proxy = new MinimalForwardProxy(
            payload,
            requiredUsername: "bob",
            requiredPassword: "s3cr3t");

        var settings = new AppSettings
        {
            NetworkMode = NetworkMode.ManualProxy,
            ProxyType = ProxyType.Http,
            ProxyHost = "127.0.0.1",
            ProxyPort = proxy.Port,
            ProxyUsername = "bob",
            ProxyPasswordProtected =
                ProxyCredentialProtector.Protect("s3cr3t"),
        };

        using HttpClient client = CreateProxiedClient(settings);

        var service = new HttpDownloadTransferService(client);
        string directory = CreateTemporaryDirectory();

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri("http://origin.test/secure.bin"),
                        directory));

            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(
                    result.DestinationPath));

            Assert.True(proxy.SawValidAuthorization);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static HttpClient CreateProxiedClient(
        AppSettings settings)
    {
        string? password =
            ProxyCredentialProtector.Unprotect(
                settings.ProxyPasswordProtected);

        ResolvedProxy resolved =
            ProxyConfiguration.Resolve(settings, password);

        var webProxy = Assert.IsType<WebProxy>(resolved.Proxy);

        // Production bypasses local addresses; the test peer lives
        // on loopback, so routing must be forced for this check.
        webProxy.BypassProxyOnLocal = false;

        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = webProxy,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];

        for (int index = 0; index < length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "DeezNutzDownloader.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// A minimal HTTP forward proxy: enough of the protocol to
    /// prove client routing and Basic proxy-auth, nothing more.
    /// </summary>
    private sealed class MinimalForwardProxy : IDisposable
    {
        private readonly byte[] _payload;
        private readonly string? _requiredAuthHeader;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _acceptLoop;
        private bool _disposed;

        public MinimalForwardProxy(
            byte[] payload,
            string? requiredUsername = null,
            string? requiredPassword = null)
        {
            _payload = payload;

            if (
                requiredUsername is not null &&
                requiredPassword is not null
            )
            {
                _requiredAuthHeader =
                    "Basic " +
                    Convert.ToBase64String(
                        Encoding.ASCII.GetBytes(
                            requiredUsername +
                            ":" +
                            requiredPassword));
            }

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _acceptLoop = AcceptLoopAsync(_stopping.Token);
        }

        public int Port { get; }

        public List<string> RequestTargets { get; } = new();

        public bool SawValidAuthorization { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping.Cancel();
            _listener.Stop();

            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            _stopping.Dispose();
        }

        private async Task AcceptLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client =
                        await _listener.AcceptTcpClientAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = HandleAsync(client, cancellationToken);
            }
        }

        private async Task HandleAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                string head = await ReadHeadAsync(
                        stream,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(head))
                {
                    return;
                }

                string[] lines = head.Split(
                    new[] { "\r\n" },
                    StringSplitOptions.None);

                string requestLine =
                    lines.Length > 0 ? lines[0] : string.Empty;

                string[] parts = requestLine.Split(' ');

                if (parts.Length >= 2)
                {
                    lock (RequestTargets)
                    {
                        RequestTargets.Add(parts[1]);
                    }
                }

                string? authHeader = lines
                    .Skip(1)
                    .FirstOrDefault(line =>
                        line.StartsWith(
                            "Proxy-Authorization:",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Split(':', 2)[1].Trim();

                if (
                    _requiredAuthHeader is not null &&
                    !string.Equals(
                        authHeader,
                        _requiredAuthHeader,
                        StringComparison.Ordinal)
                )
                {
                    byte[] challenge = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                        "Proxy-Authenticate: Basic realm=\"test\"\r\n" +
                        "Content-Length: 0\r\n" +
                        "Connection: close\r\n\r\n");

                    await stream.WriteAsync(
                            challenge,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                if (
                    _requiredAuthHeader is not null &&
                    string.Equals(
                        authHeader,
                        _requiredAuthHeader,
                        StringComparison.Ordinal)
                )
                {
                    SawValidAuthorization = true;
                }

                byte[] headerBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    $"Content-Length: {_payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                await stream.WriteAsync(
                        headerBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                await stream.WriteAsync(
                        _payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadHeadAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var head = new StringBuilder();

            while (head.Length < 32_768)
            {
                int read = await stream.ReadAsync(
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                head.Append((char)buffer[0]);

                if (
                    head.Length >= 4 &&
                    head[^4] == '\r' &&
                    head[^3] == '\n' &&
                    head[^2] == '\r' &&
                    head[^1] == '\n'
                )
                {
                    break;
                }
            }

            return head.ToString();
        }
    }
}
