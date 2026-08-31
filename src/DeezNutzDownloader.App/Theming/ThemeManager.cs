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
                Brush("#FF060C09");

            resources["SurfaceBrush"] =
                Brush("#FF0E1B15");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FF162A20");

            resources["TextBrush"] =
                Brush("#FFF1FBF6");

            resources["MutedTextBrush"] =
                Brush("#FFA6BBAF");

            resources["BorderBrush"] =
                Brush("#FF2D5141");

            resources["AccentBrush"] =
                Brush("#FF18A875");

            resources["AccentHoverBrush"] =
                Brush("#FF25C88C");

            resources["AccentPressedBrush"] =
                Brush("#FF107D58");

            resources["AccentSoftBrush"] =
                Brush("#FF123D2F");

            resources["AccentTextBrush"] =
                Brush("#FF66F0B9");

            resources["SidebarBrush"] =
                Brush("#FF08130E");

            resources["SurfaceHoverBrush"] =
                Brush("#FF1D392C");
        }
        else
        {
            resources["AppBackgroundBrush"] =
                Brush("#FFF2F7F4");

            resources["SurfaceBrush"] =
                Brush("#FFFFFFFF");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FFE8F1EC");

            resources["TextBrush"] =
                Brush("#FF14231C");

            resources["MutedTextBrush"] =
                Brush("#FF667B70");

            resources["BorderBrush"] =
                Brush("#FFD2E1D9");

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

            resources["SidebarBrush"] =
                Brush("#FFE7F0EB");

            resources["SurfaceHoverBrush"] =
                Brush("#FFDDECE4");
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
