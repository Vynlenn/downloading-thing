namespace DeezNutzDownloader.Core.Settings;

public static class DefaultSettingsFactory
{
    public static AppSettings Create()
    {
        return new AppSettings
        {
            DownloadFolder = GetDefaultDownloadFolder(),
            Theme = AppTheme.System,
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
        const string preferredParent = @"E:\Downloads\AI";

        if (Directory.Exists(preferredParent))
        {
            return Path.Combine(preferredParent, "DeezNutzDownloader-Downloads");
        }

        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        return Path.Combine(
            userProfile,
            "Downloads",
            "DeezNutzDownloader");
    }
}
