namespace Bwc.Update.Tests;

/// <summary>Finds files in the repository from the test output folder. Never uses source file paths.</summary>
internal static class RepoPaths
{
    private const string Marker = "BetterWindowsConfig.slnx";

    /// <summary>The folder that holds BetterWindowsConfig.slnx, found by walking up from the test output.</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, Marker)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"{Marker} was not found above {AppContext.BaseDirectory}");
    }
}
