using System.Buffers;
using CG.Web.MegaApiClient;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Adapters.Media;

/// <summary>
/// MEGA public-link downloads via MegaApiClient (MIT), behind the
/// Core <see cref="IMegaDownloader"/> contract so the transfer
/// service stays testable. Anonymous login; no MEGA account needed.
/// Note: MegaApiClient manages its own connections, so the app
/// proxy does not apply to MEGA traffic (documented limit).
/// </summary>
public sealed class MegaDownloadService : IMegaDownloader
{
    public async Task<MegaFileMetadata> GetInfoAsync(
        Uri link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!MegaLinks.IsLink(link))
        {
            throw new ArgumentException(
                "Not a MEGA file or folder link (key missing?).",
                nameof(link));
        }

        var client = new MegaApiClient();

        try
        {
            await client.LoginAnonymousAsync().ConfigureAwait(false);

            INode node = await client.GetNodeFromLinkAsync(link)
                .ConfigureAwait(false);

            return new MegaFileMetadata(
                node.Name,
                node.Size,
                node.Type == NodeType.Directory);
        }
        catch (ApiException exception)
        {
            throw MapApiError(
                link,
                exception.ApiResultCode,
                exception.Message);
        }
        finally
        {
            await TryLogoutAsync(client).ConfigureAwait(false);
        }
    }

    /// <returns>The decrypted file path (== destinationPath).</returns>
    public async Task<string> DownloadToAsync(
        Uri link,
        string destinationPath,
        long? totalBytes,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException(
                "A destination file path is required.",
                nameof(destinationPath));
        }

        if (!MegaLinks.IsLink(link))
        {
            throw new ArgumentException(
                "Not a MEGA file or folder link (key missing?).",
                nameof(link));
        }

        progress?.Report(new DownloadTransferProgress(0, totalBytes));

        var client = new MegaApiClient();

        try
        {
            await client.LoginAnonymousAsync().ConfigureAwait(false);

            using Stream remote = await client.DownloadAsync(
                    link,
                    progress: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await using var local = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous |
                    FileOptions.SequentialScan);

            byte[] buffer =
                ArrayPool<byte>.Shared.Rent(81_920);

            try
            {
                long written = 0;

                while (true)
                {
                    int read = await remote.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    await local.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);

                    checked
                    {
                        written += read;
                    }

                    progress?.Report(
                        new DownloadTransferProgress(
                            written,
                            totalBytes));
                }

                await local.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            progress?.Report(
                new DownloadTransferProgress(
                    new FileInfo(destinationPath).Length,
                    totalBytes));

            return destinationPath;
        }
        catch (ApiException exception)
        {
            throw MapApiError(
                link,
                exception.ApiResultCode,
                exception.Message);
        }
        catch (DownloadException exception)
        {
            throw new InvalidOperationException(
                $"MEGA failed while fetching {link.Host} content: " +
                exception.Message.Trim(),
                exception);
        }
        finally
        {
            await TryLogoutAsync(client).ConfigureAwait(false);
        }
    }

    private static async Task TryLogoutAsync(MegaApiClient client)
    {
        try
        {
            await client.LogoutAsync().ConfigureAwait(false);
        }
        catch
        {
            // Anonymous sessions expire server-side; logout is
            // politeness, never load-bearing.
        }
    }

    public static Exception MapApiError(
        Uri link,
        ApiResultCode result,
        string? message)
    {
        string host = link.Host;
        string detail = message?.Trim() ?? string.Empty;

        return result switch
        {
            ApiResultCode.QuotaExceeded =>
                new HostThrottledException(
                    host,
                    $"{host} transfer quota is exhausted for this IP. " +
                    "Wait for reset — the item stays queued.",
                    isQuotaExhausted: true),

            ApiResultCode.TooManyRequests or
            ApiResultCode.TooManyConnectionsOnThisResource or
            ApiResultCode.ToManyRequestsForThisResource =>
                new HostThrottledException(
                    host,
                    $"{host} is rate limiting. Wait a minute and " +
                    "retry — the item stays queued."),

            ApiResultCode.ResourceNotExists or
            ApiResultCode.ResourceExpired =>
                new InvalidOperationException(
                    "That MEGA link is removed or expired. " +
                    "Retrying will not help — remove it from the queue."),

            ApiResultCode.AccessDenied or
            ApiResultCode.ResourceAdministrativelyBlocked =>
                new InvalidOperationException(
                    "MEGA refused that link (blocked or private). " +
                    "Open it in a browser to check."),

            _ =>
                new InvalidOperationException(
                    $"MEGA reported an error ({result}): {detail}"),
        };
    }
}
