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

public partial class MainWindow : Window
{
    private const int MaximumSupportedConcurrency = 64;

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly AppPaths _paths;
    private readonly LocalLog _log;
    private readonly DownloadQueue _downloadQueue = new();
    private readonly DownloadQueueStore _queueStore;
    private readonly ChallengeSessionStore _challengeStore;
    private readonly DownloadHistoryStore _historyStore;
    private DownloadHistory _history = new();
    private readonly SemaphoreSlim _queuePersistGate = new(1, 1);
    private DateTimeOffset _lastQueuePersistUtc =
        DateTimeOffset.MinValue;

    private readonly ObservableCollection<DownloadQueueItem>
        _visibleDownloadItems = new();

    private DownloadOrchestrator? _activeDownloadOrchestrator;
    private HttpClient? _activeDownloadHttpClient;
    private LinkAnalysisResult? _lastLinkAnalysis;
    private IReadOnlyList<LinkPackage> _lastLinkPackages =
        Array.Empty<LinkPackage>();
    private readonly DispatcherTimer _clipboardWatchTimer = new();
    private string _lastClipboardText = string.Empty;
    private int _downloadPresentationRefreshPending;
    private bool _cancellationRequested;
    private bool _videoGrabBusy;
    private bool _crawlBusy;
    private volatile bool _isClosing;

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
        _queueStore = new DownloadQueueStore(paths);
        _challengeStore = new ChallengeSessionStore(paths);
        _historyStore = new DownloadHistoryStore(paths);

        InitializeComponent();

        DownloadQueueList.ItemsSource =
            _visibleDownloadItems;

        _downloadQueue.Changed +=
            DownloadQueue_Changed;

        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        PopulateSettings();
        RefreshDownloadQueue();
        ShowPage("Downloads");
        InitializeTray();

        _clipboardWatchTimer.Interval = TimeSpan.FromSeconds(1);
        _clipboardWatchTimer.Tick += ClipboardWatchTimer_Tick;
        _clipboardWatchTimer.Start();

        _ = RestoreDownloadQueueAsync();
        _ = RestoreHistoryAsync();
        _ = CheckUpdatesOnStartupAsync();
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

    // Closing must never block the UI thread on async saves: the
    // stores' continuations need the dispatcher, so Wait()/GetResult()
    // here deadlocked every close. Instead the first close cancels,
    // saves in the background, then closes for real.
    private bool _closeConfirmed;

    private async void MainWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _clipboardWatchTimer.Stop();

        _activeDownloadOrchestrator?.Cancel();
        _linkCheckCancellation?.Cancel();

        // Give active transfers a moment to stop and record how much
        // of each partial file is good, so the next start resumes.
        if (_activeBatchTask is Task<DownloadBatchResult> batch)
        {
            try
            {
                await Task.WhenAny(batch, Task.Delay(TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(true);
            }
            catch
            {
            }
        }

        try
        {
            await _queuePersistGate.WaitAsync().ConfigureAwait(true);

            try
            {
                await _queueStore.SaveAsync(_downloadQueue.Items);
            }
            finally
            {
                _queuePersistGate.Release();
            }

            RecordCompletedItems();
            await _historyStore.SaveAsync(_history);
        }
        catch
        {
        }

        _closeConfirmed = true;
        Close();
    }

    private void MainWindow_Closed(
        object? sender,
        EventArgs e)
    {
        DisposeTray();
        _queuePersistGate.Dispose();

        _downloadQueue.Changed -=
            DownloadQueue_Changed;

        DownloadOrchestrator? orchestrator =
            _activeDownloadOrchestrator;

        HttpClient? httpClient =
            _activeDownloadHttpClient;

        _activeDownloadOrchestrator = null;
        _activeDownloadHttpClient = null;

        if (orchestrator is not null)
        {
            orchestrator.RunningStateChanged -=
                DownloadOrchestrator_RunningStateChanged;

            orchestrator.Cancel();
            orchestrator.Dispose();
        }

        httpClient?.Dispose();
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

        ApplyNavigationState(
            DownloadsNavigationButton,
            pageName == "Downloads");

        ApplyNavigationState(
            LinkGrabberNavigationButton,
            pageName == "LinkGrabber");

        ApplyNavigationState(
            SettingsNavigationButton,
            pageName == "Settings");

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

    private static void ApplyNavigationState(
        System.Windows.Controls.Button button,
        bool isSelected)
    {
        button.Opacity = 1.0;

        button.SetResourceReference(
            FrameworkElement.StyleProperty,
            isSelected
                ? "SelectedNavigationButtonStyle"
                : "NavigationButtonStyle");
    }
}
