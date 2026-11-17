namespace Bwc.Registry;

/// <summary>
/// The order classic regedit lists keys and values in: the Windows NLS comparison at the user default locale with
/// case ignored (<c>CompareStringEx</c> with <c>NORM_IGNORECASE</c>, as <c>lstrcmpiW</c> does). The tree uses it for
/// sibling keys, so non-ASCII letters, digit runs and symbols sort as they do in classic (VIEW-009, OQ-25).
/// </summary>
public static class RegistryNameOrder
{
    /// <summary>The comparer for key and value names in classic display order.</summary>
    public static IComparer<string> Comparer => RegistryFindCollation.Comparer;
}
