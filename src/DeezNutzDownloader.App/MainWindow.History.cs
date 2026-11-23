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
}
