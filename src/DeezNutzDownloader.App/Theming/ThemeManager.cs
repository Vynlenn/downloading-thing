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
                Brush("#FF11141B");

            resources["SurfaceBrush"] =
                Brush("#FF1B202A");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FF272D39");

            resources["TextBrush"] =
                Brush("#FFF2F4F8");

            resources["MutedTextBrush"] =
                Brush("#FFAAB2C0");

            resources["BorderBrush"] =
                Brush("#FF343C49");

            resources["AccentBrush"] =
                Brush("#FF777EF2");

            resources["AccentHoverBrush"] =
                Brush("#FF858BF5");
        }
        else
        {
            resources["AppBackgroundBrush"] =
                Brush("#FFF4F6FA");

            resources["SurfaceBrush"] =
                Brush("#FFFFFFFF");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FFEEF1F6");

            resources["TextBrush"] =
                Brush("#FF18202C");

            resources["MutedTextBrush"] =
                Brush("#FF647084");

            resources["BorderBrush"] =
                Brush("#FFD9DEE8");

            resources["AccentBrush"] =
                Brush("#FF5C63E8");

            resources["AccentHoverBrush"] =
                Brush("#FF4D54D8");
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
