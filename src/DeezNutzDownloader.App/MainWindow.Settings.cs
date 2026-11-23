using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Adapters.Media;
using DeezNutzDownloader.App.Networking;
using DeezNutzDownloader.App.Theming;
using DeezNutzDownloader.App.Updates;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;
using DeezNutzDownloader.Core.Settings;
using DeezNutzDownloader.Core.Updates;
using DeezNutzDownloader.Infrastructure.Challenges;
using DeezNutzDownloader.Infrastructure.Configuration;
using DeezNutzDownloader.Infrastructure.Downloads;
using DeezNutzDownloader.Infrastructure.Logging;
using DeezNutzDownloader.Infrastructure.Security;

namespace DeezNutzDownloader.App;



public partial class MainWindow

{

    private void PopulateSettings()
    {
        DownloadFolderTextBox.Text = _settings.DownloadFolder;

        ThemeComboBox.SelectedIndex = _settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0
        };

        NetworkModeComboBox.SelectedIndex =
            _settings.NetworkMode switch
            {
                NetworkMode.Direct => 1,
                NetworkMode.ManualProxy => 2,
                _ => 0
            };

        ProxyTypeComboBox.SelectedIndex =
            _settings.ProxyType switch
            {
                ProxyType.Https => 1,
                ProxyType.Socks4 => 2,
                ProxyType.Socks5 => 3,
                _ => 0
            };

        ProxyHostTextBox.Text = _settings.ProxyHost;
        ProxyPortTextBox.Text = _settings.ProxyPort.ToString();
        ProxyUsernameTextBox.Text = _settings.ProxyUsername;
        ProxyPasswordBox.Password = string.Empty;
        PixeldrainApiKeyBox.Password = string.Empty;
        UpdatePixeldrainKeyStatus();
        ConcurrencyTextBox.Text =
            _settings.MaximumConcurrentDownloads.ToString();
        ConnectionsTextBox.Text =
            _settings.ConnectionsPerDownload.ToString();
        YtDlpPathTextBox.Text = _settings.YtDlpPath;
        FFmpegPathTextBox.Text = _settings.FFmpegPath;
        SevenZipPathTextBox.Text = _settings.SevenZipPath;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        YtDlpCookiesTextBox.Text =
            _settings.YtDlpCookiesFromBrowser;
        YtDlpSubtitlesTextBox.Text =
            _settings.YtDlpSubtitleLangs;
        AutoToolsCheckBox.IsChecked =
            _settings.ExternalToolsAutoUpdate;
        OrganizeFoldersCheckBox.IsChecked =
            _settings.OrganizeIntoSiteFolders;
        ClipboardWatchCheckBox.IsChecked =
            _settings.ClipboardWatchEnabled;
        AskExtractCheckBox.IsChecked =
            _settings.AskBeforeArchiveExtraction;
        QuietHoursCheckBox.IsChecked =
            _settings.QuietHoursEnabled;
        QuietStartTextBox.Text = FormatClock(
            _settings.QuietHoursStartHour,
            _settings.QuietHoursStartMinute);
        QuietEndTextBox.Text = FormatClock(
            _settings.QuietHoursEndHour,
            _settings.QuietHoursEndMinute);
        QuietCapTextBox.Text =
            _settings.QuietHoursCapKBps.ToString();
        UpdateRepoTextBox.Text = _settings.UpdateRepository;
        UpdateCheckStartupCheckBox.IsChecked =
            _settings.CheckForUpdatesOnStartup;
        UpdateStatusText.Text = string.Empty;

        StorageModeText.Text = _paths.IsPortable
            ? "Portable mode"
            : "Standard Windows mode";

        DataFolderText.Text =
            $"Application data: {_paths.DataDirectory}";

        UpdateManualProxyState();
        UpdateNetworkSummary();
    }

    private static string FormatClock(int hour, int minute)
    {
        return $"{hour:D2}:{minute:D2}";
    }

    private static int ParseClockHour(string text, string field)
    {
        if (
            TimeOnly.TryParse(text?.Trim(), out TimeOnly parsed))
        {
            return parsed.Hour;
        }

        throw new InvalidOperationException(
            "Quiet-hours times must look like 22:00.");
    }

    private static int ParseClockMinute(string text, string field)
    {
        if (
            TimeOnly.TryParse(text?.Trim(), out TimeOnly parsed))
        {
            return parsed.Minute;
        }

        throw new InvalidOperationException(
            "Quiet-hours times must look like 22:00.");
    }

    private void UseDefaultFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        DownloadFolderTextBox.Text =
            DefaultSettingsFactory.GetDefaultDownloadFolder();
    }

    private void ThemeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !Enum.TryParse(value, out AppTheme selectedTheme))
        {
            return;
        }

        ThemeManager.Apply(selectedTheme);
    }

    private void NetworkModeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateManualProxyState();
        UpdateNetworkSummary();
    }

    private async void SaveSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            string folder = Environment.ExpandEnvironmentVariables(
                DownloadFolderTextBox.Text.Trim());

            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new InvalidOperationException(
                    "Choose a default download folder.");
            }

            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);

            AppTheme selectedTheme =
                ReadEnumTag(ThemeComboBox, AppTheme.System);

            NetworkMode selectedNetworkMode =
                ReadEnumTag(
                    NetworkModeComboBox,
                    NetworkMode.WindowsSettings);

            ProxyType selectedProxyType =
                ReadEnumTag(ProxyTypeComboBox, ProxyType.Http);

            int port = 8080;

            if (selectedNetworkMode == NetworkMode.ManualProxy)
            {
                if (string.IsNullOrWhiteSpace(
                    ProxyHostTextBox.Text))
                {
                    throw new InvalidOperationException(
                        "Enter a proxy server name or IP address.");
                }

                if (!int.TryParse(
                        ProxyPortTextBox.Text,
                        out port) ||
                    port is < 1 or > 65535)
                {
                    throw new InvalidOperationException(
                        "The proxy port must be between 1 and 65535.");
                }
            }
            else if (int.TryParse(
                         ProxyPortTextBox.Text,
                         out int optionalPort) &&
                     optionalPort is >= 1 and <= 65535)
            {
                port = optionalPort;
            }

            _settings.DownloadFolder = folder;
            _settings.Theme = selectedTheme;
            _settings.NetworkMode = selectedNetworkMode;
            _settings.ProxyType = selectedProxyType;
            if (!int.TryParse(
                    ConcurrencyTextBox.Text.Trim(),
                    out int maximumConcurrency) ||
                maximumConcurrency is < 1 or > 64)
            {
                throw new InvalidOperationException(
                    "Concurrent downloads must be between 1 and 64.");
            }

            if (!int.TryParse(
                    ConnectionsTextBox.Text.Trim(),
                    out int connectionsPerDownload) ||
                connectionsPerDownload is < 1 or > 16)
            {
                throw new InvalidOperationException(
                    "Connections per file must be between 1 and 16.");
            }

            _settings.MaximumConcurrentDownloads =
                maximumConcurrency;
            _settings.ConnectionsPerDownload =
                connectionsPerDownload;
            _settings.YtDlpPath = YtDlpPathTextBox.Text.Trim();
            _settings.FFmpegPath = FFmpegPathTextBox.Text.Trim();
            _settings.SevenZipPath = SevenZipPathTextBox.Text.Trim();
            _settings.MinimizeToTray =
                MinimizeToTrayCheckBox.IsChecked == true;
            _settings.YtDlpCookiesFromBrowser =
                YtDlpCookiesTextBox.Text.Trim();
            _settings.YtDlpSubtitleLangs =
                YtDlpSubtitlesTextBox.Text.Trim();
            _settings.ExternalToolsAutoUpdate =
                AutoToolsCheckBox.IsChecked != false;
            _settings.OrganizeIntoSiteFolders =
                OrganizeFoldersCheckBox.IsChecked != false;
            _settings.ClipboardWatchEnabled =
                ClipboardWatchCheckBox.IsChecked != false;
            _settings.AskBeforeArchiveExtraction =
                AskExtractCheckBox.IsChecked != false;
            _settings.UpdateRepository =
                UpdateRepoTextBox.Text.Trim();
            _settings.CheckForUpdatesOnStartup =
                UpdateCheckStartupCheckBox.IsChecked != false;

            if (
                _settings.UpdateRepository.Length > 0 &&
                !UpdateCheck.IsValidRepository(
                    _settings.UpdateRepository))
            {
                throw new InvalidOperationException(
                    "Update repository must look like owner/name.");
            }
            _settings.QuietHoursEnabled =
                QuietHoursCheckBox.IsChecked == true;
            _settings.QuietHoursStartHour = ParseClockHour(
                QuietStartTextBox.Text,
                nameof(QuietStartTextBox));
            _settings.QuietHoursStartMinute = ParseClockMinute(
                QuietStartTextBox.Text,
                nameof(QuietStartTextBox));
            _settings.QuietHoursEndHour = ParseClockHour(
                QuietEndTextBox.Text,
                nameof(QuietEndTextBox));
            _settings.QuietHoursEndMinute = ParseClockMinute(
                QuietEndTextBox.Text,
                nameof(QuietEndTextBox));
            if (!int.TryParse(
                    QuietCapTextBox.Text.Trim(),
                    out int quietCapKBps) ||
                quietCapKBps is < 1 or > 1_000_000)
            {
                throw new InvalidOperationException(
                    "Quiet-hours cap must be 1 to 1000000 KB/s.");
            }

            _settings.QuietHoursCapKBps = quietCapKBps;
            _settings.ProxyHost = ProxyHostTextBox.Text.Trim();
            _settings.ProxyPort = port;
            _settings.ProxyUsername = ProxyUsernameTextBox.Text.Trim();

            // The password box is never pre-filled: an empty box
            // keeps the saved (encrypted) password, a typed one
            // replaces it, and clearing the username drops it.
            if (ProxyPasswordBox.Password.Length > 0)
            {
                _settings.ProxyPasswordProtected =
                    ProxyCredentialProtector.Protect(
                        ProxyPasswordBox.Password);
            }
            else if (_settings.ProxyUsername.Length == 0)
            {
                _settings.ProxyPasswordProtected = string.Empty;
            }

            // A typed key replaces the saved one; empty keeps it
            // (Remove key clears it explicitly).
            if (PixeldrainApiKeyBox.Password.Trim().Length > 0)
            {
                _settings.PixeldrainApiKeyProtected =
                    ProxyCredentialProtector.Protect(
                        PixeldrainApiKeyBox.Password.Trim());

                PixeldrainApiKeyBox.Password = string.Empty;
            }

            // Immediate feedback: bad host/port, SOCKS, or an
            // undecryptable password fails here, not mid-batch.
            DownloadHttpClientFactory.Validate(_settings);

            await _settingsStore.SaveAsync(_settings);
        UpdatePixeldrainKeyStatus();
            await _log.InformationAsync(
                "Application settings were saved.");

            DownloadFolderTextBox.Text = folder;
            ThemeManager.Apply(selectedTheme);
            UpdateNetworkSummary();

            ApplicationStatusText.Text =
                "Settings saved successfully";
        }
        catch (Exception exception)
        {
            await _log.ErrorAsync(
                "Settings could not be saved.",
                exception);

            MessageBox.Show(
                exception.Message,
                "Settings could not be saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ProxyTypeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateManualProxyState();
    }

    private void UpdateManualProxyState()
    {
        if (ManualProxyPanel is null ||
            NetworkModeComboBox is null)
        {
            return;
        }

        NetworkMode mode = ReadEnumTag(
            NetworkModeComboBox,
            NetworkMode.WindowsSettings);

        bool enabled = mode == NetworkMode.ManualProxy;

        ManualProxyPanel.IsEnabled = enabled;
        ManualProxyPanel.Opacity = enabled ? 1.0 : 0.55;

        if (ProxySocksWarningText is not null &&
            ProxyTypeComboBox is not null)
        {
            ProxyType selectedType = ReadEnumTag(
                ProxyTypeComboBox,
                ProxyType.Http);

            ProxySocksWarningText.Visibility =
                selectedType is ProxyType.Socks4
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void UpdateNetworkSummary()
    {
        if (NetworkSummaryText is null ||
            NetworkModeComboBox is null)
        {
            return;
        }

        NetworkMode mode = ReadEnumTag(
            NetworkModeComboBox,
            NetworkMode.WindowsSettings);

        NetworkSummaryText.Text = mode switch
        {
            NetworkMode.Direct =>
                "Direct connection",

            NetworkMode.ManualProxy =>
                "Manual proxy",

            _ =>
                "Windows settings"
        };
    }

    private static TEnum ReadEnumTag<TEnum>(
        ComboBox comboBox,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        if (comboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string value &&
            Enum.TryParse(value, out TEnum parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private void UpdatePixeldrainKeyStatus()
    {
        bool saved = !string.IsNullOrEmpty(_settings.PixeldrainApiKeyProtected);

        PixeldrainKeyStatusText.Text = saved
            ? "A key is saved and used for Pixeldrain downloads."
            : "No key saved: Pixeldrain's free per-IP limits apply.";

        RemovePixeldrainKeyButton.IsEnabled = saved;
    }

    private async void CheckPixeldrainKey_Click(object sender, RoutedEventArgs e)
    {
        // Check what is typed, or else the saved key.
        string? key = PixeldrainApiKeyBox.Password.Trim();

        if (key.Length == 0)
        {
            key = ProxyCredentialProtector.Unprotect(_settings.PixeldrainApiKeyProtected);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            PixeldrainKeyStatusText.Text = "Paste a key first.";
            return;
        }

        CheckPixeldrainKeyButton.IsEnabled = false;
        PixeldrainKeyStatusText.Text = "Checking with Pixeldrain…";

        try
        {
            using HttpClient client = DownloadHttpClientFactory.Create(_settings);
            PixeldrainKeyCheck result = await PixeldrainAccount.CheckKeyAsync(client, key);

            PixeldrainKeyStatusText.Text =
                result.Message +
                (result.Valid && PixeldrainApiKeyBox.Password.Trim().Length > 0
                    ? " Press Save settings to keep it."
                    : string.Empty);
        }
        catch (Exception exception)
        {
            PixeldrainKeyStatusText.Text = $"Could not check the key: {exception.Message}";
        }
        finally
        {
            CheckPixeldrainKeyButton.IsEnabled = true;
        }
    }

    private async void RemovePixeldrainKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.PixeldrainApiKeyProtected = string.Empty;
        PixeldrainApiKeyBox.Password = string.Empty;

        try
        {
            await _settingsStore.SaveAsync(_settings);
            UpdatePixeldrainKeyStatus();
            PixeldrainKeyStatusText.Text = "Key removed.";
        }
        catch (Exception exception)
        {
            PixeldrainKeyStatusText.Text = $"Could not save: {exception.Message}";
        }
    }
}

