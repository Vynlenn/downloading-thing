using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using DeezNutzDownloader.Core.Updates;

namespace DeezNutzDownloader.App.Updates;

/// <summary>
/// GitHub-releases updater for the portable zip. Flow: check the
/// latest release, download its portable asset to temp, extract to
/// a pending folder, then hand off to a generated batch script
/// that waits for this process to exit, copies files over, and
/// restarts the app. The running exe can never overwrite itself,
/// hence the staging.
/// </summary>
public sealed class AppUpdater
{
    private readonly HttpClient _httpClient;
    private readonly string _repository;

    public AppUpdater(HttpClient httpClient, string repository)
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));

        if (!UpdateCheck.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "A GitHub repository like owner/name is required.",
                nameof(repository));
        }

        _repository = repository.Trim();
    }

    public static string CurrentVersion()
    {
        string? informational =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

        // "1.2.3+commit" -> "1.2.3".
        int plus = informational?.IndexOf('+') ?? -1;

        if (plus >= 0)
        {
            informational = informational![..plus];
        }

        return string.IsNullOrWhiteSpace(informational)
            ? "0.0.0"
            : informational.Trim();
    }

    public async Task<UpdateAvailable?> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            UpdateCheck.LatestReleaseUrl(_repository));

        request.Headers.UserAgent.ParseAdd("DeezNutzDownloader");
        request.Headers.Accept.ParseAdd(
            "application/vnd.github+json");

        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"No releases found for {_repository}. Publish a " +
                "release with a portable zip asset first.");
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(
                cancellationToken)
            .ConfigureAwait(false);

        GitHubReleaseInfo? release = UpdateCheck.ParseRelease(json);

        if (release is null)
        {
            throw new InvalidOperationException(
                "The release answer could not be understood. " +
                "Try again later.");
        }

        return UpdateCheck.Decide(CurrentVersion(), release);
    }

    /// <returns>The staged installer script path. Run it and exit.</returns>
    public async Task<string> StageAsync(
        UpdateAvailable update,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        string installDir = AppContext.BaseDirectory;
        string exePath = Environment.ProcessPath ??
            Path.Combine(
                installDir,
                "DeezNutzDownloader.App.exe");

        string stageRoot = Path.Combine(
            Path.GetTempPath(),
            "DeezNutzDownloader-update",
            Guid.NewGuid().ToString("N"));

        string zipPath = Path.Combine(stageRoot, "update.zip");
        string pendingDir = Path.Combine(stageRoot, "pending");

        Directory.CreateDirectory(pendingDir);

        log?.Report(
            $"Downloading update {update.Version}…");

        await DownloadFileAsync(
                update.Asset,
                zipPath,
                cancellationToken)
            .ConfigureAwait(false);

        log?.Report("Unpacking update…");

        ZipFile.ExtractToDirectory(zipPath, pendingDir, overwriteFiles: true);
        TryDelete(zipPath);

        // The zip may nest everything one level deep
        // (DeezNutzDownloader/...). Unwrap a lone wrapper folder.
        pendingDir = UnwrapSingleFolder(pendingDir);

        string scriptPath = Path.Combine(stageRoot, "apply-update.cmd");

        await File.WriteAllTextAsync(
                scriptPath,
                UpdateCheck.BuildInstallerScript(
                    Environment.ProcessId,
                    exePath,
                    pendingDir,
                    installDir),
                cancellationToken)
            .ConfigureAwait(false);

        return scriptPath;
    }

    public static void LaunchInstallerAndExit(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process.Start(startInfo);
    }

    private async Task DownloadFileAsync(
        ReleaseAsset asset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!UpdateCheck.IsTrustedAssetUrl(asset.DownloadUrl))
        {
            throw new InvalidOperationException(
                "The update download is not an HTTPS GitHub URL; " +
                "it was refused.");
        }

        using HttpResponseMessage response =
            await _httpClient.GetAsync(
                    asset.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Redirects (github.com -> *.githubusercontent.com) must
        // land on a trusted HTTPS host too.
        if (
            response.RequestMessage?.RequestUri is not Uri finalUri ||
            !UpdateCheck.IsTrustedAssetUri(finalUri))
        {
            throw new InvalidOperationException(
                "The update download was redirected off GitHub; " +
                "it was refused.");
        }

        using var sha256 = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        long received = 0;

        await using (
            Stream source =
                await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false))
        await using (
            var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[81_920];

            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);

                sha256.AppendData(buffer, 0, read);
                received += read;
            }
        }

        if (asset.SizeBytes > 0 && received != asset.SizeBytes)
        {
            TryDelete(destinationPath);

            throw new InvalidOperationException(
                $"The update download was {received:N0} bytes but " +
                $"GitHub lists {asset.SizeBytes:N0}; it was discarded.");
        }

        if (asset.Sha256 is not null)
        {
            string actual = Convert.ToHexString(sha256.GetHashAndReset());

            if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destinationPath);

                throw new InvalidOperationException(
                    "The update failed its SHA-256 check against the " +
                    "digest GitHub published; it was discarded.");
            }
        }
    }

    private static string UnwrapSingleFolder(string pendingDir)
    {
        string[] entries = Directory.GetFileSystemEntries(pendingDir);

        if (
            entries.Length == 1 &&
            Directory.Exists(entries[0]))
        {
            return entries[0];
        }

        return pendingDir;
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


