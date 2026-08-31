using DeezNutzDownloader.Infrastructure.Configuration;

namespace DeezNutzDownloader.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void PortableMarkerUsesLocalDataDirectory()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeezNutzDownloaderTests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            File.WriteAllText(
                Path.Combine(temporaryDirectory, "portable.flag"),
                string.Empty);

            AppPaths paths = new(temporaryDirectory);

            Assert.True(paths.IsPortable);
            Assert.Equal(
                Path.Combine(temporaryDirectory, "data"),
                paths.DataDirectory);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }
}
