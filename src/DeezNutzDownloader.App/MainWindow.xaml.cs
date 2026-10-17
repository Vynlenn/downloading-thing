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
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;
using DeezNutzDownloader.Core.Settings;
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

        _clipboardWatchTimer.Interval = TimeSpan.FromSeconds(1);
        _clipboardWatchTimer.Tick += ClipboardWatchTimer_Tick;
        _clipboardWatchTimer.Start();

        _ = RestoreDownloadQueueAsync();
        _ = RestoreHistoryAsync();
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
        ProxyUsernameTextBox.Text = _settings.ProxyUsername;
        ProxyPasswordBox.Password = string.Empty;
        ConcurrencyTextBox.Text =
            _settings.MaximumConcurrentDownloads.ToString();
        YtDlpPathTextBox.Text = _settings.YtDlpPath;
        FFmpegPathTextBox.Text = _settings.FFmpegPath;
        YtDlpCookiesTextBox.Text =
            _settings.YtDlpCookiesFromBrowser;
        AutoToolsCheckBox.IsChecked =
            _settings.ExternalToolsAutoUpdate;
        OrganizeFoldersCheckBox.IsChecked =
            _settings.OrganizeIntoSiteFolders;
        ClipboardWatchCheckBox.IsChecked =
            _settings.ClipboardWatchEnabled;

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

        ResetLinkAnalysisPresentation(
            "Paste links above to analyze them locally.");

        ApplicationStatusText.Text =
            "Link input cleared - no network activity";
    }

    private void AnalyzeLinks_Click(
        object sender,
        RoutedEventArgs e)
    {
        LinkAnalysisResult result =
            LinkAnalyzer.Analyze(LinksTextBox.Text);

        _lastLinkAnalysis = result;
        _lastLinkPackages = LinkPackager.GroupByHost(result);
        AnalysisResultsList.ItemsSource = result.Items;
        UpdatePackagePresentation();

        SetLinkAnalysisCounts(
            result.ValidCount,
            result.DuplicateCount,
            result.InvalidCount);

        QueueValidLinksButton.IsEnabled =
            result.ValidCount > 0;

        if (result.TotalCount == 0)
        {
            LinkGrabberStatusText.Text =
                "Paste at least one address before analyzing.";

            ApplicationStatusText.Text =
                "Nothing to analyze - no network activity";

            return;
        }

        string linkWord =
            result.TotalCount == 1 ? "link" : "links";

        LinkGrabberStatusText.Text =
            $"Analyzed {result.TotalCount} {linkWord} locally. " +
            "No network requests were made.";

        ApplicationStatusText.Text =
            $"Link analysis complete - {result.ValidCount} ready, " +
            $"{result.DuplicateCount} duplicate, " +
            $"{result.InvalidCount} invalid";
    }

    private async void CrawlPage_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_crawlBusy)
        {
            return;
        }

        string[] lines =
            (LinksTextBox.Text ?? string.Empty)
                .Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

        if (
            lines.Length != 1 ||
            !Uri.TryCreate(
                lines[0],
                UriKind.Absolute,
                out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            LinkGrabberStatusText.Text =
                "Paste a single page address above, then use " +
                "Crawl page. Direct file links still go " +
                "through Analyze links.";

            return;
        }

        _crawlBusy = true;
        CrawlPageButton.IsEnabled = false;

        try
        {
            LinkGrabberStatusText.Text =
                "Fetching the page and filtering its links…";

            PageCrawlResult result;

            try
            {
                result = await FetchAndExtractAsync(uri);
            }
            catch (ChallengeDetectedException challenge)
                when (challenge.IsSolvableInBrowser)
            {
                // One solve, one retry: the fresh cookies replay
                // through the same factory-built client.
                if (!await SolveChallengeAsync(challenge))
                {
                    throw;
                }

                result = await FetchAndExtractAsync(uri);
            }

            if (result.Files.Count == 0)
            {
                LinkGrabberStatusText.Text =
                    $"No file links found — walked " +
                    $"{result.PagesVisited} page" +
                    $"{(result.PagesVisited == 1 ? "" : "s")}, " +
                    $"scanned {result.AnchorsExamined} links, " +
                    $"skipped {result.SkippedCount} (avatars, " +
                    "logos, navigation).";

                ApplicationStatusText.Text =
                    "Page crawl finished - no files found";

                return;
            }

            LinksTextBox.Text = string.Join(
                Environment.NewLine,
                result.Files.Select(file => file.Url));

            AnalyzeLinks_Click(this, new RoutedEventArgs());

            LinkGrabberStatusText.Text =
                $"Crawled {result.PagesVisited} page" +
                $"{(result.PagesVisited == 1 ? "" : "s")}: " +
                $"{result.Files.Count} file link" +
                $"{(result.Files.Count == 1 ? "" : "s")} ready, " +
                $"{result.SkippedCount} junk skipped. Review " +
                "below, then add to the queue.";

            ApplicationStatusText.Text =
                $"Page crawl finished - {result.Files.Count} " +
                "file links ready";
        }
        catch (Exception exception)
        {
            string message = string.IsNullOrWhiteSpace(
                exception.Message)
                    ? "The page crawl failed."
                    : exception.Message.Trim();

            if (message.Length > 500)
            {
                message = message[..500];
            }

            LinkGrabberStatusText.Text = message;
            ApplicationStatusText.Text = "Page crawl failed";
        }
        finally
        {
            _crawlBusy = false;
            CrawlPageButton.IsEnabled = true;
        }
    }

    private async void GrabVideo_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_videoGrabBusy)
        {
            return;
        }

        string[] lines =
            (LinksTextBox.Text ?? string.Empty)
                .Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

        if (
            lines.Length != 1 ||
            !Uri.TryCreate(
                lines[0],
                UriKind.Absolute,
                out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            LinkGrabberStatusText.Text =
                "Paste a single video page address above, " +
                "then use Grab video. Direct file links still " +
                "go through Analyze links.";

            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.DownloadFolder))
        {
            LinkGrabberStatusText.Text =
                "Set a download folder in Settings first.";

            ShowPage("Settings");
            return;
        }

        string videoUrl = uri.AbsoluteUri;
        var media = new YtDlpMediaService();

        string? proxyUrl =
            ProxyConfiguration.BuildYtDlpProxyUrl(
                _settings,
                ProxyCredentialProtector.Unprotect(
                    _settings.ProxyPasswordProtected));

        _videoGrabBusy = true;
        GrabVideoButton.IsEnabled = false;

        try
        {
            LinkGrabberStatusText.Text =
                "Preparing media tools…";

            string ytDlpPath;
            string ffmpegPath;

            using (HttpClient toolClient =
                DownloadHttpClientFactory.Create(_settings))
            {
                var provisioner = new ExternalToolProvisioner(
                    toolClient,
                    _paths.ToolsDirectory);

                var toolLog = new Progress<string>(line =>
                    LinkGrabberStatusText.Text = line);

                ytDlpPath = await provisioner.ResolveAsync(
                    ExternalTool.YtDlp,
                    _settings.YtDlpPath,
                    _settings.ExternalToolsAutoUpdate,
                    toolLog);

                ffmpegPath = await provisioner.ResolveAsync(
                    ExternalTool.FFmpeg,
                    _settings.FFmpegPath,
                    _settings.ExternalToolsAutoUpdate,
                    toolLog);
            }

            bool available =
                await Task.Run(
                    () => media.IsAvailableAsync(ytDlpPath));

            if (!available)
            {
                LinkGrabberStatusText.Text =
                    "yt-dlp was not found. Turn on automatic media " +
                    "tools in Settings (or set a path / install it " +
                    "on PATH), then try again.";

                ShowPage("Settings");
                return;
            }

            string? browserCookies =
                string.IsNullOrWhiteSpace(
                    _settings.YtDlpCookiesFromBrowser)
                    ? null
                    : _settings.YtDlpCookiesFromBrowser.Trim();

            LinkGrabberStatusText.Text =
                "Probing video formats with yt-dlp…";

            MediaProbeResult probe =
                await Task.Run(
                    () => media.ProbeAsync(
                        videoUrl,
                        ytDlpPath,
                        CancellationToken.None,
                        proxyUrl,
                        browserCookies));

            LinkGrabberStatusText.Text = probe.IsPlaylist
                ? $"Found: {probe.Summary}. Playlists currently " +
                    "grab the single video, not every item. " +
                    "Downloading…"
                : $"Found: {probe.Summary}. " +
                    "Downloading best quality…";

            ApplicationStatusText.Text =
                $"Video probe complete - {probe.Title}";

            var progress = new Progress<string>(line =>
            {
                if (
                    line.StartsWith(
                        "[download]",
                        StringComparison.Ordinal) &&
                    line.Contains('%')
                )
                {
                    LinkGrabberStatusText.Text =
                        $"Downloading: {probe.Title} — " +
                        line.Trim();
                }
            });

            string savedPath =
                await Task.Run(
                    () => media.DownloadAsync(
                        videoUrl,
                        formatId: null,
                        _settings.DownloadFolder,
                        ytDlpPath,
                        ffmpegPath,
                        progress,
                        CancellationToken.None,
                        proxyUrl,
                        browserCookies));

            _history.Record(
                new DownloadHistoryEntry(
                    videoUrl,
                    Path.GetFileName(savedPath),
                    null,
                    DateTimeOffset.UtcNow));

            _ = _historyStore.SaveAsync(_history);

            LinkGrabberStatusText.Text =
                $"Saved {Path.GetFileName(savedPath)} " +
                "to the download folder.";

            ApplicationStatusText.Text =
                $"Video saved - {Path.GetFileName(savedPath)}";
        }
        catch (Exception exception)
        {
            string message = string.IsNullOrWhiteSpace(
                exception.Message)
                    ? "The video grab failed."
                    : exception.Message.Trim();

            if (message.Length > 500)
            {
                message = message[..500];
            }

            LinkGrabberStatusText.Text = message;
            ApplicationStatusText.Text = "Video grab failed";
        }
        finally
        {
            _videoGrabBusy = false;
            GrabVideoButton.IsEnabled = true;
        }
    }

    private void SetLinkAnalysisCounts(
        int validCount,
        int duplicateCount,
        int invalidCount)
    {
        ValidLinksCountText.Text =
            $"Valid  {validCount}";

        DuplicateLinksCountText.Text =
            $"Duplicates  {duplicateCount}";

        InvalidLinksCountText.Text =
            $"Invalid  {invalidCount}";
    }

    private void LinksTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        string message = string.IsNullOrWhiteSpace(
            LinksTextBox.Text)
                ? "Paste links above to analyze them locally."
                : "Input changed. Analyze the links to refresh the results.";

        ResetLinkAnalysisPresentation(message);
    }

    private void ResetLinkAnalysisPresentation(
        string message)
    {
        _lastLinkAnalysis = null;
        _lastLinkPackages = Array.Empty<LinkPackage>();
        AnalysisResultsList.ItemsSource = null;
        QueueValidLinksButton.IsEnabled = false;
        QueueValidLinksButton.Content = "Add valid to queue";

        SetLinkAnalysisCounts(0, 0, 0);

        LinkGrabberStatusText.Text = message;
        PackageSummaryText.Visibility = Visibility.Collapsed;
        PackageSummaryText.Text = string.Empty;
    }

    private void UpdatePackagePresentation()
    {
        if (_lastLinkPackages.Count == 0)
        {
            PackageSummaryText.Visibility = Visibility.Collapsed;
            PackageSummaryText.Text = string.Empty;
            QueueValidLinksButton.Content = "Add valid to queue";
            return;
        }

        int totalValid =
            _lastLinkPackages.Sum(package => package.ValidCount);

        string summary =
            _lastLinkPackages.Count == 1
                ? $"1 package: {_lastLinkPackages[0].Name} " +
                    $"({totalValid} links)"
                : $"{_lastLinkPackages.Count} packages: " +
                    string.Join(
                        ", ",
                        _lastLinkPackages.Select(
                            package =>
                                $"{package.Name} " +
                                $"({package.ValidCount})"));

        if (_settings.OrganizeIntoSiteFolders)
        {
            summary +=
                " · each package downloads into its own folder";
        }

        PackageSummaryText.Text = summary;
        PackageSummaryText.Visibility = Visibility.Visible;

        QueueValidLinksButton.Content =
            $"Add {totalValid} to queue · " +
            $"{_lastLinkPackages.Count} " +
            $"package{(_lastLinkPackages.Count == 1 ? "" : "s")}";
    }

    private void ClipboardWatchTimer_Tick(
        object? sender,
        EventArgs e)
    {
        if (
            _isClosing ||
            !_settings.ClipboardWatchEnabled ||
            !IsLoaded ||
            !string.IsNullOrWhiteSpace(LinksTextBox.Text))
        {
            return;
        }

        string clipboardText;

        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            clipboardText = Clipboard.GetText()?.Trim() ??
                string.Empty;
        }
        catch
        {
            return;
        }

        if (
            clipboardText.Length == 0 ||
            string.Equals(
                clipboardText,
                _lastClipboardText,
                StringComparison.Ordinal) ||
            !clipboardText.Contains(
                "http",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastClipboardText = clipboardText;

        LinkAnalysisResult result =
            LinkAnalyzer.Analyze(clipboardText);

        if (result.ValidCount == 0)
        {
            return;
        }

        LinksTextBox.Text = clipboardText;

        AnalyzeLinks_Click(this, new RoutedEventArgs());

        ApplicationStatusText.Text =
            $"{result.ValidCount} link" +
            $"{(result.ValidCount == 1 ? "" : "s")} " +
            "detected from the clipboard.";

        ShowPage("LinkGrabber");
    }

    private void QueueValidLinks_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_lastLinkAnalysis is null)
        {
            QueueValidLinksButton.IsEnabled = false;

            LinkGrabberStatusText.Text =
                "Analyze the current input before adding it to the queue.";

            return;
        }

        DownloadQueueImportResult result =
            _lastLinkPackages.Count > 0
                ? _downloadQueue.EnqueuePackages(
                    _lastLinkPackages,
                    _settings.OrganizeIntoSiteFolders,
                    _history.Contains)
                : _downloadQueue.Enqueue(
                    _lastLinkAnalysis,
                    _history.Contains);

        int packageCount = _lastLinkPackages.Count;

        LinkGrabberStatusText.Text =
            $"Queue updated: {result.AddedCount} added, " +
            $"{result.AlreadyQueuedCount} already queued, " +
            $"{result.IgnoredCount} invalid or duplicate" +
            (packageCount > 0
                ? $" · {packageCount} package" +
                    $"{(packageCount == 1 ? "" : "s")}."
                : ".");

        ApplicationStatusText.Text =
            $"Local queue updated - {result.AddedCount} added, " +
            $"{result.AlreadyQueuedCount} already queued; " +
            "no network activity";

        ShowPage("Downloads");
    }

    private void RemoveQueuedItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            sender is not Button button ||
            button.Tag is not Guid itemId
        )
        {
            return;
        }

        if (!_downloadQueue.Remove(itemId))
        {
            return;
        }

        ApplicationStatusText.Text =
            $"Queue item removed - {_downloadQueue.Count} remaining; " +
            "no network activity";
    }

    private void ClearDownloadQueue_Click(
        object sender,
        RoutedEventArgs e)
    {
        int removedCount = _downloadQueue.Clear();

        ApplicationStatusText.Text =
            $"Local queue cleared - {removedCount} removed; " +
            "no network activity";
    }

    private void ClearFinishedDownloads_Click(
        object sender,
        RoutedEventArgs e)
    {
        int removedCount = _downloadQueue.ClearCompleted();

        ApplicationStatusText.Text =
            removedCount > 0
                ? $"Finished downloads cleared - " +
                    $"{removedCount} removed."
                : "There are no finished downloads to clear.";
    }

    private void RetryQueuedItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            sender is not Button button ||
            button.Tag is not Guid itemId
        )
        {
            return;
        }

        if (!_downloadQueue.TryResetForRetry(itemId))
        {
            return;
        }

        ApplicationStatusText.Text =
            "Download re-queued - start the batch when ready.";
    }

    private void DownloadQueue_Changed(
        object? sender,
        EventArgs e)
    {
        ScheduleDownloadPresentationRefresh();
        ScheduleQueuePersist();
    }

    private void DownloadOrchestrator_RunningStateChanged(
        object? sender,
        EventArgs e)
    {
        ScheduleDownloadPresentationRefresh();
    }

    private void ScheduleDownloadPresentationRefresh()
    {
        if (
            _isClosing ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(
                ref _downloadPresentationRefreshPending,
                1) != 0
        )
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(
                    () =>
                    {
                        Interlocked.Exchange(
                            ref _downloadPresentationRefreshPending,
                            0);

                        if (
                            _isClosing ||
                            Dispatcher.HasShutdownStarted ||
                            Dispatcher.HasShutdownFinished
                        )
                        {
                            return;
                        }

                        RefreshDownloadQueue();
                    }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(
                ref _downloadPresentationRefreshPending,
                0);
        }
    }

    private async void StartAllDownloads_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_activeDownloadOrchestrator is not null)
        {
            return;
        }

        HttpClient? httpClient = null;
        DownloadOrchestrator? orchestrator = null;
        var batchChallenges =
            new List<ChallengeDetectedException>();
        bool restartAfterSolve = false;

        try
        {
            DownloadQueueItem[] startableItems =
                _downloadQueue.Items
                    .Where(
                        item =>
                            IsStartable(item.State))
                    .ToArray();

            if (startableItems.Length == 0)
            {
                ApplicationStatusText.Text =
                    "There are no queued, failed, or canceled " +
                    "downloads to start.";

                RefreshDownloadQueue();
                return;
            }

            string destinationDirectory =
                PrepareDestinationDirectory(
                    _settings.DownloadFolder);

            int maximumConcurrency =
                _settings.MaximumConcurrentDownloads;

            if (
                maximumConcurrency is < 1 or
                > MaximumSupportedConcurrency
            )
            {
                throw new InvalidOperationException(
                    "Maximum concurrent downloads must be " +
                    $"between 1 and " +
                    $"{MaximumSupportedConcurrency}.");
            }

            httpClient =
                DownloadHttpClientFactory.Create(
                    _settings,
                    _challengeStore);

            orchestrator =
                new DownloadOrchestrator(
                    _downloadQueue,
                    new HttpDownloadTransferService(
                        httpClient,
                        new IHostFileResolver[]
                        {
                            new PixeldrainResolver(),
                            new GofileResolver(),
                            new MediaFireResolver()
                        },
                        new MegaDownloadService()));

            _activeDownloadHttpClient = httpClient;
            _activeDownloadOrchestrator = orchestrator;
            _cancellationRequested = false;

            orchestrator.RunningStateChanged +=
                DownloadOrchestrator_RunningStateChanged;

            EventHandler<ChallengeDetectedException>
                challengeHandler = (_, challenge) =>
                {
                    lock (batchChallenges)
                    {
                        batchChallenges.Add(challenge);
                    }
                };

            orchestrator.ChallengeBlocked += challengeHandler;

            ApplicationStatusText.Text =
                $"Starting {startableItems.Length} download" +
                $"{(startableItems.Length == 1 ? "" : "s")} " +
                $"with up to {maximumConcurrency} concurrent " +
                "transfer" +
                $"{(maximumConcurrency == 1 ? "" : "s")}...";

            Task<DownloadBatchResult> batchTask =
                orchestrator.StartAllAsync(
                    destinationDirectory,
                    maximumConcurrency);

            RefreshDownloadQueue();

            DownloadBatchResult result;

            try
            {
                result = await batchTask;
            }
            finally
            {
                orchestrator.ChallengeBlocked -= challengeHandler;
            }

            if (!_isClosing)
            {
                ApplicationStatusText.Text =
                    CreateBatchStatusText(result);

                await TryWriteDownloadLogAsync(
                    $"Download batch finished. " +
                    $"Selected: {result.SelectedCount}; " +
                    $"started: {result.StartedCount}; " +
                    $"completed: {result.CompletedCount}; " +
                    $"failed: {result.FailedCount}; " +
                    $"canceled: {result.CanceledCount}; " +
                    $"not started: {result.NotStartedCount}.");

                restartAfterSolve =
                    await OfferChallengeSolvesAsync(batchChallenges);
            }
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                ApplicationStatusText.Text =
                    "Downloads could not be started.";

                await TryWriteDownloadLogAsync(
                    "A download batch could not be started.",
                    exception);

                MessageBox.Show(
                    this,
                    exception.Message,
                    "Downloads could not be started",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (orchestrator is not null)
            {
                orchestrator.RunningStateChanged -=
                    DownloadOrchestrator_RunningStateChanged;
            }

            if (
                ReferenceEquals(
                    _activeDownloadOrchestrator,
                    orchestrator)
            )
            {
                _activeDownloadOrchestrator = null;
            }

            if (
                ReferenceEquals(
                    _activeDownloadHttpClient,
                    httpClient)
            )
            {
                _activeDownloadHttpClient = null;
            }

            orchestrator?.Dispose();
            httpClient?.Dispose();

            _cancellationRequested = false;

            await PersistQueueAsync();
            await RecordCompletedHistoryAsync();

            if (!_isClosing)
            {
                RefreshDownloadQueue();
            }
        }

        if (restartAfterSolve && !_isClosing)
        {
            StartAllDownloads_Click(sender, e);
        }
    }

    /// <summary>
    /// Offers the solve window once per challenged host. Solved
    /// hosts get their failed items requeued; returns true when a
    /// solve landed and the batch should restart on its own.
    /// </summary>
    private async Task<bool> OfferChallengeSolvesAsync(
        List<ChallengeDetectedException> batchChallenges)
    {
        List<ChallengeDetectedException> distinct;

        lock (batchChallenges)
        {
            distinct = batchChallenges
                .GroupBy(challenge => challenge.Host.ToLowerInvariant())
                .Select(group => group.First())
                .ToList();
        }

        bool anySolved = false;

        foreach (ChallengeDetectedException challenge in distinct)
        {
            if (_isClosing)
            {
                break;
            }

            if (!challenge.IsSolvableInBrowser)
            {
                continue;
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                challenge.Message + " Solve it now in the app browser?",
                $"{challenge.Host} needs a check",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                continue;
            }

            var solveWindow = new ChallengeSolveWindow(
                challenge.PageUrl,
                challenge.Host,
                _challengeStore)
            {
                Owner = this
            };

            bool? solved = solveWindow.ShowDialog();

            if (solved != true || !solveWindow.SessionSaved)
            {
                continue;
            }

            await _log.InformationAsync(
                $"Challenge solved for {challenge.Host} " +
                $"({solveWindow.SavedCookieCount} cookies). " +
                "Requeuing blocked items.");

            RequeueHostFailures(challenge.Host);
            anySolved = true;
        }

        return anySolved;
    }

    private async Task<PageCrawlResult> FetchAndExtractAsync(
        Uri page)
    {
        using HttpClient client =
            DownloadHttpClientFactory.Create(
                _settings,
                _challengeStore);

        var crawler = new PageLinkCrawler(client);

        return await crawler.ExtractFromPagesAsync(
            page.AbsoluteUri);
    }

    /// <summary>
    /// Prompts for and runs the solve window. Returns true when a
    /// session was saved and the caller should retry once.
    /// </summary>
    private async Task<bool> SolveChallengeAsync(
        ChallengeDetectedException challenge)
    {
        // Kept async for symmetry with the batch flow; the dialog
        // itself is synchronous.
        await Task.CompletedTask;

        MessageBoxResult answer = MessageBox.Show(
            this,
            challenge.Message + " Solve it now in the app browser?",
            $"{challenge.Host} needs a check",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        var solveWindow = new ChallengeSolveWindow(
            challenge.PageUrl,
            challenge.Host,
            _challengeStore)
        {
            Owner = this
        };

        return solveWindow.ShowDialog() == true &&
            solveWindow.SessionSaved;
    }

    private void RequeueHostFailures(string host)
    {
        foreach (DownloadQueueItem item in _downloadQueue.Items)
        {
            if (
                item.State !=
                    DownloadQueueItemState.Failed ||
                !Uri.TryCreate(
                    item.Url,
                    UriKind.Absolute,
                    out Uri? uri) ||
                !uri.Host.Equals(
                    host,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _downloadQueue.TryResetForRetry(item.Id);
        }

        RefreshDownloadQueue();
    }

    private void CancelAllDownloads_Click(
        object sender,
        RoutedEventArgs e)
    {
        DownloadOrchestrator? orchestrator =
            _activeDownloadOrchestrator;

        if (orchestrator is null)
        {
            RefreshDownloadQueue();
            return;
        }

        if (!orchestrator.Cancel())
        {
            RefreshDownloadQueue();
            return;
        }

        _cancellationRequested = true;

        ApplicationStatusText.Text =
            "Cancel requested. Active transfers are stopping...";

        RefreshDownloadQueue();
    }

    private void RefreshDownloadQueue()
    {
        IReadOnlyList<DownloadQueueItem> items =
            _downloadQueue.Items;

        SynchronizeVisibleDownloadItems(items);

        int itemCount = items.Count;
        bool hasItems = itemCount > 0;

        bool batchActive =
            _activeDownloadOrchestrator is not null;

        bool orchestratorRunning =
            _activeDownloadOrchestrator?.IsRunning ==
            true;

        bool hasStartableItems =
            items.Any(
                item =>
                    IsStartable(item.State));

        DownloadQueueEmptyState.Visibility =
            hasItems
                ? Visibility.Collapsed
                : Visibility.Visible;

        DownloadQueueContent.Visibility =
            hasItems
                ? Visibility.Visible
                : Visibility.Collapsed;

        ClearDownloadQueueButton.IsEnabled =
            hasItems &&
            !batchActive;

        ClearFinishedDownloadsButton.IsEnabled =
            !batchActive &&
            items.Any(
                item =>
                    item.State
                        is DownloadQueueItemState.Completed or
                        DownloadQueueItemState.Failed or
                        DownloadQueueItemState.Canceled);

        StartAllDownloadsButton.IsEnabled =
            hasStartableItems &&
            !batchActive;

        CancelAllDownloadsButton.IsEnabled =
            orchestratorRunning &&
            !_cancellationRequested;

        CancelAllDownloadsButton.Content =
            _cancellationRequested
                ? "Canceling..."
                : "Cancel all";

        DownloadQueueCountText.Text =
            itemCount == 1
                ? "1 item"
                : $"{itemCount} items";
    }

    private void SynchronizeVisibleDownloadItems(
        IReadOnlyList<DownloadQueueItem> items)
    {
        bool sameIdentitySequence =
            _visibleDownloadItems.Count ==
                items.Count;

        if (sameIdentitySequence)
        {
            for (
                int index = 0;
                index < items.Count;
                index++
            )
            {
                if (
                    _visibleDownloadItems[index].Id !=
                    items[index].Id
                )
                {
                    sameIdentitySequence = false;
                    break;
                }
            }
        }

        if (!sameIdentitySequence)
        {
            _visibleDownloadItems.Clear();

            foreach (DownloadQueueItem item in items)
            {
                _visibleDownloadItems.Add(item);
            }

            return;
        }

        for (
            int index = 0;
            index < items.Count;
            index++
        )
        {
            if (
                !Equals(
                    _visibleDownloadItems[index],
                    items[index])
            )
            {
                _visibleDownloadItems[index] =
                    items[index];
            }
        }
    }

    private static bool IsStartable(
        DownloadQueueItemState state)
    {
        return state is
            DownloadQueueItemState.Queued or
            DownloadQueueItemState.Failed or
            DownloadQueueItemState.Canceled;
    }

    private static string PrepareDestinationDirectory(
        string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            throw new InvalidOperationException(
                "Choose and save a default download folder " +
                "before starting downloads.");
        }

        try
        {
            string expandedDirectory =
                Environment.ExpandEnvironmentVariables(
                    configuredDirectory.Trim());

            string fullPath =
                Path.GetFullPath(expandedDirectory);

            Directory.CreateDirectory(fullPath);

            return fullPath;
        }
        catch (Exception exception)
            when (
                exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException
            )
        {
            throw new InvalidOperationException(
                "The saved download folder is invalid or " +
                "cannot be created.",
                exception);
        }
    }

    private static string CreateBatchStatusText(
        DownloadBatchResult result)
    {
        if (result.SelectedCount == 0)
        {
            return "No startable downloads were found.";
        }

        string status =
            $"Download batch finished - " +
            $"{result.CompletedCount} completed, " +
            $"{result.FailedCount} failed, " +
            $"{result.CanceledCount} canceled";

        if (result.NotStartedCount > 0)
        {
            status +=
                $", {result.NotStartedCount} not started";
        }

        return status;
    }

    private async Task TryWriteDownloadLogAsync(
        string message,
        Exception? exception = null)
    {
        try
        {
            if (exception is null)
            {
                await _log.InformationAsync(message);
            }
            else
            {
                await _log.ErrorAsync(
                    message,
                    exception);
            }
        }
        catch
        {
            // Logging must not change an already-established
            // download or queue result.
        }
    }

    private void ScheduleQueuePersist()
    {
        if (_isClosing)
        {
            return;
        }

        if (_activeDownloadOrchestrator is not null &&
            DateTimeOffset.UtcNow - _lastQueuePersistUtc <
                TimeSpan.FromSeconds(5))
        {
            return;
        }

        _ = PersistQueueAsync();
    }

    private async Task PersistQueueAsync()
    {
        if (!await _queuePersistGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await _queueStore.SaveAsync(_downloadQueue.Items);
            _lastQueuePersistUtc = DateTimeOffset.UtcNow;
        }
        catch
        {
        }
        finally
        {
            _queuePersistGate.Release();
        }
    }

    private async Task RestoreHistoryAsync()
    {
        try
        {
            _history = await _historyStore.LoadAsync();

            if (!_isClosing)
            {
                UpdateHistoryPresentation();
            }
        }
        catch
        {
        }
    }

    /// <returns>True when at least one new entry was recorded.</returns>
    private bool RecordCompletedItems()
    {
        bool added = false;

        foreach (DownloadQueueItem item in _downloadQueue.Items)
        {
            if (
                item.State !=
                    DownloadQueueItemState.Completed ||
                string.IsNullOrWhiteSpace(item.Url))
            {
                continue;
            }

            added |= _history.Record(
                new DownloadHistoryEntry(
                    item.Url,
                    item.SavedFileName ?? item.DisplayName,
                    item.TotalBytes,
                    DateTimeOffset.UtcNow));
        }

        return added;
    }

    /// <summary>
    /// Records finished queue items into permanent history.
    /// Called after batches and on close; completed items cleared
    /// from the queue stay recognized as already downloaded.
    /// </summary>
    private async Task RecordCompletedHistoryAsync()
    {
        try
        {
            bool added = RecordCompletedItems();

            if (added)
            {
                await _historyStore.SaveAsync(_history);
            }

            if (!_isClosing)
            {
                UpdateHistoryPresentation();
            }
        }
        catch
        {
        }
    }

    private void UpdateHistoryPresentation()
    {
        if (HistoryStatusText is null)
        {
            return;
        }

        HistoryStatusText.Text =
            _history.Count == 0
                ? "No finished downloads remembered yet."
                : $"{_history.Count} finished download" +
                    $"{(_history.Count == 1 ? "" : "s")} remembered. " +
                    "Re-added links are skipped as duplicates.";
    }

    private async void ClearHistory_Click(
        object sender,
        RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Forget all {_history.Count} remembered downloads? " +
            "Already-downloaded links can then be queued again.",
            "Clear download history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _history.Clear();
        await _historyStore.SaveAsync(_history);
        UpdateHistoryPresentation();

        ApplicationStatusText.Text = "Download history cleared.";
    }

    private async Task RestoreDownloadQueueAsync()
    {
        try
        {
            IReadOnlyList<DownloadQueueItem> snapshot =
                await _queueStore.LoadAsync();

            if (snapshot.Count == 0 || _isClosing)
            {
                return;
            }

            _downloadQueue.RestoreSnapshot(snapshot);
        }
        catch
        {
        }
    }

    private void MainWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        _isClosing = true;
        _clipboardWatchTimer.Stop();

        _activeDownloadOrchestrator?.Cancel();

        try
        {
            _queuePersistGate.Wait();

            try
            {
                _queueStore.SaveAsync(
                    _downloadQueue.Items).GetAwaiter().GetResult();
            }
            finally
            {
                _queuePersistGate.Release();
            }

            RecordCompletedItems();
            _historyStore.SaveAsync(_history).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private void MainWindow_Closed(
        object? sender,
        EventArgs e)
    {
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

            _settings.MaximumConcurrentDownloads =
                maximumConcurrency;
            _settings.YtDlpPath = YtDlpPathTextBox.Text.Trim();
            _settings.FFmpegPath = FFmpegPathTextBox.Text.Trim();
            _settings.YtDlpCookiesFromBrowser =
                YtDlpCookiesTextBox.Text.Trim();
            _settings.ExternalToolsAutoUpdate =
                AutoToolsCheckBox.IsChecked != false;
            _settings.OrganizeIntoSiteFolders =
                OrganizeFoldersCheckBox.IsChecked != false;
            _settings.ClipboardWatchEnabled =
                ClipboardWatchCheckBox.IsChecked != false;
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

            // Immediate feedback: bad host/port, SOCKS, or an
            // undecryptable password fails here, not mid-batch.
            DownloadHttpClientFactory.Validate(_settings);

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
                selectedType is ProxyType.Socks4 or ProxyType.Socks5
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
}
