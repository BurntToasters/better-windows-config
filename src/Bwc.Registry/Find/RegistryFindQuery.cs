namespace Bwc.Registry;

/// <summary>
/// One Find request. Matching uses the classic Windows NLS collation (case-insensitive). At
/// least one of Keys, Values, or Data must be enabled, and the search text cannot be empty.
/// </summary>
public sealed class RegistryFindQuery
{
    private static readonly IReadOnlyList<RegistryPath> DefaultRoots = Array.AsReadOnly(
        new[]
        {
            RegistryPath.Parse("HKCR"),
            RegistryPath.Parse("HKCU"),
            RegistryPath.Parse("HKLM"),
            RegistryPath.Parse("HKU"),
            RegistryPath.Parse("HKCC"),
        });

    public RegistryFindQuery(
        string searchText,
        bool lookAtKeys = false,
        bool lookAtValues = false,
        bool lookAtData = false,
        bool matchWholeStringOnly = false,
        bool searchInSubtree = true,
        IReadOnlyList<RegistryPath>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(searchText);

        if (searchText.Length == 0)
        {
            throw new ArgumentException("The search text must not be empty.", nameof(searchText));
        }

        if (!lookAtKeys && !lookAtValues && !lookAtData)
        {
            throw new ArgumentException(
                "At least one of Keys, Values, or Data must be searched.",
                nameof(lookAtKeys));
        }

        if (roots is not null && roots.Count == 0)
        {
            throw new ArgumentException("At least one search root is required.", nameof(roots));
        }

        SearchText = searchText;
        LookAtKeys = lookAtKeys;
        LookAtValues = lookAtValues;
        LookAtData = lookAtData;
        MatchWholeStringOnly = matchWholeStringOnly;
        SearchInSubtree = searchInSubtree;
        Roots = roots is null ? DefaultRoots : CopyRoots(roots);
    }

    /// <summary>
    /// Builds a query from the classic Find option bits. This is the overload the persisted
    /// <c>FindFlags</c> DWORD feeds through <see cref="RegistryFindFlagsCodec"/>.
    /// </summary>
    public RegistryFindQuery(
        string searchText,
        RegistryFindFlags flags,
        IReadOnlyList<RegistryPath>? roots = null)
        : this(
            searchText,
            (flags & RegistryFindFlags.Keys) != 0,
            (flags & RegistryFindFlags.Values) != 0,
            (flags & RegistryFindFlags.Data) != 0,
            (flags & RegistryFindFlags.MatchWholeString) != 0,
            (flags & RegistryFindFlags.SearchInSubtree) != 0,
            roots)
    {
    }

    public string SearchText { get; }

    /// <summary>The classic Find option bits for this query.</summary>
    public RegistryFindFlags Flags
    {
        get
        {
            RegistryFindFlags flags = RegistryFindFlags.None;

            if (LookAtKeys)
            {
                flags |= RegistryFindFlags.Keys;
            }

            if (LookAtValues)
            {
                flags |= RegistryFindFlags.Values;
            }

            if (LookAtData)
            {
                flags |= RegistryFindFlags.Data;
            }

            if (MatchWholeStringOnly)
            {
                flags |= RegistryFindFlags.MatchWholeString;
            }

            if (SearchInSubtree)
            {
                flags |= RegistryFindFlags.SearchInSubtree;
            }

            return flags;
        }
    }

    public bool LookAtKeys { get; }

    public bool LookAtValues { get; }

    public bool LookAtData { get; }

    public bool MatchWholeStringOnly { get; }

    /// <summary>
    /// Provisional because OQ-10 records that regedit's checkbox default and exact effect are
    /// unknown. When false, only each starting key is searched and no child keys are visited.
    /// </summary>
    public bool SearchInSubtree { get; }

    /// <summary>
    /// Search roots in order. Defaults to HKCR, HKCU, HKLM, HKU, HKCC. A caller can pass a
    /// narrower key, such as one sandbox subtree, to bound the search.
    /// </summary>
    public IReadOnlyList<RegistryPath> Roots { get; }

    private static IReadOnlyList<RegistryPath> CopyRoots(IReadOnlyList<RegistryPath> roots)
    {
        var copy = new RegistryPath[roots.Count];

        for (int index = 0; index < roots.Count; index += 1)
        {
            RegistryPath root = roots[index];
            ArgumentNullException.ThrowIfNull(root, nameof(roots));
            copy[index] = root;
        }

        return Array.AsReadOnly(copy);
    }
}
