namespace Bwc.Gpo.Store;

/// <summary>
/// The classic gpt.ini choices that the 2026-11-18 probe did not settle, in one place so the oracle capture (groups A
/// and C) corrects them in one spot. Every member marked "verify against oracle" is an assumption.
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
    /// Verify against oracle (group C): whether a comment-only change bumps the version. Assumed false: a comment-only
    /// save writes only the comment file.
    /// </summary>
    public const bool CommentOnlyChangeBumpsVersion = false;

    /// <summary>
    /// Verify against oracle (groups A and C): whether classic removes a pair when a side's last setting is cleared.
    /// Assumed false.
    /// </summary>
    public const bool RemovesPairWhenSideEmpty = false;

    /// <summary>
    /// Verify against oracle (group A): the order of the lines in a new gpt.ini, which is also the order a missing key
    /// is inserted next to its neighbours in an existing one. The probe file's order.
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
    /// Verify against oracle (probe: +1 for machine; overflow not observable): the version after one save of a side. The
    /// other half never changes. A half at 65535 wraps to 0 without a carry into the other half.
    /// </summary>
    /// <param name="version">The current 32-bit version.</param>
    /// <param name="side">The saved side.</param>
    /// <returns>The new version.</returns>
    public static uint Bump(uint version, GpoSide side)
    {
        if (side == GpoSide.Machine)
        {
            var low = unchecked((ushort)((version & 0xFFFF) + 1));
            return (version & 0xFFFF0000u) | low;
        }

        var high = unchecked((ushort)((version >> 16) + 1));
        return ((uint)high << 16) | (version & 0xFFFFu);
    }

    /// <summary>
    /// Verify against oracle (group C): where a missing key goes inside the General section. After the last existing key
    /// that precedes it in <see cref="NewFileLineOrder"/>, else before the first existing key that follows it, else
    /// after the last non-blank line of the section.
    /// </summary>
    /// <param name="keys">For each line after the header, the key name, or null when the line is not a key.</param>
    /// <param name="blank">For each of those lines, whether it is blank.</param>
    /// <param name="key">The key to insert; one of <see cref="NewFileLineOrder"/>.</param>
    /// <returns>An index into the section's lines (0 means right after the header).</returns>
    public static int InsertionIndex(IReadOnlyList<string?> keys, IReadOnlyList<bool> blank, string key)
    {
        var position = IndexOfOrder(key);
        var after = -1;
        var before = -1;
        for (var i = 0; i < keys.Count; i++)
        {
            var existing = keys[i] is null ? -1 : IndexOfOrder(keys[i]!);
            if (existing < 0 || existing == position)
            {
                continue;
            }

            if (existing < position)
            {
                after = i;
            }
            else if (before < 0)
            {
                before = i;
            }
        }

        if (after >= 0)
        {
            return after + 1;
        }

        if (before >= 0)
        {
            return before;
        }

        var last = keys.Count - 1;
        while (last >= 0 && blank[last])
        {
            last--;
        }

        return last + 1;
    }

    /// <summary>
    /// Verify against oracle (group C): adds the pair for a side to the parsed extension groups. Each group is the
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

    private static int IndexOfOrder(string key)
    {
        for (var i = 0; i < NewFileLineOrder.Count; i++)
        {
            if (string.Equals(NewFileLineOrder[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
