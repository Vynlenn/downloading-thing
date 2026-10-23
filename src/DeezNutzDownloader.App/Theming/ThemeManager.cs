using System.Windows;
using System.Windows.Media;
using DeezNutzDownloader.Core.Settings;
using Microsoft.Win32;

namespace DeezNutzDownloader.App.Theming;

public static class ThemeManager
{
    public static void Apply(AppTheme requestedTheme)
    {
        bool useDarkTheme = requestedTheme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsWindowsDarkTheme()
        };

        ResourceDictionary resources =
            Application.Current.Resources;

        if (useDarkTheme)
        {
            resources["AppBackgroundBrush"] =
                Brush("#FF0A0F0D");

            resources["SurfaceBrush"] =
                Brush("#FF111A16");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FF17231D");

            resources["RaisedSurfaceBrush"] =
                Brush("#FF1A2921");

            resources["TextBrush"] =
                Brush("#FFE6F0EB");

            resources["MutedTextBrush"] =
                Brush("#FFA3B3AA");

            resources["DisabledTextBrush"] =
                Brush("#FF718078");

            resources["BorderBrush"] =
                Brush("#FF29382F");

            resources["StrongBorderBrush"] =
                Brush("#FF5A7165");

            resources["AccentBrush"] =
                Brush("#FF10B981");

            resources["AccentHoverBrush"] =
                Brush("#FF34D399");

            resources["AccentPressedBrush"] =
                Brush("#FF0C7A55");

            resources["AccentSoftBrush"] =
                Brush("#FF10382B");

            resources["AccentTextBrush"] =
                Brush("#FF34D399");

            resources["TextOnAccentBrush"] =
                Brush("#FF062219");

            resources["SidebarBrush"] =
                Brush("#FF0C1411");

            resources["SurfaceHoverBrush"] =
                Brush("#FF1E2F26");

            resources["ErrorBrush"] =
                Brush("#FFF87171");
        }
        else
        {
            resources["AppBackgroundBrush"] =
                Brush("#FFF2F7F4");

            resources["SurfaceBrush"] =
                Brush("#FFFFFFFF");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FFE8F1EC");

            resources["RaisedSurfaceBrush"] =
                Brush("#FFFFFFFF");

            resources["TextBrush"] =
                Brush("#FF14231C");

            resources["MutedTextBrush"] =
                Brush("#FF667B70");

            resources["DisabledTextBrush"] =
                Brush("#FF9DB0A6");

            resources["BorderBrush"] =
                Brush("#FFD2E1D9");

            resources["StrongBorderBrush"] =
                Brush("#FF9DB8AB");

            resources["AccentBrush"] =
                Brush("#FF147A5C");

            resources["AccentHoverBrush"] =
                Brush("#FF0F674E");

            resources["AccentPressedBrush"] =
                Brush("#FF0A513D");

            resources["AccentSoftBrush"] =
                Brush("#FFD6EFE4");

            resources["AccentTextBrush"] =
                Brush("#FF0C6D50");

            resources["TextOnAccentBrush"] =
                Brush("#FFFFFFFF");

            resources["SidebarBrush"] =
                Brush("#FFE7F0EB");

            resources["SurfaceHoverBrush"] =
                Brush("#FFDDECE4");

            resources["ErrorBrush"] =
                Brush("#FFB91C1C");
        }
    }

    private static SolidColorBrush Brush(string color)
    {
        return new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
    }

    private static bool IsWindowsDarkTheme()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);

            return value is int lightThemeValue &&
                   lightThemeValue == 0;
        }
        catch
        {
            return false;
        }
    }
}
