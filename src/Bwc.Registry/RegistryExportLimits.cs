namespace Bwc.Registry;

/// <summary>
/// Limits shared by the .reg and text export walks. The registry allows keys nested 512 levels, and
/// <c>RegistryWriter</c> stops its subtree walks at the same depth. An export opens keys without
/// REG_OPTION_OPEN_LINK, so a link that points at its own ancestor makes a walk go deeper than any real tree.
/// </summary>
internal static class RegistryExportLimits
{
    /// <summary>
    /// How many levels below the start key (level 0) an export walk reads. A key one level deeper is skipped with
    /// its subtree, as an unreadable sub key is. See <c>docs/failure-modes/export.md</c>.
    /// </summary>
    internal const int MaxSubtreeDepth = 512;
}
