using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Adapters.Http;

public sealed class HttpDownloadTransferService
    : IDownloadTransferService
{
    private const int BufferSize = 81_920;
    private const int MaximumFileNameLength = 180;
    private const int MaximumPreservedExtensionLength = 32;

    private static readonly HashSet<string> ReservedWindowsNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IHostFileResolver> _resolvers;

    public HttpDownloadTransferService(
        HttpClient httpClient,
        IEnumerable<IHostFileResolver>? resolvers = null)
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));

        _resolvers = resolvers?.ToArray() ??
            Array.Empty<IHostFileResolver>();
    }

    public async Task<DownloadTransferResult> DownloadAsync(
        DownloadTransferRequest request,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri source = ValidateSource(request.Source);
        string? suggestedFileName = null;
        long? advertisedTotalBytes = null;
        bool preferSuggestedName = false;

        // Host pages (/u/ID) become direct downloads first.
        // Resolver failures are typed (challenge/quota/unreachable)
        // and propagate — the item stays queued for solve or retry.
        foreach (IHostFileResolver resolver in _resolvers)
        {
            if (!resolver.CanResolve(source))
            {
                continue;
            }

            ResolvedHostFile resolved =
                await resolver.ResolveAsync(
                        source,
                        _httpClient,
                        cancellationToken)
                    .ConfigureAwait(false);

            source = resolved.DownloadUri;
            suggestedFileName = resolved.SuggestedFileName;
            advertisedTotalBytes = resolved.TotalBytes;
            preferSuggestedName = true;
            break;
        }

        string destinationDirectory =
            ValidateDestinationDirectory(
                request.DestinationDirectory);

        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        (
            string temporaryPath,
            FileStream temporaryStream
        ) = CreateTemporaryFile(destinationDirectory);

        // The creation stream only reserves a unique name.
        temporaryStream.Dispose();

        // The temp file is kept across throttle retries so a
        // 429/503 wait can resume instead of restarting. It is
        // deleted only on terminal failure (see catch below).
        // A `continue` never triggers that cleanup.
        string? pendingTemporaryPath = temporaryPath;
        long bytesWritten = 0;
        long? totalBytes = advertisedTotalBytes;
        string? fileName = null;

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var httpRequest =
                    new HttpRequestMessage(HttpMethod.Get, source);

                if (bytesWritten > 0)
                {
                    httpRequest.Headers.Range =
                        new RangeHeaderValue(bytesWritten, null);
                }

                using HttpResponseMessage response =
                    await _httpClient.SendAsync(
                            httpRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (
                    HostThrottlePolicy.IsThrottleStatusCode(
                        response.StatusCode)
                )
                {
                    TimeSpan? retryAfter =
                        HostThrottlePolicy.TryParseRetryAfter(
                            response.Headers,
                            DateTimeOffset.UtcNow);

                    TimeSpan? wait =
                        HostThrottlePolicy.ComputeWaitBeforeRetry(
                            attempt,
                            retryAfter);

                    if (
                        !HostThrottlePolicy.ShouldRetryAttempt(
                            response.StatusCode,
                            attempt) ||
                        wait is null
                    )
                    {
                        throw new HostThrottledException(
                            source.Host,
                            HostThrottlePolicy.FriendlyMessage(
                                source.Host,
                                response.StatusCode,
                                retryAfter));
                    }

                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));

                    await Task.Delay(wait.Value, cancellationToken)
                        .ConfigureAwait(false);

                    continue;
                }

                if (
                    response.StatusCode ==
                        System.Net.HttpStatusCode.Forbidden)
                {
                    ChallengeKind? challenge =
                        await DetectChallengeAsync(
                                response,
                                source,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (challenge.HasValue)
                    {
                        throw BuildChallengeException(
                            source,
                            challenge.Value);
                    }

                    if (HostThrottlePolicy.IsSingleLaneHost(source.Host))
                    {
                        throw new HostThrottledException(
                            source.Host,
                            HostThrottlePolicy.FriendlyMessage(
                                source.Host,
                                response.StatusCode,
                                retryAfter: null,
                                isPixeldrainQuotaBody: true),
                            isQuotaExhausted: true);
                    }
                }

                if (
                    response.StatusCode ==
                        System.Net.HttpStatusCode
                            .RequestedRangeNotSatisfiable
                )
                {
                    // Our resume offset is stale (the file
                    // changed server-side). Restart once.
                    bytesWritten = 0;
                    totalBytes = null;
                    TruncateFile(temporaryPath);

                    if (attempt >= 4)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    continue;
                }

                response.EnsureSuccessStatusCode();

                HttpContent content =
                    response.Content ??
                    throw new HttpRequestException(
                        "The HTTP response did not contain a body.");

                bool isPartial =
                    response.StatusCode ==
                        System.Net.HttpStatusCode.PartialContent;

                if (isPartial && bytesWritten > 0)
                {
                    totalBytes ??=
                        DeriveTotalBytes(
                            content.Headers,
                            bytesWritten);
                }
                else
                {
                    if (bytesWritten > 0)
                    {
                        // The server ignored our Range header.
                        // Restart cleanly instead of splicing.
                        bytesWritten = 0;
                        TruncateFile(temporaryPath);
                    }

                    totalBytes = content.Headers.ContentLength;
                }

                if (fileName is null)
                {
                    Uri fileNameSource =
                        response.RequestMessage?.RequestUri is
                            { IsAbsoluteUri: true } responseUri
                            ? responseUri
                            : source;

                    fileName =
                        ResolveSafeFileName(
                            content.Headers,
                            fileNameSource,
                            suggestedFileName,
                            preferSuggestedName);
                }

                byte[] buffer =
                    ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
                    await using Stream sourceStream =
                        await content.ReadAsStreamAsync(
                                cancellationToken)
                            .ConfigureAwait(false);

                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));

                    await using (
                        var resumeStream = new FileStream(
                            temporaryPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.None,
                            BufferSize,
                            FileOptions.Asynchronous |
                                FileOptions.SequentialScan))
                    {
                        resumeStream.Seek(
                            bytesWritten,
                            SeekOrigin.Begin);

                        while (true)
                        {
                            int bytesRead =
                                await sourceStream.ReadAsync(
                                        buffer.AsMemory(
                                            0,
                                            BufferSize),
                                        cancellationToken)
                                    .ConfigureAwait(false);

                            if (bytesRead == 0)
                            {
                                break;
                            }

                            await resumeStream.WriteAsync(
                                    buffer.AsMemory(
                                        0,
                                        bytesRead),
                                    cancellationToken)
                                .ConfigureAwait(false);

                            checked
                            {
                                bytesWritten += bytesRead;
                            }

                            progress?.Report(
                                new DownloadTransferProgress(
                                    bytesWritten,
                                    totalBytes));
                        }

                        await resumeStream.FlushAsync(
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                    when (
                        exception is HttpRequestException or
                            IOException &&
                        attempt < 4)
                {
                    // Keep the partial file; the next attempt
                    // resumes via the Range header above.
                    progress?.Report(
                        new DownloadTransferProgress(
                            bytesWritten,
                            totalBytes));

                    TimeSpan? wait =
                        HostThrottlePolicy.ComputeWaitBeforeRetry(
                            attempt,
                            retryAfter: null);

                    if (wait.HasValue)
                    {
                        await Task.Delay(
                                wait.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                cancellationToken.ThrowIfCancellationRequested();

                string destinationPath =
                    MoveToAvailableDestination(
                        temporaryPath,
                        destinationDirectory,
                        fileName!);

                pendingTemporaryPath = null;

                return new DownloadTransferResult(
                    source,
                    destinationPath,
                    Path.GetFileName(destinationPath),
                    bytesWritten,
                    totalBytes);
            }
        }
        catch
        {
            if (pendingTemporaryPath is not null)
            {
                TryDeleteTemporaryFile(
                    pendingTemporaryPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Reads a bounded snippet of a 403 page and asks the pure
    /// detector what wall (if any) the host put up. Never throws:
    /// an unreadable body simply means "no detectable challenge".
    /// </summary>
    public static async Task<ChallengeKind?> DetectChallengeAsync(
        HttpResponseMessage response,
        Uri source,
        CancellationToken cancellationToken)
    {
        string snippet;

        try
        {
            snippet = await ErrorSnippetReader.ReadSnippetAsync(
                    response.Content,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return ChallengeDetector.Detect(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            snippet,
            source.Host);
    }

    public static Exception BuildChallengeException(
        Uri source,
        ChallengeKind kind)
    {
        if (kind == ChallengeKind.QuotaWall)
        {
            return new HostThrottledException(
                source.Host,
                HostThrottlePolicy.FriendlyMessage(
                    source.Host,
                    System.Net.HttpStatusCode.Forbidden,
                    retryAfter: null,
                    isPixeldrainQuotaBody: true),
                isQuotaExhausted: true);
        }

        return new ChallengeDetectedException(
            source.Host,
            kind,
            source.AbsoluteUri,
            ChallengeDetector.FriendlyMessage(
                source.Host,
                kind,
                source.AbsoluteUri));
    }

    private static Uri ValidateSource(Uri? source)
    {
        if (source is null)
        {
            throw new ArgumentException(
                "A source URL is required.",
                nameof(source));
        }

        if (!source.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The source URL must be absolute.",
                nameof(source));
        }

        bool isHttp = string.Equals(
            source.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);

        bool isHttps = string.Equals(
            source.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

        if (!isHttp && !isHttps)
        {
            throw new ArgumentException(
                "Only HTTP and HTTPS downloads are supported.",
                nameof(source));
        }

        if (string.IsNullOrWhiteSpace(source.Host))
        {
            throw new ArgumentException(
                "The source URL must contain a host.",
                nameof(source));
        }

        return source;
    }

    private static string ValidateDestinationDirectory(
        string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException(
                "A destination directory is required.",
                nameof(destinationDirectory));
        }

        try
        {
            return Path.GetFullPath(destinationDirectory);
        }
        catch (Exception exception)
            when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException
            )
        {
            throw new ArgumentException(
                "The destination directory is invalid.",
                nameof(destinationDirectory),
                exception);
        }
    }

    private static (
        string Path,
        FileStream Stream
    ) CreateTemporaryFile(string destinationDirectory)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string temporaryPath = Path.Combine(
                destinationDirectory,
                $".download-{Guid.NewGuid():N}.part");

            try
            {
                var stream = new FileStream(
                    temporaryPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = BufferSize,
                        Options =
                            FileOptions.Asynchronous |
                            FileOptions.SequentialScan
                    });

                return (temporaryPath, stream);
            }
            catch (IOException)
                when (
                    File.Exists(temporaryPath) ||
                    Directory.Exists(temporaryPath)
                )
            {
                // A GUID collision is extraordinarily unlikely,
                // but CreateNew keeps the operation race-safe.
            }
        }

        throw new IOException(
            "A unique temporary download file could not be created.");
    }

    private static string MoveToAvailableDestination(
        string temporaryPath,
        string destinationDirectory,
        string fileName)
    {
        for (
            int collisionIndex = 0;
            collisionIndex < int.MaxValue;
            collisionIndex++
        )
        {
            string candidateName =
                CreateCollisionFileName(
                    fileName,
                    collisionIndex);

            string candidatePath = Path.Combine(
                destinationDirectory,
                candidateName);

            if (
                File.Exists(candidatePath) ||
                Directory.Exists(candidatePath)
            )
            {
                continue;
            }

            try
            {
                File.Move(
                    temporaryPath,
                    candidatePath,
                    overwrite: false);

                return candidatePath;
            }
            catch (IOException)
                when (
                    File.Exists(candidatePath) ||
                    Directory.Exists(candidatePath)
                )
            {
                // Another transfer won the filename race.
                // Try the next suffix without overwriting it.
            }
        }

        throw new IOException(
            "An available destination filename could not be found.");
    }

    private static string CreateCollisionFileName(
        string fileName,
        int collisionIndex)
    {
        if (collisionIndex == 0)
        {
            return fileName;
        }

        string extension = Path.GetExtension(fileName);

        if (
            extension.Length >
            MaximumPreservedExtensionLength
        )
        {
            extension = string.Empty;
        }

        string stem = extension.Length == 0
            ? fileName
            : fileName[..^extension.Length];

        string suffix = $" ({collisionIndex})";

        int maximumStemLength =
            MaximumFileNameLength -
            extension.Length -
            suffix.Length;

        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength];
        }

        stem = stem.TrimEnd(' ', '.');

        if (stem.Length == 0)
        {
            stem = "_";
        }

        return stem + suffix + extension;
    }

    private static string ResolveSafeFileName(
        HttpContentHeaders headers,
        Uri source,
        string? suggestedFileName = null,
        bool preferSuggestedName = false)
    {
        string? dispositionName =
            GetContentDispositionFileName(headers);

        string? safeDispositionName =
            SanitizeFileName(dispositionName);

        if (safeDispositionName is not null)
        {
            return safeDispositionName;
        }

        string? urlName = GetUrlFileName(source);
        string? safeUrlName = SanitizeFileName(urlName);
        string? safeSuggested = SanitizeFileName(suggestedFileName);

        // Host-advertised names (Pixeldrain info) beat the URL
        // when a resolver engaged: endpoint paths carry IDs, not
        // names. Server headers always stay authoritative, and the
        // suggestion passes through the same sanitizer.
        if (preferSuggestedName && safeSuggested is not null)
        {
            return safeSuggested;
        }

        if (safeUrlName is not null)
        {
            return safeUrlName;
        }

        return safeSuggested ?? "download";
    }

    private static string? GetContentDispositionFileName(
        HttpContentHeaders headers)
    {
        try
        {
            ContentDispositionHeaderValue? disposition =
                headers.ContentDisposition;

            if (disposition is null)
            {
                return null;
            }

            if (
                !string.IsNullOrWhiteSpace(
                    disposition.FileNameStar)
            )
            {
                return DecodeExtendedHeaderValue(
                    disposition.FileNameStar);
            }

            if (
                !string.IsNullOrWhiteSpace(
                    disposition.FileName)
            )
            {
                return Unquote(disposition.FileName);
            }
        }
        catch (FormatException)
        {
            // A malformed server-controlled header must not
            // influence the destination path.
        }

        return null;
    }

    private static string DecodeExtendedHeaderValue(
        string rawValue)
    {
        string value = Unquote(rawValue);

        int firstQuote = value.IndexOf(
            "'",
            StringComparison.Ordinal);

        int secondQuote = firstQuote < 0
            ? -1
            : value.IndexOf(
                "'",
                firstQuote + 1,
                StringComparison.Ordinal);

        if (
            firstQuote >= 0 &&
            secondQuote > firstQuote
        )
        {
            value = value[(secondQuote + 1)..];
        }

        return DecodePercentEscapes(value);
    }

    private static string Unquote(string rawValue)
    {
        string value = rawValue.Trim();

        if (
            value.Length >= 2 &&
            value[0] == '"' &&
            value[^1] == '"'
        )
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? GetUrlFileName(Uri source)
    {
        string escapedPath = source.GetComponents(
            UriComponents.Path,
            UriFormat.UriEscaped);

        if (
            string.IsNullOrWhiteSpace(escapedPath) ||
            escapedPath.EndsWith(
                "/",
                StringComparison.Ordinal)
        )
        {
            return null;
        }

        int separatorIndex =
            escapedPath.LastIndexOf('/');

        string escapedName = separatorIndex >= 0
            ? escapedPath[(separatorIndex + 1)..]
            : escapedPath;

        return DecodePercentEscapes(escapedName);
    }

    private static string DecodePercentEscapes(
        string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string? SanitizeFileName(
        string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string value = candidate
            .Trim()
            .Trim('"');

        int lastSeparator = Math.Max(
            value.LastIndexOf('/'),
            value.LastIndexOf('\\'));

        if (lastSeparator >= 0)
        {
            value = value[(lastSeparator + 1)..];
        }

        value = value
            .Trim()
            .Trim('"')
            .TrimEnd(' ', '.');

        if (
            value.Length == 0 ||
            value is "." or ".."
        )
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);

        foreach (char character in value)
        {
            builder.Append(
                IsInvalidFileNameCharacter(character)
                    ? '_'
                    : character);
        }

        string fileName = builder
            .ToString()
            .Trim()
            .TrimEnd(' ', '.');

        if (
            fileName.Length == 0 ||
            fileName is "." or ".."
        )
        {
            return null;
        }

        string deviceStem = fileName
            .Split('.', 2)[0]
            .TrimEnd(' ', '.');

        if (ReservedWindowsNames.Contains(deviceStem))
        {
            fileName = "_" + fileName;
        }

        return LimitFileName(fileName);
    }

    private static bool IsInvalidFileNameCharacter(
        char character)
    {
        return
            char.IsControl(character) ||
            character is
                '<' or
                '>' or
                ':' or
                '"' or
                '/' or
                '\\' or
                '|' or
                '?' or
                '*';
    }

    private static string LimitFileName(
        string fileName)
    {
        if (fileName.Length <= MaximumFileNameLength)
        {
            return fileName;
        }

        string extension = Path.GetExtension(fileName);

        if (
            extension.Length >
            MaximumPreservedExtensionLength
        )
        {
            extension = string.Empty;
        }

        int maximumStemLength =
            MaximumFileNameLength - extension.Length;

        string stem = extension.Length == 0
            ? fileName
            : fileName[..^extension.Length];

        stem = stem[..maximumStemLength]
            .TrimEnd(' ', '.');

        if (stem.Length == 0)
        {
            stem = "_";
        }

        return stem + extension;
    }

    private static long? DeriveTotalBytes(
        HttpContentHeaders contentHeaders,
        long resumeOffset)
    {
        // A 206 response carries the full length in
        // Content-Range (bytes s-e/total); otherwise the
        // remaining Content-Length plus what we already hold.
        if (contentHeaders.ContentRange?.Length is long total)
        {
            return total;
        }

        if (contentHeaders.ContentLength is long remaining)
        {
            return checked(resumeOffset + remaining);
        }

        return null;
    }

    private static void TruncateFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Truncate,
            FileAccess.Write,
            FileShare.None);
    }

    private static void TryDeleteTemporaryFile(
        string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
            when (
                exception is IOException or
                UnauthorizedAccessException
            )
        {
            // Cleanup is best effort. The original transfer
            // exception remains more useful to the caller.
        }
    }
}
