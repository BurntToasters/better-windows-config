namespace Bwc.Registry.Resources;

/// <summary>
/// The classic resource names that the text export prints. The names are English constants so that Bwc.Registry
/// does not depend on Bwc.UI. Bwc.UI keeps its own copy in ResourceText, which must stay equal (export.md, section 2,
/// open point 8).
/// </summary>
public static class ResourceNames
{
    private const string Invalid = "Invalid";

    private static readonly string[] BusTypes =
    [
        "Internal",
        "Isa",
        "Eisa",
        "Micro Channel",
        "Turbo Channel",
        "PCI",
        "VME",
        "Nu",
        "PCMCIA",
        "C Bus",
        "MPI",
        "MPSA",
    ];

    /// <summary>The bus name for an INTERFACE_TYPE value from 0 to 11, and Invalid for any other value.</summary>
    public static string InterfaceType(uint type) => type < BusTypes.Length ? BusTypes[type] : Invalid;

    public static string ShareDisposition(byte share) => share switch
    {
        0 => "Undetermined",
        1 => "Device Exclusive",
        2 => "Driver Exclusive",
        3 => "Shared",
        _ => Invalid,
    };

    /// <summary>The partial descriptor type name, or Invalid for a type with no name (0, 6, 7, and unknown).</summary>
    public static string PartialType(byte type) => type switch
    {
        1 => "Port",
        2 => "Interrupt",
        3 => "Memory",
        4 => "DMA",
        5 => "Device Specific",
        _ => Invalid,
    };

    /// <summary>Memory access from the descriptor flags: Read / Write, Read Only, or Write Only.</summary>
    public static string MemoryAccess(ushort flags) => (flags & 3) switch
    {
        0 => "Read / Write",
        1 => "Read Only",
        2 => "Write Only",
        _ => Invalid,
    };

    public static string InterruptType(ushort flags) => (flags & 1) != 0 ? "Latched" : "Level Sensitive";

    public static string PortType(ushort flags) => (flags & 1) != 0 ? "Port" : "Memory";
}
