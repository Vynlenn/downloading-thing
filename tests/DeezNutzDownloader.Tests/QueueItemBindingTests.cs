namespace DeezNutzDownloader.Tests;

/// <summary>
/// Guards the crash class that froze the app when the first queue row
/// rendered: a TwoWay-by-default binding (ProgressBar.Value) against a
/// get-only DownloadQueueItem property throws XamlParseException during
/// layout. Every such binding must be explicitly OneWay.
/// </summary>
public sealed class QueueItemBindingTests
{
    [Fact]
    public void QueueRow_TwoWayByDefaultBindings_AreOneWay()
    {
        string? xaml = FindMainWindowXaml();

        Assert.True(
            xaml is not null,
            "MainWindow.xaml not found from the test run directory.");

        // Dependency properties whose default binding mode is TwoWay.
        // Bound to get-only record properties, they crash template load.
        string[] twoWayTargets = ["Value=\"{Binding", "IsIndeterminate=\"{Binding"];

        foreach (string target in twoWayTargets)
        {
            int index = 0;

            while ((index = xaml!.IndexOf(
                target,
                index,
                StringComparison.Ordinal)) >= 0)
            {
                int end = xaml.IndexOf(
                    "}\"",
                    index,
                    StringComparison.Ordinal);

                Assert.True(end > index, $"Unterminated binding at {index}.");

                string binding = xaml[index..end];

                Assert.Contains(
                    "Mode=OneWay",
                    binding,
                    StringComparison.Ordinal);

                index = end;
            }
        }
    }

    private static string? FindMainWindowXaml()
    {
        string? directory = AppContext.BaseDirectory;

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory,
                "src",
                "DeezNutzDownloader.App",
                "MainWindow.xaml");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        }

        return null;
    }
}
