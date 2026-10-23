namespace DeezNutzDownloader.Core.Downloads;

/// <summary>
/// A finished download whose bytes do not match the host's
/// advertised hash. The file is deliberately KEPT on disk
/// (KeptPath) for inspection; the queue item flags failed with
/// the path so nothing is lost silently.
/// </summary>
public sealed class ChecksumMismatchException : Exception
{
    public Uri DownloadSource { get; }

    public string KeptPath { get; }

    public ChecksumMismatchException(
        Uri downloadSource,
        string keptPath,
        string message)
        : base(message)
    {
        DownloadSource = downloadSource;
        KeptPath = keptPath;
    }
}
