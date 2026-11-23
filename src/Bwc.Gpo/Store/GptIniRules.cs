namespace Bwc.Gpo.Store;

/// <summary>
/// The classic gpt.ini choices, in one place. The captures of groups A and C settle them (the gpt.ini of every A and C
/// golden step); a member that names no capture case restates the 2026-11-18 probe.
/// </summary>
public static class GptIniRules
{
    /// <summary>The registry client-side extension GUID, first in every pair.</summary>
    public const string RegistryExtension = "{35378EAC-683F-11D2-A89A-00C04FBBCFA2}";

    /// <summary>The Computer Configuration snap-in GUID of the pair.</summary>
    public const string MachineTool = "{D02B1F72-3407-48AE-BA88-E8213C6761F1}";

    /// <summary>The User Configuration snap-in GUID of the pair.</summary>
    public const string UserTool = "{D02B1F73-3407-48AE-BA88-E8213C6761F1}";

    /// <summary>The section that holds every key.</summary>
    public const string GeneralSection = "General";

    /// <summary>The version key.</summary>
    public const string VersionKey = "Version";

    /// <summary>
    /// A comment-only change bumps the version and writes gpt.ini. Settled by capture A/Machine_CommentOnly and
    /// A/User_CommentOnly (D3).
    /// </summary>
    public const bool CommentOnlyChangeBumpsVersion = true;

    /// <summary>
    /// When the saved side holds no setting any more and its extension line exists, the line keeps its key and its value
    /// becomes one space, and a missing line is not added. Settled by capture K/KeyTrailingBackslash_Enable step 2 and
    /// C/GptIni_PairRemoval (D4), and by A/Machine_CommentOnly for the missing line (D11).
    /// </summary>
    public const bool EmptiedSideBlanksPairLine = true;

    /// <summary>
    /// The text an extension line becomes when the side's pair is removed and no group remains: the tail the line kept
    /// plus this one space (captures C/GptIni_PairRemoval, where the tail is empty, and E/Decimal_EdgeValues step 4, where
    /// the tail is one space and the line reads two).
    /// </summary>
    public const string EmptiedLineSpacer = " ";

    /// <summary>
    /// The text after the last GUID group of an extension value, kept as it is (the tail). A value without a group is all
    /// tail. Settled by E/Decimal_EdgeValues steps 2 to 5 and C/GptIni_ForeignExtensions step 4 (the tail is empty there).
    /// </summary>
    /// <param name="value">The raw value, after the equals sign.</param>
    /// <returns>The tail.</returns>
    public static string Tail(string value)
    {
        var close = value.LastIndexOf(']');
        return close < 0 ? value : value[(close + 1)..];
    }

    /// <summary>
    /// The keys the store writes: the extension line of each side and Version. Their order no longer places a missing key (see
    /// <see cref="InsertionIndex"/>); a new file gets its keys in the order they are first written.
    /// </summary>
    public static IReadOnlyList<string> NewFileLineOrder { get; } = ["gPCMachineExtensionNames", VersionKey, "gPCUserExtensionNames"];

    /// <summary>The extension-names key of a side.</summary>
    /// <param name="side">The side.</param>
    /// <returns>The key name.</returns>
    public static string ExtensionKey(GpoSide side) => side == GpoSide.Machine ? NewFileLineOrder[0] : NewFileLineOrder[2];

    /// <summary>The snap-in GUID of a side's pair.</summary>
    /// <param name="side">The side.</param>
    /// <returns>The GUID with braces.</returns>
    public static string Tool(GpoSide side) => side == GpoSide.Machine ? MachineTool : UserTool;

    /// <summary>
    /// The version after one save of a side. The other half never changes. A half at 65535 goes to 1, not 0, and does not
    /// carry into the other half: captures C/Version_FromHighMachine (65535 becomes 1) and C/Version_FromHighUser (the high
    /// half 65535 becomes 1, so the file reads 65536).
    /// </summary>
    /// <param name="version">The current 32-bit version.</param>
    /// <param name="side">The saved side.</param>
    /// <returns>The new version.</returns>
    public static uint Bump(uint version, GpoSide side)
    {
        if (side == GpoSide.Machine)
        {
            return (version & 0xFFFF0000u) | NextHalf(version & 0xFFFFu);
        }

        return (NextHalf(version >> 16) << 16) | (version & 0xFFFFu);
    }

    private static uint NextHalf(uint half) => half >= 0xFFFFu ? 1u : half + 1u;

    /// <summary>
    /// Where a missing key goes inside the General section: after the last non-blank line of the section, whatever the other
    /// keys are. Settled by capture A/User_Enabled, A/User_Then_Machine and A/Machine_Then_User (D2): the first save of a
    /// side writes its extension line and then Version, and a later save of the other side appends its line last.
    /// </summary>
    /// <param name="keys">For each line after the header, the key name, or null when the line is not a key.</param>
    /// <param name="blank">For each of those lines, whether it is blank.</param>
    /// <param name="key">The key to insert; one of <see cref="NewFileLineOrder"/>.</param>
    /// <returns>An index into the section's lines (0 means right after the header).</returns>
    public static int InsertionIndex(IReadOnlyList<string?> keys, IReadOnlyList<bool> blank, string key)
    {
        var last = keys.Count - 1;
        while (last >= 0 && blank[last])
        {
            last--;
        }

        return last + 1;
    }

    /// <summary>
    /// Takes the snap-in GUID of a side out of the registry group of the parsed extension groups, and the group with it when
    /// no tool is left. Other groups and tools stay in place. Settled by C/GptIni_ForeignExtensions step 4 and
    /// C/GptIni_PairRemoval.
    /// </summary>
    /// <param name="groups">The groups; the first entry of each is the extension GUID.</param>
    /// <param name="side">The side.</param>
    /// <returns>True when the groups changed.</returns>
    public static bool RemovePair(List<List<string>> groups, GpoSide side)
    {
        var tool = Tool(side);
        var changed = false;
        foreach (var group in groups.Where(g => string.Equals(g[0], RegistryExtension, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (group.RemoveAll(g => string.Equals(g, tool, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                changed = true;
                if (group.Count == 1)
                {
                    groups.Remove(group);
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Adds the pair for a side to the parsed extension groups (C/GptIni_ForeignExtensions). Each group is the
    /// extension GUID followed by tool GUIDs. A tool joins an existing registry group in ordinal order; a new group is
    /// inserted before the first group whose GUID is greater, else appended. Existing groups are never reordered.
    /// </summary>
    /// <param name="groups">The groups; the first entry of each is the extension GUID.</param>
    /// <param name="side">The side.</param>
    /// <returns>True when the groups changed.</returns>
    public static bool AddPair(List<List<string>> groups, GpoSide side)
    {
        var tool = Tool(side);
        foreach (var group in groups)
        {
            if (!string.Equals(group[0], RegistryExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (group.Skip(1).Any(g => string.Equals(g, tool, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var at = group.FindIndex(1, g => string.Compare(g, tool, StringComparison.OrdinalIgnoreCase) > 0);
            group.Insert(at < 0 ? group.Count : at, tool);
            return true;
        }

        var index = groups.FindIndex(g => string.Compare(g[0], RegistryExtension, StringComparison.OrdinalIgnoreCase) > 0);
        groups.Insert(index < 0 ? groups.Count : index, [RegistryExtension, tool]);
        return true;
    }
}
