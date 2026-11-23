using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.App.Networking;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.App;

/// <summary>
/// Queue interaction: selection, filtering, per-row and bulk actions,
/// opening finished files, link checks, and the context menu.
/// </summary>
public partial class MainWindow
{
    // Rows are immutable records replaced on every progress tick, so
    // selection is tracked by id and re-applied after each refresh.
    private readonly HashSet<Guid> _selectedQueueIds = new();
    private bool _syncingQueueSelection;
    private CancellationTokenSource? _linkCheckCancellation;

    // ---------- Filtering, summary, selection ----------

    private IReadOnlyList<DownloadQueueItem> ApplyQueueFilter(
        IReadOnlyList<DownloadQueueItem> items)
    {
        QueueFilterKind kind =
            QueueFilterComboBox?.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse(tag, out QueueFilterKind parsed)
                ? parsed
                : QueueFilterKind.All;

        return QueueViews.Filter(items, QueueSearchTextBox?.Text, kind);
    }

    private void UpdateQueueChrome(IReadOnlyList<DownloadQueueItem> items)
    {
        RestoreQueueSelection();

        QueueSummaryText.Text =
            QueueViews.FormatSummary(QueueViews.Summarize(items));

        int visible = _visibleDownloadItems.Count;

        DownloadQueueHintText.Text =
            visible == 0 && items.Count > 0
                ? "No items match the search or filter."
                : "Select rows (Ctrl/Shift for several), right-click for more. " +
                  "Paused and canceled downloads keep their progress.";

        DownloadQueueCountText.Text =
            visible == items.Count
                ? (items.Count == 1 ? "1 item" : $"{items.Count} items")
                : $"{visible} of {items.Count} shown";

        CheckLinksButton.IsEnabled =
            _linkCheckCancellation is null &&
            items.Any(item => item.State != DownloadQueueItemState.Completed);

        CheckLinksButton.Content =
            _linkCheckCancellation is null ? "Check links" : "Checking…";

        UpdateSelectionButtons();
    }

    private void RestoreQueueSelection()
    {
        if (_selectedQueueIds.Count == 0)
        {
            return;
        }

        _syncingQueueSelection = true;

        try
        {
            foreach (DownloadQueueItem item in _visibleDownloadItems)
            {
                if (
                    _selectedQueueIds.Contains(item.Id) &&
                    !DownloadQueueList.SelectedItems.Contains(item))
                {
                    DownloadQueueList.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _syncingQueueSelection = false;
        }
    }

    private void DownloadQueueList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingQueueSelection)
        {
            return;
        }

        // Replacing a row record deselects it; that is not the user
        // changing the selection, so only explicit removals count.
        foreach (DownloadQueueItem added in e.AddedItems.OfType<DownloadQueueItem>())
        {
            _selectedQueueIds.Add(added.Id);
        }

        foreach (DownloadQueueItem removed in e.RemovedItems.OfType<DownloadQueueItem>())
        {
            bool stillVisible = _visibleDownloadItems.Any(item => item.Id == removed.Id);
            bool replacedRecord = stillVisible &&
                !_visibleDownloadItems.Contains(removed);

            if (!replacedRecord)
            {
                _selectedQueueIds.Remove(removed.Id);
            }
        }

        UpdateSelectionButtons();
    }

    private void QueueFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (DownloadQueueList is null)
        {
            return;
        }

        RefreshDownloadQueue();
    }

    private IReadOnlyList<DownloadQueueItem> SelectedQueueItems()
    {
        return _downloadQueue.Items
            .Where(item => _selectedQueueIds.Contains(item.Id))
            .ToArray();
    }

    /// <summary>The clicked row for row buttons; the selection otherwise.</summary>
    private IReadOnlyList<DownloadQueueItem> TargetItems(object sender)
    {
        if (
            sender is FrameworkElement { DataContext: DownloadQueueItem row } &&
            sender is not MenuItem)
        {
            DownloadQueueItem? current =
                _downloadQueue.Items.FirstOrDefault(item => item.Id == row.Id);

            return current is null
                ? Array.Empty<DownloadQueueItem>()
                : new[] { current };
        }

        return SelectedQueueItems();
    }

    private void UpdateSelectionButtons()
    {
        IReadOnlyList<DownloadQueueItem> selected = SelectedQueueItems();

        QueueSelectionText.Text = selected.Count switch
        {
            0 => string.Empty,
            1 => "1 selected",
            _ => $"{selected.Count} selected"
        };

        ResumeSelectedButton.IsEnabled = selected.Any(item => item.CanResume);
        PauseSelectedButton.IsEnabled = selected.Any(item => item.CanPause);
        RemoveSelectedButton.IsEnabled = selected.Any(item => item.CanRemove);
    }

    // ---------- Pause / resume / cancel / remove ----------

    private void PauseRow_Click(object sender, RoutedEventArgs e) =>
        PauseItems(TargetItems(sender));

    private void PauseSelected_Click(object sender, RoutedEventArgs e) =>
        PauseItems(TargetItems(sender));

    private async void ResumeRow_Click(object sender, RoutedEventArgs e) =>
        await ResumeItemsAsync(TargetItems(sender));

    private async void ResumeSelected_Click(object sender, RoutedEventArgs e) =>
        await ResumeItemsAsync(TargetItems(sender));

    private void RemoveRow_Click(object sender, RoutedEventArgs e) =>
        RemoveItems(TargetItems(sender));

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        RemoveItems(TargetItems(sender));

    private void PauseItems(IReadOnlyList<DownloadQueueItem> items)
    {
        int paused = 0;

        foreach (DownloadQueueItem item in items.Where(item => item.CanPause))
        {
            bool done = _activeDownloadOrchestrator?.PauseItem(item.Id) ??
                _downloadQueue.TryPause(item.Id);

            if (done)
            {
                paused++;
            }
        }

        ApplicationStatusText.Text = paused > 0
            ? $"Paused {paused} item{(paused == 1 ? "" : "s")}; progress is kept."
            : "Nothing to pause.";
    }

    private async Task ResumeItemsAsync(IReadOnlyList<DownloadQueueItem> items)
    {
        var resumed = new List<Guid>();

        foreach (DownloadQueueItem item in items.Where(item => item.CanResume))
        {
            if (_downloadQueue.TryResume(item.Id))
            {
                resumed.Add(item.Id);
            }
        }

        if (resumed.Count == 0)
        {
            ApplicationStatusText.Text = "Nothing to resume.";
            return;
        }

        // Joins the running batch, or starts one with just these.
        await StartBatchAsync(resumed);
    }

    private void CancelSelected_Click(object sender, RoutedEventArgs e)
    {
        int canceled = 0;

        foreach (DownloadQueueItem item in TargetItems(sender)
                     .Where(item => item.State == DownloadQueueItemState.Downloading))
        {
            if (_activeDownloadOrchestrator?.CancelItem(item.Id) == true)
            {
                canceled++;
            }
        }

        ApplicationStatusText.Text = canceled > 0
            ? $"Canceled {canceled} download{(canceled == 1 ? "" : "s")}; progress is kept for a retry."
            : "Only downloads in progress can be canceled.";
    }

    private void RemoveItems(IReadOnlyList<DownloadQueueItem> items)
    {
        var removed = new List<DownloadQueueItem>();

        foreach (DownloadQueueItem item in items.Where(item => item.CanRemove))
        {
            if (_downloadQueue.Remove(item.Id))
            {
                removed.Add(item);
                _selectedQueueIds.Remove(item.Id);
            }
        }

        DeletePartialFiles(removed);

        int skipped = items.Count - removed.Count;

        ApplicationStatusText.Text =
            $"Removed {removed.Count} item{(removed.Count == 1 ? "" : "s")}" +
            (skipped > 0 ? $"; {skipped} still downloading (pause or cancel them first)." : ".");

        UpdateSelectionButtons();
    }

    /// <summary>
    /// Deletes the kept partial file of items that leave the queue
    /// unfinished, so removed downloads do not leave hidden data.
    /// </summary>
    private void DeletePartialFiles(IEnumerable<DownloadQueueItem> items)
    {
        string root;

        try
        {
            root = PrepareDestinationDirectory(_settings.DownloadFolder);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (DownloadQueueItem item in items)
        {
            if (item.State == DownloadQueueItemState.Completed)
            {
                continue;
            }

            string directory =
                DownloadOrchestrator.ResolveItemDirectory(root, item.RelativeFolder);

            PartialDownloadFiles.Delete(directory, PartialDownloadFiles.KeyFor(item.Id));
        }
    }

    private async void DownloadInBrowser_Click(object sender, RoutedEventArgs e)
    {
        var queued = new List<Guid>();

        foreach (DownloadQueueItem item in TargetItems(sender)
                     .Where(item => item.State != DownloadQueueItemState.Downloading))
        {
            if (_downloadQueue.TryRequeueViaBrowser(item.Id))
            {
                queued.Add(item.Id);
            }
        }

        if (queued.Count == 0)
        {
            ApplicationStatusText.Text = "Pick rows that are not downloading right now.";
            return;
        }

        await StartBatchAsync(queued);
    }

    // ---------- Order ----------

    private void DownloadNext_Click(object sender, RoutedEventArgs e) =>
        MoveItems(TargetItems(sender), item => _downloadQueue.MoveToFront(item.Id), reverse: true);

    private void MoveUp_Click(object sender, RoutedEventArgs e) =>
        MoveItems(TargetItems(sender), item => _downloadQueue.Move(item.Id, -1), reverse: false);

    private void MoveDown_Click(object sender, RoutedEventArgs e) =>
        MoveItems(TargetItems(sender), item => _downloadQueue.Move(item.Id, 1), reverse: true);

    private void MoveToBottom_Click(object sender, RoutedEventArgs e) =>
        MoveItems(TargetItems(sender), item => _downloadQueue.MoveToBack(item.Id), reverse: false);

    private void MoveItems(
        IReadOnlyList<DownloadQueueItem> items,
        Func<DownloadQueueItem, bool> move,
        bool reverse)
    {
        // Moving a block keeps its internal order: front-moves go
        // last-to-first, back-moves first-to-last.
        IEnumerable<DownloadQueueItem> ordered = reverse ? items.Reverse() : items;
        int moved = ordered.Count(move);

        if (moved > 0)
        {
            ApplicationStatusText.Text =
                "Queue order updated; downloads follow the new order.";
        }
    }

    // ---------- Open / folder / copy ----------

    private void OpenRow_Click(object sender, RoutedEventArgs e) =>
        OpenItem(TargetItems(sender).FirstOrDefault());

    private void OpenSelected_Click(object sender, RoutedEventArgs e) =>
        OpenItem(TargetItems(sender).FirstOrDefault(item => item.CanOpen));

    private void ShowRowInFolder_Click(object sender, RoutedEventArgs e) =>
        ShowInFolder(TargetItems(sender).FirstOrDefault());

    private void ShowSelectedInFolder_Click(object sender, RoutedEventArgs e) =>
        ShowInFolder(TargetItems(sender).FirstOrDefault(item => item.CanOpen));

    private void DownloadQueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (
            e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(DownloadQueueList, source) is ListBoxItem
            {
                DataContext: DownloadQueueItem { CanOpen: true } item
            })
        {
            OpenItem(item);
        }
    }

    private void OpenItem(DownloadQueueItem? item)
    {
        if (item is not { CanOpen: true } || item.DestinationPath is not string path)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            StartShell("explorer.exe", $"\"{path}\"");
            return;
        }

        if (!File.Exists(path))
        {
            ApplicationStatusText.Text =
                $"{Path.GetFileName(path)} is no longer where it was saved.";
            return;
        }

        // Same name check as the queue badge: a disguised program
        // needs a deliberate yes, a plain one a quick confirmation.
        FileSafetyAssessment safety = FileTypeSafety.Assess(path);

        if (safety.Level != FileSafetyLevel.None)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                safety.Message + "\n\nOpen it anyway?",
                safety.Level == FileSafetyLevel.Danger
                    ? "Disguised file"
                    : "This file can run code",
                MessageBoxButton.YesNo,
                safety.Level == FileSafetyLevel.Danger
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            // Shell execute: Windows applies SmartScreen and the
            // Mark of the Web checks, exactly as for a browser file.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ApplicationStatusText.Text = $"Could not open the file: {exception.Message}";
        }
    }

    private void ShowInFolder(DownloadQueueItem? item)
    {
        if (item?.DestinationPath is not string path)
        {
            return;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            // Paths come from our own sanitized names: no quotes.
            StartShell("explorer.exe", $"/select,\"{path}\"");
        }
        else if (Path.GetDirectoryName(path) is string folder && Directory.Exists(folder))
        {
            StartShell("explorer.exe", $"\"{folder}\"");
        }
    }

    private void StartShell(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ApplicationStatusText.Text = $"Could not open Explorer: {exception.Message}";
        }
    }

    private void CopySelectedLinks_Click(object sender, RoutedEventArgs e)
    {
        string links = string.Join(
            Environment.NewLine,
            TargetItems(sender).Select(item => item.Url));

        if (links.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(links);
            ApplicationStatusText.Text = "Link copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ApplicationStatusText.Text = "The clipboard is busy; try again.";
        }
    }

    // ---------- Link checks ----------

    private async void CheckLinks_Click(object sender, RoutedEventArgs e) =>
        await CheckLinksAsync(
            _downloadQueue.Items
                .Where(item => item.State is not
                    (DownloadQueueItemState.Completed or DownloadQueueItemState.Downloading))
                .ToArray());

    private async void CheckSelectedLinks_Click(object sender, RoutedEventArgs e) =>
        await CheckLinksAsync(
            TargetItems(sender)
                .Where(item => item.State is not
                    (DownloadQueueItemState.Completed or DownloadQueueItemState.Downloading))
                .ToArray());

    private async Task CheckLinksAsync(IReadOnlyList<DownloadQueueItem> items)
    {
        if (items.Count == 0 || _linkCheckCancellation is not null)
        {
            return;
        }

        var links = new List<(Guid Id, Uri Url)>();

        foreach (DownloadQueueItem item in items)
        {
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? url))
            {
                links.Add((item.Id, url));
            }
        }

        _linkCheckCancellation = new CancellationTokenSource();
        RefreshDownloadQueue();

        int online = 0, offline = 0, attention = 0;

        try
        {
            using HttpClient client =
                DownloadHttpClientFactory.Create(_settings, _challengeStore);

            var checker = new LinkChecker(
                client,
                new IHostFileResolver[]
                {
                    new PixeldrainResolver(),
                    new GofileResolver(),
                    new MediaFireResolver()
                });

            ApplicationStatusText.Text =
                $"Checking {links.Count} link{(links.Count == 1 ? "" : "s")}…";

            await checker.CheckAllAsync(
                links,
                (id, result) =>
                {
                    _downloadQueue.SetLinkStatus(
                        id,
                        result.Status,
                        result.Status == LinkCheckStatus.Online ? null : result.Message,
                        result.FileName,
                        result.SizeBytes);

                    switch (result.Status)
                    {
                        case LinkCheckStatus.Online:
                            Interlocked.Increment(ref online);
                            break;
                        case LinkCheckStatus.Offline:
                            Interlocked.Increment(ref offline);
                            break;
                        case LinkCheckStatus.NeedsAttention:
                        case LinkCheckStatus.Limited:
                            Interlocked.Increment(ref attention);
                            break;
                    }
                },
                _linkCheckCancellation.Token);

            ApplicationStatusText.Text =
                $"Link check done: {online} online, {offline} offline, " +
                $"{attention} need attention" +
                (offline + attention > 0 ? " (filter: Link problems)." : ".");
        }
        catch (OperationCanceledException)
        {
            ApplicationStatusText.Text = "Link check stopped.";
        }
        catch (Exception exception)
        {
            ApplicationStatusText.Text = $"Link check failed: {exception.Message}";
        }
        finally
        {
            _linkCheckCancellation.Dispose();
            _linkCheckCancellation = null;

            if (!_isClosing)
            {
                RefreshDownloadQueue();
            }
        }
    }

    // ---------- Context menu ----------

    private void QueueContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<DownloadQueueItem> selected = SelectedQueueItems();
        bool any = selected.Count > 0;

        MenuOpenFile.IsEnabled = selected.Any(item => item.CanOpen);
        MenuShowInFolder.IsEnabled = selected.Any(item => item.CanOpen);
        MenuExtract.IsEnabled = selected.Any(IsExtractable);
        MenuResume.IsEnabled = selected.Any(item => item.CanResume);
        MenuPause.IsEnabled = selected.Any(item => item.CanPause);
        MenuCancel.IsEnabled =
            _activeDownloadOrchestrator is not null &&
            selected.Any(item => item.State == DownloadQueueItemState.Downloading);
        MenuViaBrowser.IsEnabled =
            selected.Any(item => item.State != DownloadQueueItemState.Downloading);
        MenuDownloadNext.IsEnabled = any;
        MenuMoveUp.IsEnabled = any;
        MenuMoveDown.IsEnabled = any;
        MenuMoveToBottom.IsEnabled = any;
        MenuCheckLink.IsEnabled =
            _linkCheckCancellation is null &&
            selected.Any(item => item.State is not
                (DownloadQueueItemState.Completed or DownloadQueueItemState.Downloading));
        MenuCopyLink.IsEnabled = any;
        MenuRemove.IsEnabled = selected.Any(item => item.CanRemove);
    }
}
