namespace Bwc.E2E.Harness;

/// <summary>Removes the per-run folder that a test made under artifacts, once nothing is left in it.</summary>
internal static class RunFolders
{
    /// <summary>
    /// Removes the parent of a scope folder (the run id folder) when it is empty. A folder with content is kept, and
    /// the shared parent above the run id folder is never touched.
    /// </summary>
    internal static void RemoveEmptyRunFolder(string scopeFolder)
    {
        string? runFolder = Path.GetDirectoryName(scopeFolder);
        if (runFolder is null || !Directory.Exists(runFolder))
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(runFolder).Any())
        {
            Directory.Delete(runFolder);
        }
    }
}
