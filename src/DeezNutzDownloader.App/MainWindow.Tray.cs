using System.Diagnostics;
using System.IO;
using System.Windows;
using DeezNutzDownloader.Core.Downloads;
using Forms = System.Windows.Forms;

namespace DeezNutzDownloader.App;

/// <summary>
/// Notification-area icon, "batch finished" notifications, optional
/// minimize-to-tray, and shut-down-when-done.
/// </summary>
public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show DeezNutz Downloader", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Start all downloads", null, async (_, _) =>
        {
            RestoreFromTray();
            await StartBatchAsync(onlyItems: null);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "DeezNutz Downloader",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        StateChanged += MainWindow_StateChanged;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            if (
                Environment.ProcessPath is string exe &&
                System.Drawing.Icon.ExtractAssociatedIcon(exe) is System.Drawing.Icon icon)
            {
                return icon;
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException)
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void DisposeTray()
    {
        StateChanged -= MainWindow_StateChanged;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    /// <summary>Tells the user a batch ended when they are not looking.</summary>
    private void NotifyBatchFinished(DownloadBatchResult result)
    {
        if (_trayIcon is null || _isClosing || (IsVisible && IsActive) || result.SelectedCount == 0)
        {
            return;
        }

        string text =
            $"{result.CompletedCount} completed" +
            (result.FailedCount > 0 ? $", {result.FailedCount} failed" : string.Empty) +
            (result.CanceledCount > 0 ? $", {result.CanceledCount} canceled" : string.Empty) +
            ".";

        _trayIcon.ShowBalloonTip(
            6000,
            "Downloads finished",
            text,
            result.FailedCount > 0 ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// "Shut down when done": uses Windows' own shutdown timer, which
    /// shows its own warning, and offers to abort it right away.
    /// Never fires for a batch the user canceled.
    /// </summary>
    private async Task OfferShutdownIfRequestedAsync(DownloadBatchResult result)
    {
        if (
            ShutdownWhenDoneCheckBox.IsChecked != true ||
            _cancellationRequested ||
            _isClosing ||
            result.SelectedCount == 0)
        {
            return;
        }

        ShutdownWhenDoneCheckBox.IsChecked = false;

        // Save first: Windows closes apps at the end of the countdown.
        await PersistQueueAsync();

        string shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");

        if (!RunQuietly(
                shutdown,
                "/s", "/t", "120",
                "/c", "DeezNutz Downloader finished its downloads. Windows will shut down in 2 minutes."))
        {
            ApplicationStatusText.Text = "Could not schedule the shutdown.";
            return;
        }

        RestoreFromTray();

        MessageBoxResult answer = MessageBox.Show(
            this,
            "Downloads are done. Windows will shut down in 2 minutes.\n\n" +
            "Cancel the shutdown?",
            "Shutting down",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            RunQuietly(shutdown, "/a");
            ApplicationStatusText.Text = "Shutdown canceled.";
        }
        else
        {
            ApplicationStatusText.Text = "Windows will shut down shortly.";
        }
    }

    private static bool RunQuietly(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
