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
                Brush("#FF08110D");

            resources["SurfaceBrush"] =
                Brush("#FF101C17");

            resources["SurfaceSecondaryBrush"] =
                Brush("#FF182821");

            resources["TextBrush"] =
                Brush("#FFE8F3ED");

            resources["MutedTextBrush"] =
                Brush("#FF91A89C");

            resources["BorderBrush"] =
                Brush("#FF284137");

            resources["AccentBrush"] =
                Brush("#FF168664");

            resources["AccentHoverBrush"] =
                Brush("#FF1CA878");
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
