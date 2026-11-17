namespace Bwc.UI.Dialogs;

/// <summary>Which detail viewer a requirement opens: Memory (1601), Port (1700), Interrupt (1800), or
/// DMA (1900).</summary>
public enum ResourceDetailKind
{
    Memory,
    Port,
    Interrupt,
    Dma,
}

/// <summary>One partial descriptor of a resource descriptor, with the union decoded for its type. Plain
/// data for the viewers.</summary>
public sealed record ResourcePartialView(
    byte Type,
    byte ShareDisposition,
    ushort Flags,
    ulong Start,
    ulong Length,
    uint Level,
    uint Vector,
    ulong Affinity,
    uint Channel,
    uint Port,
    uint Reserved1,
    uint Reserved2,
    uint DataSize,
    byte[]? DeviceData);

/// <summary>One full resource descriptor (a row of Resource Lists, and the content of Resources).</summary>
public sealed record ResourceFullView(uint InterfaceType, uint BusNumber, ushort Version, ushort Revision, IReadOnlyList<ResourcePartialView> Partials);

/// <summary>One requirement descriptor with its position in the requirements list.</summary>
public sealed record ResourceRequirementView(
    int AlternativeList,
    int ResourceList,
    int Descriptor,
    byte Option,
    byte Type,
    byte ShareDisposition,
    ushort Flags,
    uint Length,
    uint Alignment,
    ulong Minimum,
    ulong Maximum,
    uint MinimumVector,
    uint MaximumVector,
    uint MinimumChannel,
    uint MaximumChannel);

/// <summary>The requirements list header and its descriptors.</summary>
public sealed record ResourceRequirementsView(uint InterfaceType, uint BusNumber, uint SlotNumber, IReadOnlyList<ResourceRequirementView> Rows);

/// <summary>
/// Cell and field texts of the resource viewers. Names come from the classic RT_STRING 7500 to 7583 entries in
/// Bwc.UI's resources. Number formats are inferred from the classic string formats (see
/// docs/failure-modes/resource-values.md).
/// </summary>
public static class ResourceText
{
    private const int MaxBusType = 11;
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Get(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out string? text))
            {
                text = BwcStrings.Get(key);
                Cache[key] = text;
            }

            return text;
        }
    }

    /// <summary>The bus type name for an INTERFACE_TYPE value, "Invalid" for values outside 0 to 11 and for
    /// -1.</summary>
    public static string InterfaceType(uint type) =>
        type <= MaxBusType ? Get("ResourceBusType" + type.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Get("ResourceInvalid");

    /// <summary>The share disposition name (Undetermined, Device Exclusive, Driver Exclusive, Shared), or
    /// Invalid.</summary>
    public static string ShareDisposition(byte share) => share switch
    {
        0 => Get("ResourceUndetermined"),
        1 => Get("ResourceDeviceExclusive"),
        2 => Get("ResourceDriverExclusive"),
        3 => Get("ResourceShared"),
        _ => Get("ResourceInvalid"),
    };

    /// <summary>The partial descriptor type name, or Invalid for an unnamed type.</summary>
    public static string PartialType(byte type) => type switch
    {
        1 => Get("ResourcePortResource"),
        2 => Get("ResourceInterrupt"),
        3 => Get("ResourceMemoryResource"),
        4 => Get("ResourceDma"),
        5 => Get("ResourceDeviceSpecific"),
        _ => Get("ResourceInvalid"),
    };

    /// <summary>Memory access from the descriptor flags: Read / Write, Read Only, or Write Only.</summary>
    public static string MemoryAccess(ushort flags) => (flags & 3) switch
    {
        0 => Get("ResourceReadWrite"),
        1 => Get("ResourceReadOnly"),
        2 => Get("ResourceWriteOnly"),
        _ => Get("ResourceInvalid"),
    };

    /// <summary>Interrupt type: Latched when the latched flag is set, otherwise Level Sensitive.</summary>
    public static string InterruptType(ushort flags) =>
        (flags & 1) != 0 ? Get("ResourceLatched") : Get("ResourceLevelSensitive");

    /// <summary>Port type: "Port" when the I/O flag is set, otherwise "Memory".</summary>
    public static string PortType(ushort flags) =>
        (flags & 1) != 0 ? Get("ResourcePortType") : Get("ResourceMemoryType");

    /// <summary>A 32-bit or 64-bit address or length as 0x plus at least eight lowercase hex digits.</summary>
    public static string Hex(ulong value) => "0x" + value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A signed decimal, as classic shows a bus number of 0xFFFFFFFF (-1).</summary>
    public static string Signed(uint value) => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Decimal(ulong value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
