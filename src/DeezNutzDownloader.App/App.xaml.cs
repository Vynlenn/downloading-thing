using System.Windows;
using System.Windows.Threading;
using DeezNutzDownloader.App.Theming;
using DeezNutzDownloader.Infrastructure.Configuration;
using DeezNutzDownloader.Infrastructure.Logging;

namespace DeezNutzDownloader.App;

public partial class App : Application
{
    private LocalLog? _log;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException +=
            CurrentDomain_UnhandledException;

        TaskScheduler.UnobservedTaskException +=
            TaskScheduler_UnobservedTaskException;
    }

    private async void Application_Startup(
        object sender,
        StartupEventArgs e)
    {
        try
        {
            AppPaths paths = AppPaths.CreateDefault();
            SettingsStore settingsStore = new(paths);
            _log = new LocalLog(paths);

            var settings = await settingsStore.LoadAsync();

            ThemeManager.Apply(settings.Theme);

            await _log.InformationAsync(
                $"Application starting. Portable mode: {paths.IsPortable}");

            MainWindow window = new(
                settings,
                settingsStore,
                paths,
                _log);

            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            if (_log is not null)
            {
                await _log.ErrorAsync(
                    "Application startup failed.",
                    exception);
            }

            MessageBox.Show(
                "DeezNutzDownloader could not start.\n\n" +
                exception.Message,
                "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private async void Application_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (_log is not null)
        {
            await _log.ErrorAsync(
                "An unexpected interface error occurred.",
                e.Exception);
        }

        MessageBox.Show(
            "An unexpected error occurred. Details were written " +
            "to the local application log.",
            "DeezNutzDownloader",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (_log is not null && e.ExceptionObject is Exception exception)
        {
            _log.ErrorAsync(
                "An unrecoverable application error occurred.",
                exception).GetAwaiter().GetResult();
        }
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        if (_log is not null)
        {
            _log.ErrorAsync(
                "An unobserved background task failed.",
                e.Exception).GetAwaiter().GetResult();
        }

        e.SetObserved();
    }
}
