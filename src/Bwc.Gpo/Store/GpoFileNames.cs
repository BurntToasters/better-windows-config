namespace Bwc.Gpo.Store;

/// <summary>The names and paths of the five files of a local GPO. Pure string building, no file access.</summary>
public static class GpoFileNames
{
    /// <summary>The registry policy file inside a side folder.</summary>
    public const string RegistryPol = "Registry.pol";

    /// <summary>The comment file inside a side folder.</summary>
    public const string CommentCmtx = "comment.cmtx";

    /// <summary>The version file in the GPO root.</summary>
    public const string GptIniFile = "gpt.ini";

    /// <summary>The Computer Configuration folder.</summary>
    public const string MachineFolder = "Machine";

    /// <summary>The User Configuration folder.</summary>
    public const string UserFolder = "User";

    /// <summary>The folder name of a side.</summary>
    /// <param name="side">The side.</param>
    /// <returns><c>Machine</c> or <c>User</c>.</returns>
    public static string FolderName(GpoSide side) => side == GpoSide.Machine ? MachineFolder : UserFolder;

    /// <summary>The path of a side's Registry.pol.</summary>
    /// <param name="root">The GPO root.</param>
    /// <param name="side">The side.</param>
    /// <returns>The path.</returns>
    public static string PolPath(string root, GpoSide side) => Path.Combine(root, FolderName(side), RegistryPol);

    /// <summary>The path of a side's comment.cmtx.</summary>
    /// <param name="root">The GPO root.</param>
    /// <param name="side">The side.</param>
    /// <returns>The path.</returns>
    public static string CmtxPath(string root, GpoSide side) => Path.Combine(root, FolderName(side), CommentCmtx);

    /// <summary>The path of gpt.ini.</summary>
    /// <param name="root">The GPO root.</param>
    /// <returns>The path.</returns>
    public static string GptIniPath(string root) => Path.Combine(root, GptIniFile);

    /// <summary>The five files in backup order: Machine pol and comments, User pol and comments, gpt.ini.</summary>
    /// <param name="root">The GPO root.</param>
    /// <returns>Five paths.</returns>
    public static string[] AllFiles(string root) =>
    [
        PolPath(root, GpoSide.Machine),
        CmtxPath(root, GpoSide.Machine),
        PolPath(root, GpoSide.User),
        CmtxPath(root, GpoSide.User),
        GptIniPath(root),
    ];
}
