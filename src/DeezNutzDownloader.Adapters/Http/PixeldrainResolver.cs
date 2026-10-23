using System.Text.Json;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Resolves Pixeldrain file pages (/u/ID) through the public API:
/// info first (real name + size), then the range-capable download
/// endpoint. API captcha errors (hotlink/file-rate-limit) surface
/// as solvable challenges opening the /u/ page; quota errors stay
/// throttled. Per https://pixeldrain.com/api.
/// </summary>
public sealed class PixeldrainResolver : IHostFileResolver, IHostFolderResolver
{
    public bool CanResolve(Uri page)
    {
        return
            page is not null &&
            IsPixeldrainHost(page.Host) &&
            TryParseFileId(page) is not null;
    }

    public async Task<ResolvedHostFile> ResolveAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(httpClient);

        string? fileId = TryParseFileId(page);

        if (fileId is null)
        {
            throw new ArgumentException(
                "Not a Pixeldrain file address.",
                nameof(page));
        }

        string infoJson;

        try
        {
            using HttpResponseMessage infoResponse =
                await httpClient.GetAsync(
                        BuildInfoUrl(fileId),
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (
                infoResponse.StatusCode ==
                    System.Net.HttpStatusCode.Forbidden ||
                infoResponse.StatusCode ==
                    System.Net.HttpStatusCode.TooManyRequests)
            {
                throw PixeldrainErrors.ToException(
                    fileId,
                    code: string.Empty,
                    message: string.Empty);
            }

            infoResponse.EnsureSuccessStatusCode();

            infoJson = await infoResponse.Content.ReadAsStringAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Pixeldrain could not be reached for file {fileId}. " +
                "Check the connection and try again.",
                exception);
        }

        PixeldrainFileInfo info =
            PixeldrainInfoParser.Parse(infoJson, fileId);

        return new ResolvedHostFile(
            BuildDownloadUrl(fileId),
            info.Name,
            info.SizeBytes,
            info.Sha256Hex);
    }

    public static string? TryParseFileId(Uri page)
    {
        if (page is null || !IsPixeldrainHost(page.Host))
        {
            return null;
        }

        // /u/{id} page, /api/file/{id}[/info] endpoint. Query and
        // fragment never carry the id.
        string[] segments = page.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 2 && segments[0] == "u")
        {
            return NormalizeId(segments[1]);
        }

        if (
            segments.Length >= 3 &&
            segments[0] == "api" &&
            segments[1] == "file")
        {
            return NormalizeId(segments[2]);
        }

        return null;
    }

    public static string BuildInfoUrl(string fileId)
    {
        return $"https://pixeldrain.com/api/file/{fileId}/info";
    }

    public static Uri BuildDownloadUrl(string fileId)
    {
        return new Uri(
            $"https://pixeldrain.com/api/file/{fileId}?download",
            UriKind.Absolute);
    }

    public static string PageUrl(string fileId)
    {
        return $"https://pixeldrain.com/u/{fileId}";
    }

    public static string ListPageUrl(string listId)
    {
        return $"https://pixeldrain.com/l/{listId}";
    }

    public bool CanResolveFolder(Uri page)
    {
        return
            page is not null &&
            IsPixeldrainHost(page.Host) &&
            TryParseListId(page) is not null;
    }

    /// <summary>
    /// Resolves a /l/ list through /api/list/{id}: every file
    /// becomes a ranged download with its real name, size, and
    /// SHA-256 (per https://pixeldrain.com/api).
    /// </summary>
    public async Task<ResolvedHostFolder> ResolveFolderAsync(
        Uri page,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(httpClient);

        string? listId = TryParseListId(page);

        if (listId is null)
        {
            throw new ArgumentException(
                "Not a Pixeldrain list address.",
                nameof(page));
        }

        string listJson;

        try
        {
            using HttpResponseMessage response =
                await httpClient.GetAsync(
                        $"https://pixeldrain.com/api/list/{listId}",
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            listJson = await response.Content.ReadAsStringAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Pixeldrain list {listId} could not be reached. " +
                "Check the connection and try again.",
                exception);
        }

        PixeldrainList list =
            PixeldrainListParser.Parse(listJson, listId);

        if (list.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Pixeldrain list {listId} contains no files. " +
                "It may be empty or removed.");
        }

        string folderName = string.IsNullOrWhiteSpace(list.Title)
            ? $"pixeldrain-{listId}"
            : list.Title;

        return new ResolvedHostFolder(
            folderName,
            list.Files);
    }

    public static string? TryParseListId(Uri page)
    {
        if (page is null || !IsPixeldrainHost(page.Host))
        {
            return null;
        }

        string[] segments = page.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /l/{id} list page.
        if (segments.Length == 2 && segments[0] == "l")
        {
            return NormalizeId(segments[1]);
        }

        return null;
    }

    private static bool IsPixeldrainHost(string host)
    {
        return
            host.Equals("pixeldrain.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("pixeldrain.nl", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".pixeldrain.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".pixeldrain.nl", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string id = raw.Trim();

        // IDs are short alphanumerics (plus - and _). Anything else
        // is a path, not a file.
        if (
            id.Length is < 4 or > 64 ||
            !id.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_'))
        {
            return null;
        }

        return id;
    }
}

public sealed record PixeldrainFileInfo(
    string FileId,
    string? Name,
    long? SizeBytes,
    string? Sha256Hex = null);

public sealed record PixeldrainList(
    string ListId,
    string? Title,
    IReadOnlyList<ResolvedHostFile> Files);

/// <summary>
/// Pure parsing of /api/list/{id} bodies (title + files[] with
/// id/name/size per the API docs).
/// </summary>
public static class PixeldrainListParser
{
    public static PixeldrainList Parse(
        string json,
        string listId)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Pixeldrain returned an unreadable list {listId}.",
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            bool success =
                root.TryGetProperty("success", out JsonElement ok) &&
                ok.ValueKind == JsonValueKind.True;

            if (!success)
            {
                throw PixeldrainErrors.ToException(
                    listId,
                    PixeldrainErrors.ReadCode(root),
                    PixeldrainErrors.ReadMessage(root));
            }

            string? title =
                root.TryGetProperty("title", out JsonElement titleElement) &&
                titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()
                    : null;

            var files = new List<ResolvedHostFile>();

            if (
                root.TryGetProperty("files", out JsonElement entries) &&
                entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    string? id =
                        entry.TryGetProperty("id", out JsonElement idElement) &&
                        idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString()
                            : null;

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    string? name =
                        entry.TryGetProperty("name", out JsonElement nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString()
                            : null;

                    long? size =
                        entry.TryGetProperty("size", out JsonElement sizeElement) &&
                        sizeElement.ValueKind == JsonValueKind.Number &&
                        sizeElement.TryGetInt64(out long parsed)
                            ? parsed
                            : null;

                    files.Add(
                        new ResolvedHostFile(
                            PixeldrainResolver.BuildDownloadUrl(id),
                            name,
                            size));
                }
            }

            return new PixeldrainList(
                listId,
                title,
                files.AsReadOnly());
        }
    }
}

/// <summary>
/// Pure parsing of /api/file/{id}/info bodies plus the documented
/// error values (captcha_required / hotlink_detected /
/// ip_download_limited_captcha_required versus quota errors).
/// </summary>
public static class PixeldrainInfoParser
{
    public static PixeldrainFileInfo Parse(
        string json,
        string fileId)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Pixeldrain returned an unreadable answer for file {fileId}.",
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            bool success =
                root.TryGetProperty("success", out JsonElement ok) &&
                ok.ValueKind == JsonValueKind.True;

            if (!success)
            {
                throw PixeldrainErrors.ToException(
                    fileId,
                    PixeldrainErrors.ReadCode(root),
                    PixeldrainErrors.ReadMessage(root));
            }

            string? name = root.TryGetProperty("name", out JsonElement nameElement) &&
                nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

            long? size = root.TryGetProperty("size", out JsonElement sizeElement) &&
                sizeElement.ValueKind == JsonValueKind.Number &&
                sizeElement.TryGetInt64(out long parsed)
                    ? parsed
                    : null;

            // Documented SHA-256 hex (per https://pixeldrain.com/api).
            // Absent on older files — verification is skipped then.
            string? sha256 =
                root.TryGetProperty("hash_sha256", out JsonElement hashElement) &&
                hashElement.ValueKind == JsonValueKind.String
                    ? hashElement.GetString()
                    : null;

            if (
                !string.IsNullOrWhiteSpace(sha256) &&
                (sha256.Length != 64 ||
                 !sha256.All(character =>
                    char.IsAsciiHexDigit(character))))
            {
                sha256 = null;
            }

            return new PixeldrainFileInfo(fileId, name, size, sha256);
        }
    }
}

internal static class PixeldrainErrors
{
    internal static string ReadCode(JsonElement root)
    {
        foreach (string property in new[] { "value", "error", "code" })
        {
            if (
                root.TryGetProperty(property, out JsonElement element) &&
                element.ValueKind == JsonValueKind.String)
            {
                string? value = element.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    internal static string ReadMessage(JsonElement root)
    {
        if (
            root.TryGetProperty("message", out JsonElement element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    internal static bool IsCaptchaWall(string code, string message)
    {
        string combined = (code + " " + message).ToLowerInvariant();

        return
            combined.Contains("captcha") ||
            combined.Contains("hotlink");
    }

    internal static Exception ToException(
        string fileId,
        string code,
        string message)
    {
        string pageUrl = PixeldrainResolver.PageUrl(fileId);

        if (IsCaptchaWall(code, message))
        {
            const string detail =
                "Pixeldrain asks for a CAPTCHA on this file " +
                "(hotlink protection).";

            return new ChallengeDetectedException(
                "pixeldrain.com",
                ChallengeKind.Captcha,
                pageUrl,
                $"{detail} Solve it once in the app browser and " +
                "the queue resumes.");
        }

        string reason = string.IsNullOrWhiteSpace(message)
            ? string.IsNullOrWhiteSpace(code)
                ? "the file is unavailable"
                : code
            : message.Trim();

        return new HostThrottledException(
            "pixeldrain.com",
            $"Pixeldrain refused file {fileId} ({reason}). " +
            "It may be over quota, removed, or limited — " +
            "the item stays queued for retry.",
            isQuotaExhausted: true);
    }
}
