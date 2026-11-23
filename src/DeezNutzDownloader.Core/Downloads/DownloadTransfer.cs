namespace DeezNutzDownloader.Core.Downloads;

/// <param name="Connections">
/// Upper bound on parallel range connections for one file. The
/// transfer service only segments large files on servers that
/// support ranges, never on per-IP throttled file hosts.
/// </param>
/// <param name="ResumeKey">
/// When set (the queue item id, 32 hex chars) the partial file is
/// kept under a stable name with its resume state, so a cancel,
/// pause, quota stop, crash or restart continues where it left off.
/// </param>
public sealed record DownloadTransferRequest(
    Uri Source,
    string DestinationDirectory,
    long? MaxBytesPerSecond = null,
    int Connections = 1,
    string? ResumeKey = null);

public sealed record DownloadTransferProgress(
    long BytesReceived,
    long? TotalBytes)
{
    public double? Percentage
    {
        get
        {
            if (TotalBytes is null)
            {
                return null;
            }

            if (TotalBytes.Value == 0)
            {
                return BytesReceived == 0
                    ? 100d
                    : null;
            }

            return Math.Min(
                100d,
                BytesReceived * 100d / TotalBytes.Value);
        }
    }
}

public sealed record DownloadTransferResult(
    Uri Source,
    string DestinationPath,
    string FileName,
    long BytesWritten,
    long? TotalBytes);

public interface IDownloadTransferService
{
    Task<DownloadTransferResult> DownloadAsync(
        DownloadTransferRequest request,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
