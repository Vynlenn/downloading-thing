namespace DeezNutzDownloader.Core.Settings;

public static class DefaultSettingsFactory
{
    public static AppSettings Create()
    {
        return new AppSettings
        {
            DownloadFolder = GetDefaultDownloadFolder(),
            Theme = AppTheme.Dark,
            NetworkMode = NetworkMode.WindowsSettings,
            ProxyType = ProxyType.Http,
            ProxyPort = 8080,
            AskBeforeArchiveExtraction = true,
            SkipExactDuplicates = true,
            MaximumConcurrentDownloads = 3
        };
    }

    public static string GetDefaultDownloadFolder()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        return Path.Combine(
            userProfile,
            "Downloads",
            "DeezNutzDownloader");
    }
}
