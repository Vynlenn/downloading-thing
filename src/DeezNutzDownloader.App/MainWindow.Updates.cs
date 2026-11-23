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
    private async void CheckUpdates_Click(
        object sender,
        RoutedEventArgs e)
    {
        // The saved repository is authoritative; the textbox may
        // hold unsaved edits, which Save first persists.
        string repository = _settings.UpdateRepository?.Trim() ??
            string.Empty;

        if (!UpdateCheck.IsValidRepository(repository))
        {
            UpdateStatusText.Text =
                "Set a GitHub repository (owner/name) in the box " +
                "above and save settings first.";

            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";

        try
        {
            using HttpClient client =
                DownloadHttpClientFactory.Create(_settings);

            var updater = new AppUpdater(client, repository);

            UpdateAvailable? update = await updater.CheckAsync();

            if (update is null)
            {
                UpdateStatusText.Text =
                    $"You're on the latest version " +
                    $"({AppUpdater.CurrentVersion()}).";

                return;
            }

            await OfferUpdateAsync(updater, update);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text =
                TrimMessage(exception.Message, 300) ??
                "The update check failed.";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Shows release notes and stages the update. The user
    /// confirms once; staging downloads + unpacks, then a script
    /// applies it after this process exits.
    /// </summary>
    private async Task OfferUpdateAsync(
        AppUpdater updater,
        UpdateAvailable update)
    {
        string notes = update.Notes.Trim();

        if (notes.Length > 800)
        {
            notes = notes[..800] + "…";
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Version {update.Version} is available " +
            $"(you have {AppUpdater.CurrentVersion()}).\n\n" +
            notes +
            "\n\nDownload and restart to install?",
            "Update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            UpdateStatusText.Text =
                $"Update {update.Version} skipped — " +
                "check again any time.";

            return;
        }

        UpdateStatusText.Text =
            $"Downloading update {update.Version}…";

        try
        {
            string script = await updater.StageAsync(
                update,
                new Progress<string>(line =>
                    UpdateStatusText.Text = line));

            MessageBoxResult restart = MessageBox.Show(
                this,
                "The update is ready. Restart now to apply it?",
                "Restart to update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (restart != MessageBoxResult.Yes)
            {
                UpdateStatusText.Text =
                    "Update staged — restart the app to apply it.";

                return;
            }

            AppUpdater.LaunchInstallerAndExit(script);
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text =
                TrimMessage(exception.Message, 300) ??
                "The update could not be staged.";
        }
    }

    private static string? TrimMessage(string? message, int maximum)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        string trimmed = message.Trim();

        return trimmed.Length <= maximum
            ? trimmed
            : trimmed[..maximum];
    }

    /// <summary>
    /// Silent startup check: only notes availability in Settings,
    /// never interrupts with a dialog.
    /// </summary>
    private async Task CheckUpdatesOnStartupAsync()
    {
        string repository = _settings.UpdateRepository?.Trim() ??
            string.Empty;

        if (
            !_settings.CheckForUpdatesOnStartup ||
            !UpdateCheck.IsValidRepository(repository))
        {
            return;
        }

        try
        {
            using HttpClient client =
                DownloadHttpClientFactory.Create(_settings);

            var updater = new AppUpdater(client, repository);

            UpdateAvailable? update = await updater.CheckAsync();

            if (
                update is not null &&
                !_isClosing &&
                UpdateStatusText is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                    UpdateStatusText.Text =
                        $"Version {update.Version} is available — " +
                        "use Check for updates now to install it.");
            }
        }
        catch
        {
            // Startup checks are silent by design.
        }
    }
}
