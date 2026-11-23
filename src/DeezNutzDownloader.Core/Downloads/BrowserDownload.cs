namespace DeezNutzDownloader.Core.Downloads;

/// <summary>How the embedded browser gets to the file.</summary>
public enum BrowserDownloadMode
{
    /// <summary>Bunkr's file page, then its download page's button.</summary>
    Bunkr,

    /// <summary>
    /// Any page: press a same-site Download control if one is found,
    /// otherwise wait for the user to press it. The file is captured
    /// either way.
    /// </summary>
    Generic
}

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
        BrowserDownloadMode mode,
        IProgress<DownloadTransferProgress>? progress,
        CancellationToken cancellationToken);
}
