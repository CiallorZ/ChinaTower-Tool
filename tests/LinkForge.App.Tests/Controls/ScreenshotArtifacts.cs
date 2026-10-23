using System.Globalization;
using System.IO;

namespace LinkForge.App.Tests.Controls;

internal static class ScreenshotArtifacts
{
    private const string UpdateEnvironmentVariable = "LINKFORGE_UPDATE_SCREENSHOTS";

    public static string Directory
    {
        get
        {
            if (ShouldUpdateRepositoryArtifacts())
            {
                return Path.Combine(FindRepositoryRoot(), "screenshots");
            }

            return Path.Combine(
                Path.GetTempPath(),
                "LinkForge-tests",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "screenshots");
        }
    }

    private static bool ShouldUpdateRepositoryArtifacts()
    {
        var value = Environment.GetEnvironmentVariable(UpdateEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LinkForge.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate the LinkForge repository root.");
    }
}
