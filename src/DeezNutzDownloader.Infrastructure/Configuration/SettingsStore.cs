using System.Text.Json;
using System.Text.Json.Serialization;
using DeezNutzDownloader.Core.Settings;

namespace DeezNutzDownloader.Infrastructure.Configuration;

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions;

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<AppSettings> LoadAsync()
    {
        _paths.EnsureDirectoriesExist();

        if (!File.Exists(_paths.SettingsFile))
        {
            AppSettings defaults = DefaultSettingsFactory.Create();
            await SaveAsync(defaults);
            return defaults;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_paths.SettingsFile);

            AppSettings? settings =
                JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);

            if (settings is null)
            {
                throw new InvalidDataException(
                    "The settings file contained no settings.");
            }

            bool settingsChanged = false;

            if (
                settings.MaximumConcurrentDownloads is < 1 or
                > 64
            )
            {
                settings.MaximumConcurrentDownloads = 3;
                settingsChanged = true;
            }

            if (settings.ProxyPort is < 1 or > 65535)
            {
                settings.ProxyPort = 8080;
                settingsChanged = true;
            }

            if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
            {
                settings.DownloadFolder =
                    DefaultSettingsFactory.GetDefaultDownloadFolder();

                settingsChanged = true;
            }

            // Version 2 introduces the Dark Jade default theme.
            // Existing version-1 settings are migrated only once.
            if (settings.ConfigurationVersion < 2)
            {
                settings.ConfigurationVersion = 2;
                settings.Theme = AppTheme.Dark;
                settingsChanged = true;
            }

            // Version 4 introduces managed yt-dlp/ffmpeg.
            // Existing installs opt in automatically; the toggle
            // plus manual paths remain available in Settings.
            if (settings.ConfigurationVersion < 4)
            {
                settings.ConfigurationVersion = 4;
                settings.ExternalToolsAutoUpdate = true;
                settingsChanged = true;
            }

            if (settingsChanged)
            {
                await SaveAsync(settings);
            }

            return settings;
        }
        catch (Exception) when (File.Exists(_paths.SettingsFile))
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            string backupFile =
                _paths.SettingsFile + ".corrupt-" + timestamp;

            File.Move(_paths.SettingsFile, backupFile, true);

            AppSettings defaults = DefaultSettingsFactory.Create();
            await SaveAsync(defaults);
            return defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        _paths.EnsureDirectoriesExist();

        string json = JsonSerializer.Serialize(settings, _jsonOptions);
        string temporaryFile = _paths.SettingsFile + ".tmp";

        await File.WriteAllTextAsync(temporaryFile, json);
        File.Move(temporaryFile, _paths.SettingsFile, true);
    }
}
