namespace Bwc.Registry;

/// <summary>
/// The Win32 registry value types. The numeric values match the REG_* constants so a raw
/// <see cref="uint"/> type can be cast without a lookup.
/// </summary>
public enum RegistryValueType : uint
{
    None = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    Dword = 4,
    DwordBigEndian = 5,
    Link = 6,
    MultiString = 7,
    ResourceList = 8,
    FullResourceDescriptor = 9,
    ResourceRequirementsList = 10,
    Qword = 11,
}

public static class RegistryValueTypeExtensions
{
    /// <summary>
    /// Returns the display name used by the oracle regedit string table. Types that the table
    /// does not name, including type 5, are reported as REG_UNKNOWN.
    /// </summary>
    public static string ToTypeName(uint type) => type switch
    {
        0 => "REG_NONE",
        1 => "REG_SZ",
        2 => "REG_EXPAND_SZ",
        3 => "REG_BINARY",
        4 => "REG_DWORD",
        6 => "REG_LINK",
        7 => "REG_MULTI_SZ",
        8 => "REG_RESOURCE_LIST",
        9 => "REG_FULL_RESOURCE_DESCRIPTOR",
        10 => "REG_RESOURCE_REQUIREMENTS_LIST",
        11 => "REG_QWORD",
        _ => "REG_UNKNOWN",
    };

    /// <summary>
    /// A friendlier display name may replace the Win32 name later. Until the UI owns that
    /// mapping, this stays identical to <see cref="ToTypeName(uint)"/>.
    /// </summary>
    public static string ToDisplayName(uint type) => ToTypeName(type);

    public static string ToDisplayName(this RegistryValueType type) => ToTypeName((uint)type);
}
