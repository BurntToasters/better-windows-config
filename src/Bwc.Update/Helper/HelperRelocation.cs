namespace Bwc.Update;

/// <summary>
/// The installer replaces files in the install folder, and a running helper locks its own files.
/// So a helper started from the install folder first copies its folder to the staging root and runs
/// the copy, then exits. The copy carries --relocated and never copies again.
/// </summary>
public static class HelperRelocation
{
    public static bool ShouldRelocate(string baseDirectory, string installDir, bool relocated)
    {
        if (relocated)
        {
            return false;
        }

        var running = WithSeparator(Path.GetFullPath(baseDirectory));
        var install = WithSeparator(Path.GetFullPath(installDir));
        return running.StartsWith(install, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies every file and folder under <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
