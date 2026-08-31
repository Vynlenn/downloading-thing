using System.Text;
using DeezNutzDownloader.Infrastructure.Configuration;

namespace DeezNutzDownloader.Infrastructure.Logging;

public sealed class LocalLog
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocalLog(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task InformationAsync(string message)
    {
        await WriteAsync("INFO", message, null);
    }

    public async Task ErrorAsync(string message, Exception? exception = null)
    {
        await WriteAsync("ERROR", message, exception);
    }

    private async Task WriteAsync(
        string level,
        string message,
        Exception? exception)
    {
        try
        {
            _paths.EnsureDirectoriesExist();

            string logFile = Path.Combine(
                _paths.LogsDirectory,
                $"DeezNutzDownloader-{DateTime.UtcNow:yyyyMMdd}.log");

            StringBuilder entry = new();

            entry.Append(DateTimeOffset.Now.ToString("O"));
            entry.Append(" [");
            entry.Append(level);
            entry.Append("] ");
            entry.Append(SecretRedactor.Redact(message));

            if (exception is not null)
            {
                entry.AppendLine();
                entry.Append(
                    SecretRedactor.Redact(exception.ToString()));
            }

            entry.AppendLine();

            await _writeLock.WaitAsync();

            try
            {
                await File.AppendAllTextAsync(logFile, entry.ToString());
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
