namespace DeezNutzDownloader.Infrastructure.Configuration;

public sealed class AppPaths
{
    public AppPaths(string executableDirectory)
    {
        ExecutableDirectory = Path.GetFullPath(executableDirectory);

        PortableMarkerFile = Path.Combine(
            ExecutableDirectory,
            "portable.flag");

        IsPortable = File.Exists(PortableMarkerFile);

        if (IsPortable)
        {
            DataDirectory = Path.Combine(
                ExecutableDirectory,
                "data");
        }
        else
        {
            string localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            DataDirectory = Path.Combine(
                localApplicationData,
                "DeezNutzDownloader");
        }

        SettingsFile = Path.Combine(DataDirectory, "settings.json");
        DownloadQueueFile = Path.Combine(DataDirectory, "download-queue.json");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
    }

    public string ExecutableDirectory { get; }

    public string PortableMarkerFile { get; }

    public bool IsPortable { get; }

    public string DataDirectory { get; }

    public string SettingsFile { get; }

    public string DownloadQueueFile { get; }

    public string LogsDirectory { get; }

    public static AppPaths CreateDefault()
    {
        return new AppPaths(AppContext.BaseDirectory);
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
