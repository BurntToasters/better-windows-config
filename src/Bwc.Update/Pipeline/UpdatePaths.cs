namespace Bwc.Update;

/// <summary>
/// The per-user staging root. The check writes here, and the helper accepts installers only from
/// here. The production command line always uses this root, never a path from the caller.
/// </summary>
public static class UpdatePaths
{
    public const string StagingFolderName = "BWC-Update";

    public static string DefaultStagingRoot => Path.Combine(Path.GetTempPath(), StagingFolderName);
}
