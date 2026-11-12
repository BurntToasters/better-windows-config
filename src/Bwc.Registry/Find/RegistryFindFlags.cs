namespace Bwc.Registry;

/// <summary>
/// The classic regedit Find option bits. The dialog's mask table maps controls 1151, 1152, 1153,
/// 1154, and 1161 to these values. The high word is not part of the classic Find dialog and is
/// ignored. Windows 11 build 26100.8457 reads and writes the same persisted <c>FindFlags</c>
/// DWORD on exit without updating it from the dialog, so BWC must write the mask itself.
/// </summary>
[Flags]
public enum RegistryFindFlags : uint
{
    None = 0,

    /// <summary>Match whole string only.</summary>
    MatchWholeString = 0x00000001,

    /// <summary>Look at key names.</summary>
    Keys = 0x00000002,

    /// <summary>Look at value names.</summary>
    Values = 0x00000004,

    /// <summary>Look at value data.</summary>
    Data = 0x00000008,

    /// <summary>Search in subtree.</summary>
    SearchInSubtree = 0x00000010,

    /// <summary>
    /// The default when the persisted DWORD is missing or invalid. The subtree choice remains
    /// OQ-10; this matches the Registry Editor's current default.
    /// </summary>
    Default = Keys | Values | Data | SearchInSubtree,
}

/// <summary>
/// Converts between the classic <c>FindFlags</c> DWORD and <see cref="RegistryFindQuery"/>.
/// The codec performs no registry I/O, so callers read and write the DWORD through
/// <see cref="RegistryReader"/> and <see cref="RegistryWriter"/> without bypassing a write policy.
/// </summary>
public static class RegistryFindFlagsCodec
{
    /// <summary>The classic low word. Bits above it are not part of the Find dialog state.</summary>
    public const uint ClassicMask = 0x0000001F;

    /// <summary>The value name classic regedit uses under the Regedit applet key.</summary>
    public const string ValueName = "FindFlags";

    /// <summary>Decodes the classic low word. Unknown and high bits are discarded.</summary>
    public static RegistryFindFlags Decode(uint value) => (RegistryFindFlags)(value & ClassicMask);

    /// <summary>
    /// Decodes the stored value. A missing value, a non-DWORD type, or a length other than four
    /// bytes returns <see cref="RegistryFindFlags.Default"/> instead of guessing from other bytes.
    /// </summary>
    public static RegistryFindFlags DecodeValue(RegistryValueInfo? value)
    {
        if (value is null
            || value.Type != (uint)RegistryValueType.Dword
            || value.DataLength != sizeof(uint))
        {
            return RegistryFindFlags.Default;
        }

        return Decode(RegistryValueCodec.DecodeDword(value.Data));
    }

    /// <summary>Encodes the classic low word.</summary>
    public static uint Encode(RegistryFindFlags flags) => (uint)flags & ClassicMask;

    /// <summary>
    /// Builds a query from a persisted DWORD. A DWORD with none of Keys, Values, or Data set is
    /// rejected by <see cref="RegistryFindQuery"/>, because such a search cannot produce a hit.
    /// </summary>
    public static RegistryFindQuery CreateQuery(
        string searchText,
        uint findFlags,
        IReadOnlyList<RegistryPath>? roots = null) =>
        new(searchText, Decode(findFlags), roots);

    /// <summary>Returns the classic DWORD for a query that produced or will produce a hit.</summary>
    public static uint FromQuery(RegistryFindQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Encode(query.Flags);
    }
}
