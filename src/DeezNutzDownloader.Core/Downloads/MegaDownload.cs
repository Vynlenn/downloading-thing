namespace DeezNutzDownloader.Core.Downloads;

public sealed record MegaFileMetadata(
    string Name,
    long Size,
    bool IsFolder);

public sealed record MegaFolderFile(
    string RelativePath,
    string Name,
    long Size);

/// <summary>
/// MEGA links carry their key in the fragment and serve encrypted
/// bytes, so they bypass the generic HTTP transfer service.
/// </summary>
public interface IMegaDownloader
{
    Task<MegaFileMetadata> GetInfoAsync(
        Uri link,
        CancellationToken cancellationToken = default);

    /// <returns>The decrypted file path (== destinationPath).</returns>
    Task<string> DownloadToAsync(
        Uri link,
        string destinationPath,
        long? totalBytes,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MegaFolderFile>> ListFolderAsync(
        Uri link,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads every file in a folder link into destination
    /// directory (flat, collision-safe). Returns total bytes.
    /// </summary>
    Task<long> DownloadFolderToAsync(
        Uri link,
        string destinationDirectory,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public static class MegaLinks
{
    public static bool IsLink(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (
            !uri.Host.Equals("mega.nz", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("mega.io", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("mega.co.nz", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".mega.nz", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".mega.io", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /file/{id}#key and /folder/{id}#key. The fragment key is
        // mandatory: without it nothing can be decrypted.
        return
            segments.Length == 2 &&
            (segments[0] == "file" || segments[0] == "folder") &&
            !string.IsNullOrWhiteSpace(uri.Fragment) &&
            uri.Fragment.Length > 1;
    }
}
