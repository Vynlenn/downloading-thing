using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeezNutzDownloader.App.Theming;
using DeezNutzDownloader.Core.Settings;
using DeezNutzDownloader.Infrastructure.Configuration;
using DeezNutzDownloader.Infrastructure.Logging;

namespace DeezNutzDownloader.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly AppPaths _paths;
    private readonly LocalLog _log;

    public MainWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        AppPaths paths,
        LocalLog log)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _paths = paths;
        _log = log;

        InitializeComponent();
        PopulateSettings();
        ShowPage("Downloads");
    }

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

        StorageModeText.Text = _paths.IsPortable
            ? "Portable mode"
            : "Standard Windows mode";

        DataFolderText.Text =
            $"Application data: {_paths.DataDirectory}";

        UpdateManualProxyState();
        UpdateNetworkSummary();
    }

    private void NavigationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is string pageName)
        {
            ShowPage(pageName);
        }
    }

    private void AddLinks_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage("LinkGrabber");
        LinksTextBox.Focus();
    }

    private void ClearLinks_Click(
        object sender,
        RoutedEventArgs e)
    {
        LinksTextBox.Clear();

        LinkGrabberStatusText.Text =
            "The link-analysis engine has not been connected yet.";

        ApplicationStatusText.Text =
            "Link input cleared — no network activity";
    }

    private void AnalyzeLinks_Click(
        object sender,
        RoutedEventArgs e)
    {
        string[] links = LinksTextBox.Text
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        if (links.Length == 0)
        {
            LinkGrabberStatusText.Text =
                "Paste at least one address before analyzing.";

            return;
        }

        LinkGrabberStatusText.Text =
            $"{links.Length} link(s) entered locally. " +
            "No network request was made because the analysis " +
            "engine is not installed yet.";

        ApplicationStatusText.Text =
            $"{links.Length} link(s) captured locally";
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
            _settings.ProxyHost = ProxyHostTextBox.Text.Trim();
            _settings.ProxyPort = port;

            await _settingsStore.SaveAsync(_settings);
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

    private void ShowPage(string pageName)
    {
        DownloadsPage.Visibility =
            pageName == "Downloads"
                ? Visibility.Visible
                : Visibility.Collapsed;

        LinkGrabberPage.Visibility =
            pageName == "LinkGrabber"
                ? Visibility.Visible
                : Visibility.Collapsed;

        SettingsPage.Visibility =
            pageName == "Settings"
                ? Visibility.Visible
                : Visibility.Collapsed;

        DownloadsNavigationButton.Opacity =
            pageName == "Downloads" ? 1.0 : 0.65;

        LinkGrabberNavigationButton.Opacity =
            pageName == "LinkGrabber" ? 1.0 : 0.65;

        SettingsNavigationButton.Opacity =
            pageName == "Settings" ? 1.0 : 0.65;

        switch (pageName)
        {
            case "LinkGrabber":
                PageTitleText.Text = "Link Grabber";
                PageSubtitleText.Text =
                    "Collect, inspect, and organize links before downloading.";
                break;

            case "Settings":
                PageTitleText.Text = "Settings";
                PageSubtitleText.Text =
                    "Configure storage, appearance, and network behavior.";
                break;

            default:
                PageTitleText.Text = "Downloads";
                PageSubtitleText.Text =
                    "Manage active, queued, and completed downloads.";
                break;
        }
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
}
