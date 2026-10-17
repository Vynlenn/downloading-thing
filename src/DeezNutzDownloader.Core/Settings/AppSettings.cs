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
    public int ConfigurationVersion { get; set; } = 6;

    public string DownloadFolder { get; set; } = string.Empty;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public NetworkMode NetworkMode { get; set; } = NetworkMode.WindowsSettings;

    public ProxyType ProxyType { get; set; } = ProxyType.Http;

    public string ProxyHost { get; set; } = string.Empty;

    public int ProxyPort { get; set; } = 8080;

    public string ProxyUsername { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-encrypted proxy password (base64). Empty means none.
    /// The cleartext password is never persisted.
    /// </summary>
    public string ProxyPasswordProtected { get; set; } = string.Empty;

    public bool AskBeforeArchiveExtraction { get; set; } = true;

    public bool SkipExactDuplicates { get; set; } = true;

    public int MaximumConcurrentDownloads { get; set; } = 3;

    public string YtDlpPath { get; set; } = string.Empty;

    public string FFmpegPath { get; set; } = string.Empty;

    public bool OrganizeIntoSiteFolders { get; set; } = true;

    public bool ClipboardWatchEnabled { get; set; } = true;

    /// <summary>
    /// When true (default), yt-dlp/ffmpeg are auto-downloaded into
    /// the managed tools folder. Manual paths always win.
    /// </summary>
    public bool ExternalToolsAutoUpdate { get; set; } = true;

    /// <summary>
    /// Optional yt-dlp --cookies-from-browser value (e.g. "chrome"
    /// or "firefox:profile"). Empty means off. Uses the user's own
    /// logged-in session for video sites that need a login.
    /// </summary>
    public string YtDlpCookiesFromBrowser { get; set; } = string.Empty;

    /// <summary>
    /// Quiet-hours speed cap: inside the window downloads run at
    /// QuietHoursCapKBps, outside it full speed. Off by default.
    /// </summary>
    public bool QuietHoursEnabled { get; set; } = false;

    /// <summary>Window start, local time (hours + minutes).</summary>
    public int QuietHoursStartHour { get; set; } = 22;

    public int QuietHoursStartMinute { get; set; } = 0;

    /// <summary>Window end, local time (hours + minutes).</summary>
    public int QuietHoursEndHour { get; set; } = 7;

    public int QuietHoursEndMinute { get; set; } = 0;

    /// <summary>Capped rate inside quiet hours (KB/s).</summary>
    public int QuietHoursCapKBps { get; set; } = 512;

    /// <summary>
    /// GitHub repository (owner/name) used for update checks.
    /// Empty disables update checks.
    /// </summary>
    public string UpdateRepository { get; set; } = string.Empty;

    public bool CheckForUpdatesOnStartup { get; set; } = true;
}
