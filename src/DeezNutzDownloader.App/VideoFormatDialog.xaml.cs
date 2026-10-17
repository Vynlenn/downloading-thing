using System.Windows;
using DeezNutzDownloader.Adapters.Media;

namespace DeezNutzDownloader.App;

/// <summary>
/// Lets the user pick a yt-dlp format (or best) and, for playlist
/// URLs, the whole playlist instead of one video.
/// </summary>
public partial class VideoFormatDialog : Window
{
    private readonly MediaProbeResult _probe;

    /// <summary>Null means best quality (yt-dlp default).</summary>
    public string? SelectedFormatId { get; private set; }

    public bool DownloadPlaylist { get; private set; }

    public VideoFormatDialog(MediaProbeResult probe)
    {
        _probe =
            probe ??
            throw new ArgumentNullException(nameof(probe));

        InitializeComponent();

        TitleText.Text = probe.Title;

        var options = new List<FormatChoice>
        {
            new(null, "Best quality (recommended)")
        };

        options.AddRange(
            probe.Formats.Select(format =>
                new FormatChoice(format.FormatId, format.Label)));

        FormatsList.ItemsSource = options;
        FormatsList.SelectedIndex = 0;

        if (probe.IsPlaylist)
        {
            PlaylistCheckBox.Visibility = Visibility.Visible;

            PlaylistCheckBox.Content =
                $"Grab the entire playlist" +
                (probe.PlaylistCount.HasValue
                    ? $" ({probe.PlaylistCount} items)"
                    : string.Empty);
        }

        PlaylistCheckBox.Checked += (_, _) => UpdateFormatState();
        PlaylistCheckBox.Unchecked += (_, _) => UpdateFormatState();
    }

    private void UpdateFormatState()
    {
        // Per-video formats do not apply to a whole playlist run.
        FormatsList.IsEnabled = !IsPlaylistRun();
    }

    private bool IsPlaylistRun()
    {
        return _probe.IsPlaylist &&
            PlaylistCheckBox.IsChecked == true;
    }

    private void DownloadButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DownloadPlaylist = IsPlaylistRun();

        SelectedFormatId = DownloadPlaylist
            ? null
            : (FormatsList.SelectedItem as FormatChoice)?.FormatId;

        DialogResult = true;
        Close();
    }

    private sealed record FormatChoice(
        string? FormatId,
        string Label);
}
