using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeezNutzDownloader.Adapters.Media;

public sealed record MediaFormatOption(
    string FormatId,
    string Label,
    string Extension,
    int? Width,
    int? Height,
    long? FileSize,
    bool IsAudioOnly,
    double? Fps);

public sealed record MediaProbeResult(
    string SourceUrl,
    string Title,
    string? Uploader,
    double? DurationSeconds,
    bool IsPlaylist,
    int? PlaylistCount,
    IReadOnlyList<MediaFormatOption> Formats)
{
    public string Summary =>
        IsPlaylist && PlaylistCount.HasValue
            ? $"{Title} · playlist ({PlaylistCount} items)"
            : Formats.Count == 0
                ? Title
                : $"{Title} · {Formats.Count} formats";
}

public interface IYtDlpMediaService
{
    string? ResolveExecutable(string configuredPath);

    Task<bool> IsAvailableAsync(
        string configuredPath,
        CancellationToken cancellationToken = default);

    Task<MediaProbeResult> ProbeAsync(
        string url,
        string configuredPath,
        CancellationToken cancellationToken = default,
        string? proxyUrl = null);

    /// <returns>The final media file path.</returns>
    Task<string> DownloadAsync(
        string url,
        string? formatId,
        string destinationDirectory,
        string ytDlpPath,
        string ffmpegPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default,
        string? proxyUrl = null);
}

public static class YtDlpProbeParser
{
    private const int MaximumFormats = 30;

    public static MediaProbeResult ParseProbeJson(
        string json,
        string sourceUrl)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (
            root.TryGetProperty("_type", out JsonElement type) &&
            type.GetString() == "playlist"
        )
        {
            string playlistTitle =
                GetString(root, "title") ?? sourceUrl;

            int? count = GetInt(root, "playlist_count");

            if (!count.HasValue &&
                root.TryGetProperty("entries", out JsonElement entries) &&
                entries.ValueKind == JsonValueKind.Array)
            {
                count = entries.GetArrayLength();
            }

            return new MediaProbeResult(
                sourceUrl,
                playlistTitle,
                GetString(root, "uploader"),
                null,
                IsPlaylist: true,
                PlaylistCount: count,
                Formats: Array.Empty<MediaFormatOption>());
        }

        string title =
            GetString(root, "title") ??
            GetString(root, "id") ??
            sourceUrl;

        var formats = new List<MediaFormatOption>();

        if (
            root.TryGetProperty("formats", out JsonElement formatsElement) &&
            formatsElement.ValueKind == JsonValueKind.Array
        )
        {
            foreach (JsonElement format in formatsElement.EnumerateArray())
            {
                MediaFormatOption? option = ParseFormat(format);

                if (option is not null)
                {
                    formats.Add(option);
                }
            }
        }

        formats.Sort(CompareFormats);

        return new MediaProbeResult(
            sourceUrl,
            title,
            GetString(root, "uploader"),
            GetDouble(root, "duration"),
            IsPlaylist: false,
            PlaylistCount: null,
            Formats: formats
                .Take(MaximumFormats)
                .ToList()
                .AsReadOnly());
    }

    private static MediaFormatOption? ParseFormat(JsonElement format)
    {
        if (
            !format.TryGetProperty("format_id", out JsonElement id) ||
            string.IsNullOrWhiteSpace(id.GetString())
        )
        {
            return null;
        }

        string formatId = id.GetString()!;
        string ext = GetString(format, "ext") ?? "mp4";
        int? width = GetInt(format, "width");
        int? height = GetInt(format, "height");
        double? fps = GetDouble(format, "fps");

        long? size =
            GetLong(format, "filesize") ??
            GetLong(format, "filesize_approx");

        string? vcodec = GetString(format, "vcodec");
        bool isAudioOnly =
            string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);

        string label = BuildLabel(
            height, width, ext, fps, size, isAudioOnly, format);

        return new MediaFormatOption(
            formatId,
            label,
            ext,
            width,
            height,
            size,
            isAudioOnly,
            fps);
    }

    private static string BuildLabel(
        int? height,
        int? width,
        string ext,
        double? fps,
        long? size,
        bool isAudioOnly,
        JsonElement format)
    {
        var builder = new StringBuilder();

        if (isAudioOnly)
        {
            builder.Append("audio only");
        }
        else if (height.HasValue)
        {
            builder.Append(height.Value).Append('p');

            if (fps is > 48)
            {
                builder.Append((int)Math.Round(fps.Value)).Append("fps");
            }
        }
        else
        {
            builder.Append(
                GetString(format, "resolution") ?? "video");
        }

        builder.Append(' ').Append(ext);

        if (size.HasValue)
        {
            builder.Append(" · ").Append(FormatBytes(size.Value));
        }

        string? note = GetString(format, "format_note");

        if (
            !string.IsNullOrWhiteSpace(note) &&
            !note.Contains("storyboard", StringComparison.OrdinalIgnoreCase)
        )
        {
            builder.Append(" (").Append(note.Trim()).Append(')');
        }

        _ = width;

        return builder.ToString();
    }

    private static int CompareFormats(
        MediaFormatOption left,
        MediaFormatOption right)
    {
        // Video first (tallest first), then audio-only.
        int audioOrder =
            left.IsAudioOnly.CompareTo(right.IsAudioOnly);

        if (audioOrder != 0)
        {
            return audioOrder;
        }

        int heightOrder =
            (right.Height ?? 0).CompareTo(left.Height ?? 0);

        if (heightOrder != 0)
        {
            return heightOrder;
        }

        return (right.FileSize ?? 0).CompareTo(left.FileSize ?? 0);
    }

    private static string FormatBytes(long bytes)
    {
        const double oneKb = 1024d;
        const double oneMb = oneKb * 1024d;
        const double oneGb = oneMb * 1024d;

        return bytes switch
        {
            >= (long)oneGb => $"{bytes / oneGb:0.0} GB",
            >= (long)oneMb => $"{bytes / oneMb:0.0} MB",
            >= (long)oneKb => $"{bytes / oneKb:0.0} KB",
            _ => $"{bytes} B",
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        return
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int parsed)
        )
        {
            return parsed;
        }

        return null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long parsed)
        )
        {
            return parsed;
        }

        return null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double parsed)
        )
        {
            return parsed;
        }

        return null;
    }
}

/// <summary>
/// Builds yt-dlp command lines. Public and pure so tests can
/// verify flags (like --proxy) without a yt-dlp binary.
/// </summary>
public static class YtDlpArgumentBuilder
{
    public static List<string> BuildProbe(
        string url,
        string? proxyUrl = null)
    {
        var arguments = new List<string>
        {
            "--dump-single-json",
            "--no-playlist",
            "--no-warnings",
            "--socket-timeout", "15",
        };

        AddProxy(arguments, proxyUrl);
        arguments.Add(url);
        return arguments;
    }

    public static List<string> BuildDownload(
        string url,
        string? formatId,
        string outputTemplate,
        string ffmpegPath,
        string? proxyUrl = null)
    {
        var arguments = new List<string>
        {
            "--no-playlist",
            "--no-warnings",
            "--newline",
            "--print", "after_move:filepath",
            "--format", string.IsNullOrWhiteSpace(formatId)
                ? "bv*+ba/b"
                : formatId,
            "--output", outputTemplate,
            "--socket-timeout", "30",
            "--retries", "3",
        };

        string ffmpeg = ffmpegPath?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(ffmpeg))
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(ffmpeg);
        }

        AddProxy(arguments, proxyUrl);
        arguments.Add(url);
        return arguments;
    }

    private static void AddProxy(
        List<string> arguments,
        string? proxyUrl)
    {
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            arguments.Add("--proxy");
            arguments.Add(proxyUrl.Trim());
        }
    }
}

public sealed class YtDlpMediaService : IYtDlpMediaService
{
    private const int DefaultTimeoutSeconds = 120;

    public string? ResolveExecutable(string configuredPath)
    {
        string trimmed = configuredPath?.Trim() ?? string.Empty;

        if (
            !string.IsNullOrEmpty(trimmed) &&
            File.Exists(trimmed)
        )
        {
            return trimmed;
        }

        if (
            !string.IsNullOrEmpty(trimmed) &&
            !trimmed.Contains(
                Path.DirectorySeparatorChar) &&
            !trimmed.Contains(Path.AltDirectorySeparatorChar)
        )
        {
            // A bare name: let PATH resolution decide at launch.
            return trimmed;
        }

        // Fall back to PATH lookup.
        return "yt-dlp";
    }

    public async Task<bool> IsAvailableAsync(
        string configuredPath,
        CancellationToken cancellationToken = default)
    {
        string? executable = ResolveExecutable(configuredPath);

        if (
            executable is null ||
            (executable != "yt-dlp" && !File.Exists(executable))
        )
        {
            return false;
        }

        try
        {
            ProcessResult result = await RunAsync(
                    executable,
                    new[] { "--version" },
                    workLog: null,
                    timeout: TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);

            return
                result.ExitCode == 0 &&
                !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch
        {
            return false;
        }
    }

    public async Task<MediaProbeResult> ProbeAsync(
        string url,
        string configuredPath,
        CancellationToken cancellationToken = default,
        string? proxyUrl = null)
    {
        Uri source = ValidateUrl(url);
        string? executable = ResolveExecutable(configuredPath);

        if (executable is null)
        {
            throw new InvalidOperationException(
                "yt-dlp was not found. Set its path in Settings " +
                "or install it so it is available on PATH.");
        }

        ProcessResult result;
        try
        {
            result = await RunAsync(
                    executable,
                    YtDlpArgumentBuilder.BuildProbe(
                        source.AbsoluteUri,
                        proxyUrl),
                    workLog: null,
                    timeout: TimeSpan.FromSeconds(60),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "yt-dlp could not be started. Check the path in " +
                $"Settings ({executable}). {exception.Message}",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "yt-dlp could not read that page. " +
                "It may be private, login-walled, or " +
                "DRM-protected (those are not supported). " +
                TrimTail(result.StandardError));
        }

        try
        {
            return YtDlpProbeParser.ParseProbeJson(
                result.StandardOutput,
                source.AbsoluteUri);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "yt-dlp returned output this app could not " +
                "understand. Try updating yt-dlp.",
                exception);
        }
    }

    public async Task<string> DownloadAsync(
        string url,
        string? formatId,
        string destinationDirectory,
        string ytDlpPath,
        string ffmpegPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default,
        string? proxyUrl = null)
    {
        Uri source = ValidateUrl(url);

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException(
                "A destination directory is required.",
                nameof(destinationDirectory));
        }

        string fullDestination =
            Path.GetFullPath(destinationDirectory);

        Directory.CreateDirectory(fullDestination);

        string? executable = ResolveExecutable(ytDlpPath);

        if (executable is null)
        {
            throw new InvalidOperationException(
                "yt-dlp was not found. Set its path in Settings " +
                "or install it so it is available on PATH.");
        }

        string template = Path.Combine(
            fullDestination,
            "%(title)s [%(id)s].%(ext)s");

        List<string> arguments =
            YtDlpArgumentBuilder.BuildDownload(
                source.AbsoluteUri,
                formatId,
                template,
                ffmpegPath,
                proxyUrl);

        ProcessResult result;
        try
        {
            result = await RunAsync(
                    executable,
                    arguments,
                    log,
                    timeout: TimeSpan.FromSeconds(
                        DefaultTimeoutSeconds * 30),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "yt-dlp could not be started. Check the path in " +
                $"Settings ({executable}). {exception.Message}",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "yt-dlp could not download that video. " +
                "It may need Premium/login, be geo-blocked, or " +
                "be DRM-protected (not supported). " +
                TrimTail(result.StandardError));
        }

        string? finalPath = result.StandardOutput
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('"'))
            .LastOrDefault(
                line =>
                    !string.IsNullOrWhiteSpace(line) &&
                    Path.IsPathRooted(line) &&
                    File.Exists(line));

        if (finalPath is null)
        {
            throw new InvalidOperationException(
                "yt-dlp finished but the media file could not be " +
                "located. Check the download folder. " +
                TrimTail(result.StandardError));
        }

        return finalPath;
    }

    private static Uri ValidateUrl(string url)
    {
        if (
            !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new ArgumentException(
                "Enter a single http(s) page or media address.",
                nameof(url));
        }

        return uri;
    }

    private static string TrimTail(string text)
    {
        const int maximumLength = 400;

        string trimmed = (text ?? string.Empty).Trim();

        if (trimmed.Length <= maximumLength)
        {
            return trimmed;
        }

        return "Details: " + trimmed[^maximumLength..];
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IProgress<string>? workLog,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            stdout.AppendLine(args.Data);
            workLog?.Report(args.Data);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            stderr.AppendLine(args.Data);
            workLog?.Report(args.Data);
        };

        bool started;
        try
        {
            started = process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Let callers translate this into a friendly message.
            throw;
        }

        if (!started)
        {
            throw new InvalidOperationException(
                "yt-dlp could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource =
            new CancellationTokenSource(timeout);

        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            throw new TimeoutException(
                "yt-dlp took too long and was stopped.");
        }

        return new ProcessResult(
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort; the original cancellation wins.
        }
    }
}
