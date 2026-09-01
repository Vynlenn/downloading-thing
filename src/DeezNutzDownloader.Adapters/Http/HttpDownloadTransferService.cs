using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using DeezNutzDownloader.Core.Downloads;

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

    public HttpDownloadTransferService(HttpClient httpClient)
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<DownloadTransferResult> DownloadAsync(
        DownloadTransferRequest request,
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri source = ValidateSource(request.Source);

        string destinationDirectory =
            ValidateDestinationDirectory(
                request.DestinationDirectory);

        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest =
            new HttpRequestMessage(HttpMethod.Get, source);

        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        HttpContent content =
            response.Content ??
            throw new HttpRequestException(
                "The HTTP response did not contain a body.");

        Uri fileNameSource =
            response.RequestMessage?.RequestUri is
                { IsAbsoluteUri: true } responseUri
                ? responseUri
                : source;

        string fileName =
            ResolveSafeFileName(content.Headers, fileNameSource);

        Directory.CreateDirectory(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        (
            string temporaryPath,
            FileStream temporaryStream
        ) = CreateTemporaryFile(destinationDirectory);

        string? pendingTemporaryPath = temporaryPath;
        long bytesWritten = 0;
        long? totalBytes = content.Headers.ContentLength;

        try
        {
            await using (temporaryStream)
            {
                await using Stream sourceStream =
                    await content.ReadAsStreamAsync(
                            cancellationToken)
                        .ConfigureAwait(false);

                progress?.Report(
                    new DownloadTransferProgress(
                        bytesWritten,
                        totalBytes));

                byte[] buffer =
                    ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
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

                        await temporaryStream.WriteAsync(
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

                    await temporaryStream.FlushAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            string destinationPath =
                MoveToAvailableDestination(
                    temporaryPath,
                    destinationDirectory,
                    fileName);

            pendingTemporaryPath = null;

            return new DownloadTransferResult(
                source,
                destinationPath,
                Path.GetFileName(destinationPath),
                bytesWritten,
                totalBytes);
        }
        finally
        {
            if (pendingTemporaryPath is not null)
            {
                TryDeleteTemporaryFile(
                    pendingTemporaryPath);
            }
        }
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
        Uri source)
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

        return safeUrlName ?? "download";
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
