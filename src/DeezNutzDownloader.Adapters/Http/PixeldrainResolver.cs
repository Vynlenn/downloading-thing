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
public sealed class PixeldrainResolver : IHostFileResolver
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
            info.SizeBytes);
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
    long? SizeBytes);

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

            return new PixeldrainFileInfo(fileId, name, size);
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
