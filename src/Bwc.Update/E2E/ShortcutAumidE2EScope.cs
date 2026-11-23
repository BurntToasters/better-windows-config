#if BWC_E2E
namespace Bwc.Update;

/// <summary>
/// E2E flavor only: where <c>--set-aumid</c> may write besides the all-users Programs folder (update-elevation.md SA5).
/// The per-user sandbox keeps its shortcuts in a folder below the process's temp folder, the Elevated tests' sandbox keeps
/// them below <c>%ProgramFiles%\BWC-E2E</c>, and the <c>/MACHINE</c> sandbox uses <c>&lt;all-users
/// Programs&gt;\BWC-E2E-&lt;id&gt;</c>.
/// Compiled only under BWC_E2E, so a ship build has no sandbox folder and writes only into the all-users Programs folder.
/// </summary>
internal static class ShortcutAumidE2EScope
{
    private const string SandboxFolder = "BWC-E2E";
    private const string MachineStartPrefix = "BWC-E2E-";

    /// <param name="userTempRoot">The user profile's own temp folder (<c>%LOCALAPPDATA%\Temp</c>), when known. The installer
    /// runs this helper with TEMP and TMP set to the protected copy folder (update-elevation.md HT1), so after an in-app
    /// update the process's own temp folder is not the one the per-user sandbox was made in (SA5).</param>
    public static Func<string, bool> For(string tempRoot, string programFiles, string commonPrograms, string? userTempRoot = null) =>
        folder =>
            ProgramFilesRule.IsUnder(folder, tempRoot)
            || (userTempRoot is not null && ProgramFilesRule.IsUnder(folder, userTempRoot))
            || ProgramFilesRule.IsUnder(folder, Path.Combine(programFiles, SandboxFolder))
            || IsMachineStartFolder(folder, commonPrograms);

    private static bool IsMachineStartFolder(string folder, string commonPrograms)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return parent is not null
            && name.Length > MachineStartPrefix.Length
            && name.StartsWith(MachineStartPrefix, StringComparison.OrdinalIgnoreCase)
            && PathRules.SameDirectory(parent, commonPrograms);
    }
}
#endif
