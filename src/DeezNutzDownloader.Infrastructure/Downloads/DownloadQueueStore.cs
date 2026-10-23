using System.Text.Json;
using System.Text.Json.Serialization;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Infrastructure.Configuration;

namespace DeezNutzDownloader.Infrastructure.Downloads;

public sealed class DownloadQueueStore
{
    private readonly string _queueFile;

    private readonly JsonSerializerOptions _jsonOptions;

    public DownloadQueueStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _queueFile = paths.DownloadQueueFile;

        _jsonOptions = CreateJsonOptions();
    }

    public DownloadQueueStore(string queueFile)
    {
        if (string.IsNullOrWhiteSpace(queueFile))
        {
            throw new ArgumentException(
                "A queue file path is required.",
                nameof(queueFile));
        }

        _queueFile = queueFile;

        _jsonOptions = CreateJsonOptions();
    }

    public async Task SaveAsync(
        IReadOnlyList<DownloadQueueItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        string? directory = Path.GetDirectoryName(_queueFile);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var snapshot = new DownloadQueueSnapshot(
            1,
            items.ToArray());

        string json =
            JsonSerializer.Serialize(snapshot, _jsonOptions);

        string temporaryFile = _queueFile + ".tmp";

        await File.WriteAllTextAsync(
            temporaryFile,
            json,
            cancellationToken).ConfigureAwait(false);

        File.Move(temporaryFile, _queueFile, true);
    }

    public async Task<IReadOnlyList<DownloadQueueItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_queueFile))
        {
            return Array.Empty<DownloadQueueItem>();
        }

        try
        {
            string json =
                await File.ReadAllTextAsync(
                    _queueFile,
                    cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<DownloadQueueItem>();
            }

            DownloadQueueSnapshot? snapshot =
                JsonSerializer.Deserialize<DownloadQueueSnapshot>(
                    json,
                    _jsonOptions);

            return snapshot?.Items ??
                Array.Empty<DownloadQueueItem>();
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                NotSupportedException)
        {
            return Array.Empty<DownloadQueueItem>();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }

    private sealed record DownloadQueueSnapshot(
        int Version,
        DownloadQueueItem[] Items);
}
