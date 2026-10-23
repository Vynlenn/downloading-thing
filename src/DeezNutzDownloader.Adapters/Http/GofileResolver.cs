using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Resolves Gofile share links (/d/code) through the public API:
/// guest account first, then the content listing for a direct
/// file link. The API gates listings to Premium accounts, so a
/// gated answer surfaces as a plain message (Premium needed),
/// never as a challenge or a silent failure.
/// Per https://gofile.io/api.
/// </summary>
public sealed class GofileResolver : IHostFileResolver, IHostFolderResolver
{
    private const string AccountsUrl = "https://api.gofile.io/accounts";

    public bool CanResolve(Uri page)
    {
        return
            page is not null &&
            IsGofileHost(page.Host) &&
            TryParseContentCode(page) is not null;
    }

    public async Task<ResolvedHostFile> ResolveAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(httpClient);

        string? code = TryParseContentCode(page);

        if (code is null)
        {
            throw new ArgumentException(
                "Not a Gofile share address.",
                nameof(page));
        }

        string token = await CreateGuestTokenAsync(
                httpClient,
                cancellationToken)
            .ConfigureAwait(false);

        GofileContent content = await GetContentAsync(
                httpClient,
                code,
                token,
                cancellationToken)
            .ConfigureAwait(false);

        if (content.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Gofile share {code} contains no downloadable files. " +
                "It may be empty, expired, or password-protected.");
        }

        if (content.Files.Count > 1)
        {
            throw new FolderExpansionNeededException(
                $"Gofile share {code} is a folder with " +
                $"{content.Files.Count} files. Expand it first " +
                "(Start expands folders automatically).");
        }

        return content.Files[0];
    }

    public bool CanResolveFolder(Uri page)
    {
        return CanResolve(page);
    }

    /// <summary>
    /// Resolves the whole share: folder name plus every file
    /// (subfolders flattened, keeping their relative paths).
    /// </summary>
    public async Task<ResolvedHostFolder> ResolveFolderAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(httpClient);

        string? code = TryParseContentCode(page);

        if (code is null)
        {
            throw new ArgumentException(
                "Not a Gofile share address.",
                nameof(page));
        }

        string token = await CreateGuestTokenAsync(
                httpClient,
                cancellationToken)
            .ConfigureAwait(false);

        GofileContent content = await GetContentAsync(
                httpClient,
                code,
                token,
                cancellationToken)
            .ConfigureAwait(false);

        if (content.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Gofile share {code} contains no downloadable files. " +
                "It may be empty, expired, or password-protected.");
        }

        string folderName = string.IsNullOrWhiteSpace(content.FolderName)
            ? $"gofile-{code}"
            : content.FolderName;

        return new ResolvedHostFolder(
            folderName,
            FlattenFiles(content));
    }

    /// <summary>
    /// Flattens nested subfolders: each file keeps its relative
    /// path as a name prefix (sub/name.ext) so the queue can
    /// rebuild the tree under the share folder.
    /// </summary>
    internal static IReadOnlyList<ResolvedHostFile> FlattenFiles(
        GofileContent content)
    {
        var flat = new List<ResolvedHostFile>();

        foreach (GofileEntry entry in content.Entries)
        {
            CollectEntry(entry, prefix: string.Empty, flat);
        }

        return flat.AsReadOnly();
    }

    private static void CollectEntry(
        GofileEntry entry,
        string prefix,
        List<ResolvedHostFile> flat)
    {
        if (entry.File is not null)
        {
            string name = string.IsNullOrWhiteSpace(prefix)
                ? entry.File.SuggestedFileName ?? "download"
                : prefix + "/" +
                    (entry.File.SuggestedFileName ?? "download");

            flat.Add(entry.File with { SuggestedFileName = name });
            return;
        }

        string folderPrefix = string.IsNullOrWhiteSpace(prefix)
            ? entry.FolderName ?? "folder"
            : prefix + "/" + (entry.FolderName ?? "folder");

        foreach (GofileEntry child in entry.Children)
        {
            CollectEntry(child, folderPrefix, flat);
        }
    }

    public static string? TryParseContentCode(Uri page)
    {
        if (page is null || !IsGofileHost(page.Host))
        {
            return null;
        }

        string[] segments = page.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /d/{code} share page.
        if (segments.Length == 2 && segments[0] == "d")
        {
            return NormalizeCode(segments[1]);
        }

        return null;
    }

    private static async Task<string> CreateGuestTokenAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            AccountsUrl)
        {
            Content = new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json")
        };

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "Gofile could not be reached. Check the " +
                "connection and try again.",
                exception);
        }

        using (response)
        {
            if ((int)response.StatusCode == 429)
            {
                throw new HostThrottledException(
                    "gofile.io",
                    "Gofile is rate limiting (429). Wait a minute " +
                    "and retry — the item stays queued.");
            }

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            string? token = GofileResponses.ParseGuestToken(json);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Gofile did not issue a guest session. " +
                    "Try again shortly.");
            }

            return token;
        }
    }

    private async Task<GofileContent> GetContentAsync(
        HttpClient httpClient,
        string code,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.gofile.io/contents/{code}");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Gofile could not be reached for share {code}.",
                exception);
        }

        using (response)
        {
            if ((int)response.StatusCode == 429)
            {
                throw new HostThrottledException(
                    "gofile.io",
                    "Gofile is rate limiting (429). Wait a minute " +
                    "and retry — the item stays queued.");
            }

            // The API reports errors inside HTTP 200 bodies, so the
            // payload — not the status — decides.
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            return GofileResponses.ParseContent(json, code);
        }
    }

    private static bool IsGofileHost(string host)
    {
        return
            host.Equals("gofile.io", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("www.gofile.io", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string code = raw.Trim();

        if (
            code.Length is < 4 or > 64 ||
            !code.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_'))
        {
            return null;
        }

        return code;
    }
}

/// <summary>
/// Thrown when a folder share reaches the single-file path:
/// the UI expands folders before batches, so this only fires
/// when expansion was skipped.
/// </summary>
public sealed class FolderExpansionNeededException : Exception
{
    public FolderExpansionNeededException(string message)
        : base(message)
    {
    }
}

internal sealed record GofileEntry(
    ResolvedHostFile? File,
    string? FolderName,
    IReadOnlyList<GofileEntry> Children);

internal sealed record GofileContent(
    string? FolderName,
    IReadOnlyList<ResolvedHostFile> Files,
    IReadOnlyList<GofileEntry> Entries);

/// <summary>
/// Pure parsing of the Gofile response envelope (status/data).
/// </summary>
internal static class GofileResponses
{
    internal static string? ParseGuestToken(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json ?? string.Empty);

            JsonElement root = document.RootElement;

            if (
                root.TryGetProperty("data", out JsonElement data) &&
                data.TryGetProperty("token", out JsonElement token) &&
                token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static GofileContent ParseContent(string json, string code)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Gofile returned an unreadable answer for share {code}.",
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            string status = root.TryGetProperty("status", out JsonElement statusElement) &&
                statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;

            if (!status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                throw ToStatusException(status, root, code);
            }

            if (!root.TryGetProperty("data", out JsonElement data))
            {
                throw new InvalidOperationException(
                    $"Gofile share {code} returned no content. It may " +
                    "be expired or removed.");
            }

            string? folderName =
                data.TryGetProperty("name", out JsonElement nameElement) &&
                nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

            var files = new List<ResolvedHostFile>();
            var entries = new List<GofileEntry>();

            // Folders carry children keyed by UUID; single files
            // carry their own link.
            if (
                data.TryGetProperty("children", out JsonElement children) &&
                children.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty child in children.EnumerateObject())
                {
                    GofileEntry entry = ParseEntry(child.Value);

                    entries.Add(entry);
                    CollectFiles(entry, files);
                }
            }
            else
            {
                ResolvedHostFile? single = ParseFileEntry(data);

                if (single is not null)
                {
                    files.Add(single);
                    entries.Add(new GofileEntry(single, null, Array.Empty<GofileEntry>()));
                }
            }

            return new GofileContent(
                folderName,
                files.AsReadOnly(),
                entries.AsReadOnly());
        }
    }

    private static Exception ToStatusException(
        string status,
        JsonElement root,
        string code)
    {
        string detail = root.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.String
                ? data.GetString() ?? string.Empty
                : status;

        if (detail.Contains("Premium", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                $"Gofile share {code} needs a Premium account for " +
                "direct access (Gofile gates API listings to Premium). " +
                "Open it in a browser or use Premium — retrying " +
                "will not help.");
        }

        if (
            detail.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                $"Gofile share {code} was not found. It may be " +
                "expired, removed, or mistyped.");
        }

        string reason = string.IsNullOrWhiteSpace(detail)
            ? "an unknown error"
            : detail.Trim();

        return new InvalidOperationException(
            $"Gofile refused share {code} ({reason}).");
    }

    private static void CollectFiles(
        GofileEntry entry,
        List<ResolvedHostFile> files)
    {
        if (entry.File is not null)
        {
            files.Add(entry.File);
            return;
        }

        foreach (GofileEntry child in entry.Children)
        {
            CollectFiles(child, files);
        }
    }

    private static GofileEntry ParseEntry(JsonElement entry)
    {
        ResolvedHostFile? file = ParseFileEntry(entry);

        if (file is not null)
        {
            return new GofileEntry(
                file,
                null,
                Array.Empty<GofileEntry>());
        }

        string? folderName =
            entry.TryGetProperty("name", out JsonElement nameElement) &&
            nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

        var children = new List<GofileEntry>();

        if (
            entry.TryGetProperty("children", out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty child in nested.EnumerateObject())
            {
                children.Add(ParseEntry(child.Value));
            }
        }

        return new GofileEntry(
            null,
            folderName,
            children.AsReadOnly());
    }

    private static ResolvedHostFile? ParseFileEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? type = entry.TryGetProperty("type", out JsonElement typeElement) &&
            typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

        if (
            !string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? link = entry.TryGetProperty("link", out JsonElement linkElement) &&
            linkElement.ValueKind == JsonValueKind.String
                ? linkElement.GetString()
                : null;

        if (
            string.IsNullOrWhiteSpace(link) ||
            !Uri.TryCreate(link, UriKind.Absolute, out Uri? download) ||
            (download.Scheme != Uri.UriSchemeHttp &&
             download.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string? name = entry.TryGetProperty("name", out JsonElement nameElement) &&
            nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

        long? size = entry.TryGetProperty("size", out JsonElement sizeElement) &&
            sizeElement.ValueKind == JsonValueKind.Number &&
            sizeElement.TryGetInt64(out long parsed)
                ? parsed
                : null;

        return new ResolvedHostFile(download, name, size);
    }
}
