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

    private void ClearDownloadQueue_Click(
        object sender,
        RoutedEventArgs e)
    {
        DownloadQueueItem[] before = _downloadQueue.Items.ToArray();
        int removedCount = _downloadQueue.Clear();

        if (removedCount > 0)
        {
            DeletePartialFiles(before);
        }

        ApplicationStatusText.Text =
            $"Local queue cleared - {removedCount} removed; " +
            "no network activity";
    }

    private void ClearFinishedDownloads_Click(
        object sender,
        RoutedEventArgs e)
    {
        DownloadQueueItem[] before = _downloadQueue.Items.ToArray();
        int removedCount = _downloadQueue.ClearCompleted();

        if (removedCount > 0)
        {
            HashSet<Guid> remaining =
                _downloadQueue.Items.Select(item => item.Id).ToHashSet();

            DeletePartialFiles(before.Where(item => !remaining.Contains(item.Id)));
        }

        ApplicationStatusText.Text =
            removedCount > 0
                ? $"Finished downloads cleared - " +
                    $"{removedCount} removed."
                : "There are no finished downloads to clear.";
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

    private Task<DownloadBatchResult>? _activeBatchTask;

    private async void StartAllDownloads_Click(
        object sender,
        RoutedEventArgs e)
    {
        await StartBatchAsync(onlyItems: null);
    }

    /// <summary>
    /// Starts a batch for every startable item, or only for
    /// <paramref name="onlyItems"/>. While a batch runs, the items are
    /// added to it instead, so Start all and Resume always work.
    /// </summary>
    private async Task StartBatchAsync(IReadOnlyCollection<Guid>? onlyItems)
    {
        if (_activeDownloadOrchestrator is DownloadOrchestrator running)
        {
            int added = 0;

            foreach (DownloadQueueItem item in _downloadQueue.Items)
            {
                if (
                    IsStartable(item.State) &&
                    (onlyItems is null || onlyItems.Contains(item.Id)) &&
                    running.TryAddToBatch(item.Id))
                {
                    added++;
                }
            }

            ApplicationStatusText.Text = added > 0
                ? $"Added {added} item{(added == 1 ? "" : "s")} to the running batch."
                : "Nothing new to add to the running batch.";

            RefreshDownloadQueue();
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
                            IsStartable(item.State) &&
                            (onlyItems is null || onlyItems.Contains(item.Id)))
                    .ToArray();

            if (startableItems.Length == 0)
            {
                ApplicationStatusText.Text =
                    "There are no queued, failed, or canceled " +
                    "downloads to start.";

                RefreshDownloadQueue();
                return;
            }

            // Folder shares (Gofile /d/, Pixeldrain /l/) expand to
            // one queue item per file before the batch starts.
            // MEGA folders stay single-row (child keys never leave
            // the folder session) and download every file inside.
            await ExpandFolderSharesAsync(
                startableItems,
                batchChallenges);

            // Folder rows may have been replaced by per-file rows.
            startableItems = _downloadQueue.Items
                .Where(item =>
                    IsStartable(item.State) &&
                    (onlyItems is null ||
                        onlyItems.Contains(item.Id) ||
                        startableItems.All(previous => previous.Id != item.Id)))
                .ToArray();

            if (startableItems.Length == 0)
            {
                ApplicationStatusText.Text =
                    "Folder expansion replaced every queued item — " +
                    "press Start again to download the files.";

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

            long? batchCap = ResolveBatchSpeedCap();

            ApplicationStatusText.Text =
                $"Starting {startableItems.Length} download" +
                $"{(startableItems.Length == 1 ? "" : "s")} " +
                $"with up to {maximumConcurrency} concurrent " +
                "transfer" +
                $"{(maximumConcurrency == 1 ? "" : "s")}" +
                (batchCap.HasValue
                    ? $" at quiet-hours {QuietHoursSchedule.FormatRate(batchCap.Value)}"
                    : string.Empty) +
                "...";

            Task<DownloadBatchResult> batchTask =
                orchestrator.StartAllAsync(
                    destinationDirectory,
                    maximumConcurrency,
                    CancellationToken.None,
                    batchCap,
                    _settings.ConnectionsPerDownload,
                    onlyItems is null
                        ? null
                        : startableItems.Select(item => item.Id).ToArray());

            _activeBatchTask = batchTask;

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
                    CreateBatchStatusText(result) +
                    CreateSafetySummary(startableItems);

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

                NotifyBatchFinished(result);

                if (!restartAfterSolve)
                {
                    await OfferArchiveExtractionAsync(
                        _downloadQueue.Items
                            .Where(item =>
                                item.State == DownloadQueueItemState.Completed &&
                                startableItems.Any(started => started.Id == item.Id))
                            .Select(item => item.Id)
                            .ToHashSet());

                    await OfferShutdownIfRequestedAsync(result);
                }
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
            _activeBatchTask = null;

            await PersistQueueAsync();
            await RecordCompletedHistoryAsync();

            if (!_isClosing)
            {
                RefreshDownloadQueue();
            }
        }

        if (restartAfterSolve && !_isClosing)
        {
            await StartBatchAsync(onlyItems: null);
        }
    }

    /// <summary>
    /// Quiet-hours cap for this batch (local clock), or null for
    /// full speed.
    /// </summary>
    private long? ResolveBatchSpeedCap()
    {
        if (!_settings.QuietHoursEnabled)
        {
            return null;
        }

        var schedule = new QuietHoursSchedule(
            true,
            new TimeSpan(
                _settings.QuietHoursStartHour,
                _settings.QuietHoursStartMinute,
                0),
            new TimeSpan(
                _settings.QuietHoursEndHour,
                _settings.QuietHoursEndMinute,
                0),
            (long)_settings.QuietHoursCapKBps * 1024L);

        return schedule.EffectiveCapBytesPerSecond(
            TimeOnly.FromDateTime(DateTime.Now));
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

    /// <summary>
    /// Expands folder-share rows into per-file rows. The folder row
    /// is removed only when expansion yields files; failures leave
    /// the row for the transfer-time resolver to report, and
    /// challenges join the batch solve flow.
    /// </summary>
    private async Task ExpandFolderSharesAsync(
        DownloadQueueItem[] startableItems,
        List<ChallengeDetectedException> batchChallenges)
    {
        using HttpClient client =
            DownloadHttpClientFactory.Create(
                _settings,
                _challengeStore);

        var folders = new IHostFolderResolver[]
        {
            new GofileResolver(),
            new PixeldrainResolver(),
        };

        int expandedFolders = 0;
        int expandedFiles = 0;

        foreach (DownloadQueueItem item in startableItems)
        {
            if (_isClosing)
            {
                break;
            }

            if (
                !Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? page))
            {
                continue;
            }

            IHostFolderResolver? resolver = folders.FirstOrDefault(
                candidate => candidate.CanResolveFolder(page));

            if (resolver is null)
            {
                continue;
            }

            ResolvedHostFolder folder;

            try
            {
                folder = await resolver.ResolveFolderAsync(
                    page,
                    client);
            }
            catch (ChallengeDetectedException challenge)
            {
                lock (batchChallenges)
                {
                    batchChallenges.Add(challenge);
                }

                continue;
            }
            catch
            {
                continue;
            }

            if (folder.Files.Count == 0)
            {
                continue;
            }

            DownloadQueueImportResult added =
                _downloadQueue.EnqueueHostFiles(
                    folder.FolderName,
                    folder.Files,
                    _history.Contains);

            if (added.AddedCount == 0)
            {
                continue;
            }

            _downloadQueue.Remove(item.Id);
            expandedFolders++;
            expandedFiles += added.AddedCount;

            await PersistQueueAsync();
        }

        if (expandedFolders > 0)
        {
            RefreshDownloadQueue();

            ApplicationStatusText.Text =
                $"Expanded {expandedFolders} folder" +
                $"{(expandedFolders == 1 ? "" : "s")} into " +
                $"{expandedFiles} file" +
                $"{(expandedFiles == 1 ? "" : "s")}. Starting…";

            await _log.InformationAsync(
                $"Folder expansion: {expandedFolders} folders, " +
                $"{expandedFiles} files.");
        }
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
            "Cancel requested. Active transfers are stopping; " +
            "their progress is kept, so Resume continues where they stopped.";

        RefreshDownloadQueue();
    }

    private void RefreshDownloadQueue()
    {
        IReadOnlyList<DownloadQueueItem> items =
            _downloadQueue.Items;

        SynchronizeVisibleDownloadItems(ApplyQueueFilter(items));
        UpdateQueueChrome(items);

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

        // While a batch runs, Start all adds anything new to it.
        StartAllDownloadsButton.IsEnabled =
            hasStartableItems;

        CancelAllDownloadsButton.IsEnabled =
            orchestratorRunning &&
            !_cancellationRequested;

        CancelAllDownloadsButton.Content =
            _cancellationRequested
                ? "Canceling..."
                : "Cancel all";

        // Item count and summary: UpdateQueueChrome (filter-aware).
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

    /// <summary>
    /// One line after a batch when files it finished can run code,
    /// so the per-row warnings are not missed on a long queue.
    /// </summary>
    private string CreateSafetySummary(
        IEnumerable<DownloadQueueItem> batchItems)
    {
        var batchIds = batchItems
            .Select(item => item.Id)
            .ToHashSet();

        DownloadQueueItem[] flagged = _downloadQueue.Items
            .Where(item =>
                batchIds.Contains(item.Id) &&
                item.State == DownloadQueueItemState.Completed &&
                item.Safety.Level != FileSafetyLevel.None)
            .ToArray();

        if (flagged.Length == 0)
        {
            return string.Empty;
        }

        int disguised = flagged.Count(item => item.IsDangerous);

        string summary =
            $". {flagged.Length} downloaded file" +
            (flagged.Length == 1 ? " can" : "s can") +
            " run code when opened";

        if (disguised > 0)
        {
            summary +=
                $" ({disguised} disguised as " +
                (disguised == 1 ? "a document or media file" : "documents or media files") +
                ")";
        }

        return summary + " - check the warnings in the queue first.";
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

}

