namespace DeezNutzDownloader.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum NetworkMode
{
    WindowsSettings,
    Direct,
    ManualProxy
}

public enum ProxyType
{
    Http,
    Https,
    Socks4,
    Socks5
}

public sealed class AppSettings
{
    public int ConfigurationVersion { get; set; } = 1;

    public string DownloadFolder { get; set; } = string.Empty;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public NetworkMode NetworkMode { get; set; } = NetworkMode.WindowsSettings;

    public ProxyType ProxyType { get; set; } = ProxyType.Http;

    public string ProxyHost { get; set; } = string.Empty;

    public int ProxyPort { get; set; } = 8080;

    public bool AskBeforeArchiveExtraction { get; set; } = true;

    public bool SkipExactDuplicates { get; set; } = true;

    public int MaximumConcurrentDownloads { get; set; } = 3;
}
