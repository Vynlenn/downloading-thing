namespace DeezNutzDownloader.Core.Links;

/// <summary>
/// A host page resolved to its real file: where to download and
/// what the host calls it. TotalBytes is known when the host says
/// so up front (Pixeldrain info) and null otherwise.
/// </summary>
public sealed record ResolvedHostFile(
    Uri DownloadUri,
    string? SuggestedFileName,
    long? TotalBytes,
    string? ExpectedSha256 = null);

/// <summary>
/// Turns host pages (/u/ID) into direct downloads. Implementations
/// throw ChallengeDetectedException for solvable walls and
/// HostThrottledException for quota, so callers need no host
/// special-casing.
/// </summary>
public interface IHostFileResolver
{
    bool CanResolve(Uri page);

    Task<ResolvedHostFile> ResolveAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A host folder resolved to its files: a display name plus one
/// entry per downloadable file. Callers queue one item per file
/// with the folder name as the relative folder.
/// </summary>
public sealed record ResolvedHostFolder(
    string FolderName,
    IReadOnlyList<ResolvedHostFile> Files);

/// <summary>
/// Turns host folder shares (Gofile /d/, Pixeldrain /l/) into
/// per-file downloads. Same typed errors as file resolvers.
/// </summary>
public interface IHostFolderResolver
{
    bool CanResolveFolder(Uri page);

    Task<ResolvedHostFolder> ResolveFolderAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default);
}
