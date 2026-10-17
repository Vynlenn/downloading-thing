namespace DeezNutzDownloader.Core.Links;

/// <summary>
/// A host page resolved to its real file: where to download and
/// what the host calls it. TotalBytes is known when the host says
/// so up front (Pixeldrain info) and null otherwise.
/// </summary>
public sealed record ResolvedHostFile(
    Uri DownloadUri,
    string? SuggestedFileName,
    long? TotalBytes);

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
