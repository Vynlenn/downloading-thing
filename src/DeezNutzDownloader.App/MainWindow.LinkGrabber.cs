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

            string? subtitleLangs =
                string.IsNullOrWhiteSpace(
                    _settings.YtDlpSubtitleLangs)
                    ? null
                    : _settings.YtDlpSubtitleLangs.Trim();

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

            var formatDialog = new VideoFormatDialog(probe)
            {
                Owner = this
            };

            if (formatDialog.ShowDialog() != true)
            {
                LinkGrabberStatusText.Text =
                    "Video grab canceled — nothing was downloaded.";

                return;
            }

            string? formatId = formatDialog.SelectedFormatId;
            bool grabPlaylist = formatDialog.DownloadPlaylist;

            LinkGrabberStatusText.Text = grabPlaylist
                ? $"Downloading playlist: {probe.Title}…"
                : $"Downloading{(formatId is null ? " best quality" : " selected format")}: {probe.Title}…";

            ApplicationStatusText.Text =
                $"Video download started - {probe.Title}";

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
                        formatId,
                        _settings.DownloadFolder,
                        ytDlpPath,
                        ffmpegPath,
                        progress,
                        CancellationToken.None,
                        proxyUrl,
                        browserCookies,
                        grabPlaylist,
                        ResolveBatchSpeedCap(),
                        subtitleLangs));

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

        // A running batch picks up new links right away.
        if (result.AddedCount > 0 && _activeDownloadOrchestrator is not null)
        {
            _ = StartBatchAsync(onlyItems: null);
        }

        ShowPage("Downloads");
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

}

