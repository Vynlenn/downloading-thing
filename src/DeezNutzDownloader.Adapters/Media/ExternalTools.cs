using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace DeezNutzDownloader.Adapters.Media;

public enum ExternalTool
{
    YtDlp,
    FFmpeg,
}

/// <summary>
/// Where official tool binaries come from. All URL construction is
/// pure so tests pin the exact addresses without network access.
/// Manual path settings always win over managed tools.
/// </summary>
public static class ExternalToolCatalog
{
    public const string YtDlpLatestReleaseApiUrl =
        "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    public const string FFmpegWindowsBuildUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>Entry inside the gyan.dev essentials zip.</summary>
    public const string FFmpegZipEntryPath = "bin/ffmpeg.exe";

    public static string AssetFileName(ExternalTool tool)
    {
        return tool switch
        {
            ExternalTool.YtDlp => "yt-dlp.exe",
            ExternalTool.FFmpeg => "ffmpeg.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    public static string VersionArgument(ExternalTool tool)
    {
        return tool switch
        {
            ExternalTool.YtDlp => "--version",
            ExternalTool.FFmpeg => "-version",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    public static string BuildYtDlpAssetUrl(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException(
                "A yt-dlp version is required.",
                nameof(version));
        }

        string tag = version.Trim();

        return $"https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/yt-dlp.exe";
    }

    /// <summary>
    /// Parses the tag_name out of a GitHub latest-release API body
    /// (e.g. "2025.10.14"). Returns null when absent.
    /// </summary>
    public static string? ParseYtDlpLatestTag(string apiJson)
    {
        if (string.IsNullOrWhiteSpace(apiJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document =
                JsonDocument.Parse(apiJson);

            if (
                document.RootElement.TryGetProperty(
                    "tag_name",
                    out JsonElement tag) &&
                tag.ValueKind == JsonValueKind.String)
            {
                string? value = tag.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Parses "7.0.2-essentials_build-www.gyan.dev" out of an
    /// `ffmpeg -version` first line. Returns null when absent.
    /// </summary>
    public static string? ParseFFmpegVersion(string? firstLine)
    {
        const string prefix = "ffmpeg version ";

        if (
            string.IsNullOrWhiteSpace(firstLine) ||
            !firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string remainder = firstLine[prefix.Length..].Trim();
        int end = remainder.IndexOf(' ');

        string version =
            end < 0 ? remainder : remainder[..end].Trim();

        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public static string? ParseYtDlpVersion(string? firstLine)
    {
        string? version = firstLine?.Trim();

        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        // yt-dlp --version prints a bare date tag ("2025.10.14").
        return version.Contains(' ') ? null : version;
    }
}

public sealed record ManagedToolManifest(
    string Tool,
    string Version,
    string FileName,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Downloads and maintains yt-dlp/ffmpeg under a managed tools
/// directory (portable folder or %localappdata%). A configured
/// manual path always wins; without auto-manage the old bare-name
/// PATH fallback is preserved. Every install is smoke-tested
/// (`--version`/`-version`) before the manifest is written, so a
/// corrupt download can never look installed.
/// </summary>
public sealed class ExternalToolProvisioner
{
    private const string ManifestFileName = "tools-manifest.json";

    private readonly HttpClient _httpClient;
    private readonly string _toolsDirectory;
    private readonly Func<string, string, Task<string?>> _versionProbe;

    public ExternalToolProvisioner(
        HttpClient httpClient,
        string toolsDirectory,
        Func<string, string, Task<string?>>? versionProbe = null)
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));

        if (string.IsNullOrWhiteSpace(toolsDirectory))
        {
            throw new ArgumentException(
                "A tools directory is required.",
                nameof(toolsDirectory));
        }

        _toolsDirectory = Path.GetFullPath(toolsDirectory);

        _versionProbe = versionProbe ?? ProbeVersionAsync;
    }

    /// <summary>
    /// Resolves the executable path: manual setting first, managed
    /// tool (downloading it when allowed) second, bare-name PATH
    /// fallback last.
    /// </summary>
    public async Task<string> ResolveAsync(
        ExternalTool tool,
        string configuredPath,
        bool autoManage,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string trimmed = configuredPath?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(trimmed))
        {
            if (File.Exists(trimmed))
            {
                return trimmed;
            }

            if (
                !trimmed.Contains(Path.DirectorySeparatorChar) &&
                !trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                // Bare name: PATH resolution decides at launch.
                return trimmed;
            }
        }

        if (!autoManage)
        {
            return ExternalToolCatalog.AssetFileName(tool)
                .Replace(".exe", string.Empty);
        }

        return await EnsureAsync(tool, log, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> EnsureAsync(
        ExternalTool tool,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_toolsDirectory);

        string binaryPath = Path.Combine(
            _toolsDirectory,
            ExternalToolCatalog.AssetFileName(tool));

        if (await IsHealthyAsync(tool, binaryPath, cancellationToken)
            .ConfigureAwait(false))
        {
            return binaryPath;
        }

        log?.Report(
            $"{tool} is missing or broken — downloading the official build…");

        await DownloadAsync(tool, binaryPath, log, cancellationToken)
            .ConfigureAwait(false);

        string? version = await _versionProbe(
                binaryPath,
                ExternalToolCatalog.VersionArgument(tool))
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(version))
        {
            TryDelete(binaryPath);

            throw new InvalidOperationException(
                $"{tool} downloaded but failed its version check. " +
                $"Check the network/proxy and try again.");
        }

        await WriteManifestAsync(
                tool,
                NormalizeVersion(tool, version!))
            .ConfigureAwait(false);

        log?.Report($"{tool} ready ({version.Trim()}).");

        return binaryPath;
    }

    public async Task<bool> IsHealthyAsync(
        ExternalTool tool,
        string binaryPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(binaryPath))
        {
            return false;
        }

        try
        {
            string? version = await _versionProbe(
                    binaryPath,
                    ExternalToolCatalog.VersionArgument(tool))
                .ConfigureAwait(false);

            return !string.IsNullOrWhiteSpace(version);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> InstalledVersionAsync(
        ExternalTool tool,
        CancellationToken cancellationToken = default)
    {
        string binaryPath = Path.Combine(
            _toolsDirectory,
            ExternalToolCatalog.AssetFileName(tool));

        if (!File.Exists(binaryPath))
        {
            return null;
        }

        try
        {
            string? raw = await _versionProbe(
                    binaryPath,
                    ExternalToolCatalog.VersionArgument(tool))
                .ConfigureAwait(false);

            return NormalizeVersion(tool, raw);
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadAsync(
        ExternalTool tool,
        string binaryPath,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        if (tool == ExternalTool.YtDlp)
        {
            string version = await LatestYtDlpTagAsync(cancellationToken)
                .ConfigureAwait(false);

            string url =
                ExternalToolCatalog.BuildYtDlpAssetUrl(version);

            log?.Report($"Downloading yt-dlp {version}…");
            await DownloadFileAsync(url, binaryPath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        log?.Report("Downloading ffmpeg (essentials build)…");

        string zipPath = binaryPath + ".download.zip";

        try
        {
            await DownloadFileAsync(
                    ExternalToolCatalog.FFmpegWindowsBuildUrl,
                    zipPath,
                    cancellationToken)
                .ConfigureAwait(false);

            ExtractZipEntry(
                zipPath,
                ExternalToolCatalog.FFmpegZipEntryPath,
                binaryPath);
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private async Task<string> LatestYtDlpTagAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ExternalToolCatalog.YtDlpLatestReleaseApiUrl);

        // GitHub API requires a User-Agent.
        request.Headers.UserAgent.ParseAdd("DeezNutzDownloader");

        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(
                cancellationToken)
            .ConfigureAwait(false);

        return ExternalToolCatalog.ParseYtDlpLatestTag(json) ??
            throw new InvalidOperationException(
                "The yt-dlp release lookup returned no version. " +
                "Set a manual path in Settings or retry later.");
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using Stream source =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await source.CopyToAsync(destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ExtractZipEntry(
        string zipPath,
        string entryPath,
        string destinationPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // gyan.dev zips nest under a versioned root folder, so
        // match on the trailing path (bin/ffmpeg.exe).
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
            candidate =>
                candidate.FullName.Replace('\\', '/').EndsWith(
                    entryPath,
                    StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidOperationException(
                "The ffmpeg package did not contain bin/ffmpeg.exe. " +
                "Set a manual path in Settings or retry later.");
        }

        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static string? NormalizeVersion(
        ExternalTool tool,
        string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string firstLine = raw
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? string.Empty;

        return tool == ExternalTool.YtDlp
            ? ExternalToolCatalog.ParseYtDlpVersion(firstLine)
            : ExternalToolCatalog.ParseFFmpegVersion(firstLine);
    }

    private async Task WriteManifestAsync(
        ExternalTool tool,
        string? version)
    {
        List<ManagedToolManifest> manifests =
            await ReadManifestAsync().ConfigureAwait(false);

        manifests.RemoveAll(
            candidate =>
                string.Equals(
                    candidate.Tool,
                    tool.ToString(),
                    StringComparison.OrdinalIgnoreCase));

        manifests.Add(
            new ManagedToolManifest(
                tool.ToString(),
                version ?? "unknown",
                ExternalToolCatalog.AssetFileName(tool),
                DateTimeOffset.UtcNow));

        string path = ManifestPath();
        string json = JsonSerializer.Serialize(
            manifests,
            new JsonSerializerOptions { WriteIndented = true });

        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, json)
            .ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    public async Task<List<ManagedToolManifest>> ReadManifestAsync()
    {
        string path = ManifestPath();

        if (!File.Exists(path))
        {
            return new List<ManagedToolManifest>();
        }

        try
        {
            string json = await File.ReadAllTextAsync(path)
                .ConfigureAwait(false);

            return JsonSerializer.Deserialize<List<ManagedToolManifest>>(json)
                ?? new List<ManagedToolManifest>();
        }
        catch
        {
            return new List<ManagedToolManifest>();
        }
    }

    private string ManifestPath()
    {
        return Path.Combine(_toolsDirectory, ManifestFileName);
    }

    private static async Task<string?> ProbeVersionAsync(
        string binaryPath,
        string versionArgument)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add(versionArgument);

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync()
                .ConfigureAwait(false);

            await process.WaitForExitAsync().ConfigureAwait(false);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
