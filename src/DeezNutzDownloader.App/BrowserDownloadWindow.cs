using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeezNutzDownloader.Core.Downloads;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeezNutzDownloader.App;

/// <summary>
/// Downloads one Bunkr file by using Bunkr's own pages in the
/// embedded browser, the same clicks a person would make: open the
/// file page, follow its Download link, press the page's Download
/// button, and capture the file the browser receives. Pop-up ads
/// are blocked. If the site shows a check, the window stays up so
/// the user can complete it; the download then continues on its own.
/// </summary>
internal sealed class BrowserDownloadWindow : Window
{
    private static readonly TimeSpan StartDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClickRetryAfter = TimeSpan.FromSeconds(20);
    private const int MaximumClicks = 3;

    private readonly Uri _page;
    private readonly string _temporaryPath;
    private readonly IProgress<DownloadTransferProgress>? _progress;
    private readonly WebView2 _browser = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(14, 10, 14, 10), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _driver = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly TaskCompletionSource<BrowserDownloadResult> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CoreWebView2DownloadOperation? _operation;
    private string? _suggestedName;
    private string? _mediaType;
    private DateTime _deadline;
    private DateTime _lastClick;
    private int _clicks;
    private bool _onDownloadPage;
    private bool _finished;

    public BrowserDownloadWindow(
        Uri page,
        string destinationDirectory,
        IProgress<DownloadTransferProgress>? progress)
    {
        _page = page;
        _progress = progress;
        _temporaryPath = Path.Combine(
            destinationDirectory,
            $".download-{Guid.NewGuid():N}.browser");

        Title = $"Downloading from {page.Host}";
        Width = 900;
        Height = 640;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");

        _status.Text =
            $"Opening {page.Host}… The download starts on its own. " +
            "If the page shows a check, complete it here.";

        var layout = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        layout.Children.Add(_status);
        layout.Children.Add(_browser);
        Content = layout;

        _driver.Tick += Driver_Tick;
        Closed += (_, _) => Fail(new InvalidOperationException(
            "The browser window was closed before the download finished."));
    }

    public async Task<BrowserDownloadResult> RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => Dispatcher.BeginInvoke(() =>
            {
                _operation?.Cancel();
                Fail(new OperationCanceledException(cancellationToken));
            }));

        try
        {
            Show();
            await _browser.EnsureCoreWebView2Async();

            CoreWebView2 core = _browser.CoreWebView2;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += Core_DownloadStarting;
            core.Settings.AreDefaultScriptDialogsEnabled = false;

            _deadline = DateTime.UtcNow + StartDeadline;
            core.Navigate(_page.AbsoluteUri);
            _driver.Start();
        }
        catch (Exception exception)
        {
            Fail(new InvalidOperationException(
                "The built-in browser could not start (WebView2 runtime). " + exception.Message,
                exception));
        }

        return await _done.Task.ConfigureAwait(true);
    }

    private async void Driver_Tick(object? sender, EventArgs e)
    {
        if (_finished || _operation is not null || _browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        if (DateTime.UtcNow > _deadline)
        {
            Fail(new NotAFileException(
                _page.Host,
                $"{_page.Host} did not start the download within 2 minutes. " +
                "The file may be removed, or the page needs a check: retry and " +
                "complete it in the window."));
            return;
        }

        try
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out Uri? current))
            {
                return;
            }

            bool downloadPage = current.AbsolutePath.Contains("/file/", StringComparison.OrdinalIgnoreCase);

            if (!downloadPage)
            {
                // File page: follow its own Download link.
                string json = await core.ExecuteScriptAsync(
                    "(() => { const a = [...document.querySelectorAll('a[href]')]" +
                    ".find(x => /\\/file\\/\\d+/.test(x.href)); return a ? a.href : null; })()");

                if (
                    System.Text.Json.JsonSerializer.Deserialize<string?>(json) is string link &&
                    Uri.TryCreate(link, UriKind.Absolute, out Uri? target) &&
                    target.Scheme == Uri.UriSchemeHttps)
                {
                    core.Navigate(target.AbsoluteUri);
                }

                return;
            }

            if (!_onDownloadPage)
            {
                _onDownloadPage = true;
                _status.Text = "Starting the download…";
            }

            // Download page: press its button (again if nothing came).
            if (_clicks < MaximumClicks && DateTime.UtcNow - _lastClick > ClickRetryAfter)
            {
                string clicked = await core.ExecuteScriptAsync(
                    "(() => { const b = document.getElementById('download-btn');" +
                    " if (!b) return false; b.click(); return true; })()");

                if (clicked == "true")
                {
                    _clicks++;
                    _lastClick = DateTime.UtcNow;
                }
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Page mid-navigation; the next tick tries again.
        }
    }

    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        if (_operation is not null || _finished)
        {
            args.Cancel = true;
            return;
        }

        _suggestedName = Path.GetFileName(args.ResultFilePath);
        _mediaType = args.DownloadOperation.MimeType;

        // Into our own temp name next to the destination; the
        // transfer service names and finishes it like any download.
        args.ResultFilePath = _temporaryPath;
        args.Handled = true;

        _operation = args.DownloadOperation;
        _operation.BytesReceivedChanged += (_, _) => ReportProgress();
        _operation.StateChanged += Operation_StateChanged;

        _status.Text = "Downloading…";
        Hide();
        ReportProgress();
    }

    private void ReportProgress()
    {
        if (_operation is null)
        {
            return;
        }

        long? total = _operation.TotalBytesToReceive is ulong value and > 0 ? (long)value : null;
        _progress?.Report(new DownloadTransferProgress(_operation.BytesReceived, total));
    }

    private void Operation_StateChanged(object? sender, object e)
    {
        if (_operation is null)
        {
            return;
        }

        switch (_operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                ReportProgress();
                Finish(new BrowserDownloadResult(
                    _temporaryPath,
                    _suggestedName,
                    _operation.BytesReceived,
                    _mediaType));
                break;

            case CoreWebView2DownloadState.Interrupted:
                Fail(new IOException(
                    $"The browser download from {_page.Host} was interrupted " +
                    $"({_operation.InterruptReason})."));
                break;
        }
    }

    private void Finish(BrowserDownloadResult result)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _driver.Stop();
        _done.TrySetResult(result);
        CloseQuietly();
    }

    private void Fail(Exception exception)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _driver.Stop();

        try
        {
            if (File.Exists(_temporaryPath))
            {
                File.Delete(_temporaryPath);
            }
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }

        _done.TrySetException(exception);
        CloseQuietly();
    }

    private void CloseQuietly()
    {
        try
        {
            if (IsLoaded)
            {
                Close();
            }
        }
        catch (InvalidOperationException)
        {
            // Already closing.
        }

        _browser.Dispose();
    }
}

/// <summary>Runs browser downloads on the UI thread, one window each.</summary>
internal sealed class WebViewBrowserDownloader : IBrowserDownloader
{
    private readonly Dispatcher _dispatcher;

    public WebViewBrowserDownloader(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<BrowserDownloadResult> DownloadAsync(
        Uri page,
        string destinationDirectory,
        string? displayName,
        IProgress<DownloadTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        return _dispatcher
            .InvokeAsync(() =>
                new BrowserDownloadWindow(page, destinationDirectory, progress)
                    .RunAsync(cancellationToken))
            .Task
            .Unwrap();
    }
}
