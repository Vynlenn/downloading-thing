using System.IO;
using System.Windows;
using DeezNutzDownloader.Adapters.Media;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.App;

/// <summary>
/// Archive extraction after a batch (or from the context menu):
/// ZIP built in, RAR/7z through an installed 7-Zip, with a password
/// prompt for protected archives. Both paths apply the same safety
/// rules and carry the Mark of the Web onto extracted files.
/// </summary>
public partial class MainWindow
{
    private const int MaximumPasswordAttempts = 3;

    private static bool IsExtractable(DownloadQueueItem item)
    {
        return
            item.State == DownloadQueueItemState.Completed &&
            item.DestinationPath is string path &&
            File.Exists(path) &&
            (ArchiveExtraction.IsSupportedArchive(path) ||
                SevenZipArchives.IsSupported(path));
    }

    /// <summary>
    /// Offers extraction for archives this batch finished (not every
    /// archive ever downloaded). AskBeforeArchiveExtraction=true
    /// prompts; false extracts silently.
    /// </summary>
    private async Task OfferArchiveExtractionAsync(
        IReadOnlySet<Guid> finishedInBatch)
    {
        List<string> archives = _downloadQueue.Items
            .Where(item => finishedInBatch.Contains(item.Id) && IsExtractable(item))
            .Select(item => item.DestinationPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (archives.Count == 0)
        {
            return;
        }

        if (_settings.AskBeforeArchiveExtraction)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"{archives.Count} archive" +
                $"{(archives.Count == 1 ? "" : "s")} finished. Extract now?",
                "Extract archives",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await ExtractArchivesAsync(archives);
    }

    private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        await ExtractArchivesAsync(
            TargetItems(sender)
                .Where(IsExtractable)
                .Select(item => item.DestinationPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private async Task ExtractArchivesAsync(IReadOnlyList<string> archives)
    {
        if (archives.Count == 0)
        {
            return;
        }

        int files = 0;
        int skipped = 0;
        int failed = 0;
        bool missingSevenZip = false;
        string? lastError = null;

        string? sevenZip = SevenZipArchives.FindExecutable(_settings.SevenZipPath);

        foreach (string archive in archives)
        {
            ApplicationStatusText.Text = $"Extracting {Path.GetFileName(archive)}…";

            try
            {
                ArchiveExtractionResult result;

                if (ArchiveExtraction.IsSupportedArchive(archive))
                {
                    result = await Task.Run(() => ArchiveExtraction.ExtractZip(archive));
                }
                else if (sevenZip is null)
                {
                    missingSevenZip = true;
                    continue;
                }
                else
                {
                    result = await ExtractWithPasswordPromptAsync(
                        new SevenZipArchives(sevenZip),
                        archive);
                }

                files += result.ExtractedCount;
                skipped += result.SkippedCount;
            }
            catch (OperationCanceledException)
            {
                // The user closed the password prompt.
                skipped++;
            }
            catch (Exception exception)
            {
                failed++;
                lastError = $"{Path.GetFileName(archive)}: {exception.Message}";
                await TryWriteDownloadLogAsync("Archive extraction failed.", exception);
            }
        }

        string status =
            $"Extracted {files} file{(files == 1 ? "" : "s")}" +
            (skipped > 0 ? $" ({skipped} skipped)" : string.Empty) + ".";

        if (failed > 0)
        {
            status += $" {failed} archive{(failed == 1 ? "" : "s")} failed - {lastError}";
        }

        if (missingSevenZip)
        {
            status +=
                " RAR/7z need 7-Zip: install it from 7-zip.org or set its path in Settings.";
        }

        ApplicationStatusText.Text = status;

        await TryWriteDownloadLogAsync(
            $"Archive extraction finished. Files: {files}; skipped: {skipped}; failed: {failed}.");
    }

    private async Task<ArchiveExtractionResult> ExtractWithPasswordPromptAsync(
        SevenZipArchives sevenZip,
        string archive)
    {
        string? password = null;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await sevenZip.ExtractAsync(archive, password);
            }
            catch (ArchivePasswordRequiredException required)
                when (attempt < MaximumPasswordAttempts)
            {
                password = PasswordPromptWindow.Ask(
                    this,
                    Path.GetFileName(archive),
                    required.WrongPassword);

                if (password is null)
                {
                    throw new OperationCanceledException();
                }
            }
        }
    }
}
