using System.Text.Json;

namespace DeezNutzDownloader.Core.Updates;

public sealed record ReleaseAsset(
    string Name,
    string DownloadUrl,
    long SizeBytes);

public sealed record GitHubReleaseInfo(
    string TagName,
    string Name,
    string Body,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record UpdateAvailable(
    string Version,
    string Notes,
    ReleaseAsset Asset);

/// <summary>
/// Pure GitHub-release update logic: parsing, version comparison,
/// and portable-asset picking. No network here — tests pin the
/// exact behavior without touching api.github.com.
/// Repository is "owner/name" from Settings; empty means off.
/// </summary>
public static class UpdateCheck
{
    public static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        string[] parts = repository.Trim().Split('/');

        return
            parts.Length == 2 &&
            parts.All(part =>
                part.Length is >= 1 and <= 100 &&
                part.All(character =>
                    char.IsLetterOrDigit(character) ||
                    character is '-' or '_' or '.'));
    }

    public static string LatestReleaseUrl(string repository)
    {
        return $"https://api.github.com/repos/{repository.Trim()}/releases/latest";
    }

    /// <returns>Null when the payload is not a usable release.</returns>
    public static GitHubReleaseInfo? ParseRelease(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string? tag = GetString(root, "tag_name");

            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            bool draft = GetBool(root, "draft");
            bool prerelease = GetBool(root, "prerelease");

            var assets = new List<ReleaseAsset>();

            if (
                root.TryGetProperty("assets", out JsonElement list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string? name = GetString(item, "name");
                    string? url = GetString(
                        item,
                        "browser_download_url");

                    if (
                        string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(url) ||
                        !Uri.TryCreate(
                            url,
                            UriKind.Absolute,
                            out _))
                    {
                        continue;
                    }

                    long size = 0;

                    if (
                        item.TryGetProperty("size", out JsonElement sizeElement) &&
                        sizeElement.ValueKind == JsonValueKind.Number)
                    {
                        sizeElement.TryGetInt64(out size);
                    }

                    assets.Add(new ReleaseAsset(name, url, size));
                }
            }

            return new GitHubReleaseInfo(
                tag.Trim(),
                GetString(root, "name") ?? tag.Trim(),
                GetString(root, "body") ?? string.Empty,
                draft,
                prerelease,
                assets.AsReadOnly());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// "v1.2.3" and "1.2.3" compare equal. Returns null when the
    /// tag carries no parseable version.
    /// </summary>
    public static Version? TryParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string cleaned = tag.Trim().TrimStart('v', 'V');

        // Strip pre-release suffixes ("1.2.3-beta") for comparison.
        int dash = cleaned.IndexOf('-');

        if (dash >= 0)
        {
            cleaned = cleaned[..dash];
        }

        // Strip build metadata ("1.2.3+5").
        int plus = cleaned.IndexOf('+');

        if (plus >= 0)
        {
            cleaned = cleaned[..plus];
        }

        // System.Version needs 2-4 components.
        string[] parts = cleaned.Split('.');

        if (parts.Length is < 2 or > 4)
        {
            return null;
        }

        if (!parts.All(part =>
            int.TryParse(part, out _) && part.Length <= 5))
        {
            return null;
        }

        return Version.TryParse(cleaned, out Version? version)
            ? version
            : null;
    }

    public static bool IsNewer(string currentTag, string latestTag)
    {
        Version? current = TryParseVersion(currentTag);
        Version? latest = TryParseVersion(latestTag);

        if (current is null || latest is null)
        {
            return false;
        }

        return latest > current;
    }

    /// <summary>
    /// Prefers a *portable*.zip asset, falls back to any .zip.
    /// Null when the release carries no zip at all.
    /// </summary>
    public static ReleaseAsset? PickPortableAsset(
        GitHubReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);

        ReleaseAsset? portable = release.Assets.FirstOrDefault(
            asset =>
                asset.Name.EndsWith(
                    ".zip",
                    StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains(
                    "portable",
                    StringComparison.OrdinalIgnoreCase));

        if (portable is not null)
        {
            return portable;
        }

        return release.Assets.FirstOrDefault(
            asset =>
                asset.Name.EndsWith(
                    ".zip",
                    StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides what the UI should do. Drafts, prereleases, and
    /// unparseable tags are skipped silently — never nag.
    /// </summary>
    public static UpdateAvailable? Decide(
        string currentTag,
        GitHubReleaseInfo? release)
    {
        if (
            release is null ||
            release.Draft ||
            release.Prerelease)
        {
            return null;
        }

        if (!IsNewer(currentTag, release.TagName))
        {
            return null;
        }

        ReleaseAsset? asset = PickPortableAsset(release);

        if (asset is null)
        {
            return null;
        }

        Version? version = TryParseVersion(release.TagName);

        return new UpdateAvailable(
            version?.ToString() ?? release.TagName,
            release.Body,
            asset);
    }

    /// <summary>
    /// Builds the apply-update batch script. Pure so tests pin the
    /// exact wait-copy-restart sequence without running it.
    /// </summary>
    public static string BuildInstallerScript(
        int processId,
        string exePath,
        string pendingDir,
        string installDir)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);

        return
            "@echo off\r\n" +
            $"rem Waits for PID {processId} to exit, copies the staged update over, restarts.\r\n" +
            ":waitloop\r\n" +
            $"tasklist /FI \"PID eq {processId}\" 2>nul | find \"{processId}\" >nul\r\n" +
            "if %ERRORLEVEL%==0 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto waitloop\r\n" +
            ")\r\n" +
            $"xcopy \"{pendingDir}\\*\" \"{installDir}\" /E /I /Y\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "(goto) 2>nul & del \"%~f0\"\r\n";
    }

    private static string? GetString(JsonElement element, string name)
    {
        return
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind is JsonValueKind.True;
    }
}
