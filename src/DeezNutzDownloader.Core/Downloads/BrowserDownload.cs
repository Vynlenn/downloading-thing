namespace DeezNutzDownloader.Core.Downloads;

/// <summary>A file a real browser session downloaded into a temporary path.</summary>
public sealed record BrowserDownloadResult(
    string TemporaryPath,
    string? SuggestedFileName,
    long BytesWritten,
    string? MediaType);

/// <summary>
/// Downloads a file by driving the host's own web page in an
/// embedded browser, exactly as a person clicking Download would.
/// Used for hosts whose files only come through their page flow
/// (Bunkr). The transfer service finishes the file afterwards with
/// the same naming, extension and Mark-of-the-Web steps as any
/// other download.
/// </summary>
public interface IBrowserDownloader
{
    Task<BrowserDownloadResult> DownloadAsync(
        Uri page,
        string destinationDirectory,
        string? displayName,
        IProgress<DownloadTransferProgress>? progress,
        CancellationToken cancellationToken);
}
