namespace DeezNutzDownloader.Core.Downloads;

public sealed record DownloadTransferRequest(
    Uri Source,
    string DestinationDirectory,
    long? MaxBytesPerSecond = null);

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
