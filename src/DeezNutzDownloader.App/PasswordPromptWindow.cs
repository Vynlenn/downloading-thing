using System.Windows;
using System.Windows.Controls;

namespace DeezNutzDownloader.App;

/// <summary>
/// Small themed dialog asking for an archive password. The password
/// is only handed to 7-Zip for this extraction; it is never stored.
/// </summary>
internal sealed class PasswordPromptWindow : Window
{
    private readonly PasswordBox _passwordBox = new() { Margin = new Thickness(0, 12, 0, 0) };

    private PasswordPromptWindow(string archiveName, bool wrongPassword)
    {
        Title = "Archive password";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");

        var message = new TextBlock
        {
            Text = wrongPassword
                ? $"That password did not open {archiveName}. Try again:"
                : $"{archiveName} is password-protected. Enter its password:",
            TextWrapping = TextWrapping.Wrap
        };

        var ok = new Button
        {
            Content = "Extract",
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button { Content = "Skip this archive", IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { message, _passwordBox, buttons }
        };

        Loaded += (_, _) => _passwordBox.Focus();
    }

    /// <returns>The password, or null when the user skipped.</returns>
    public static string? Ask(Window owner, string archiveName, bool wrongPassword)
    {
        var dialog = new PasswordPromptWindow(archiveName, wrongPassword)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true
            ? dialog._passwordBox.Password
            : null;
    }
}
