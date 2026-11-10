namespace Bwc.Oracle.Resources.Model;

// Shapes of regedit-resources.json. Property declaration order is JSON order.
// Null properties are omitted from the JSON. Every field is a plain value so the output is deterministic.

/// A numeric "sz_Or_Ord" value: a resource ordinal, with a well-known name when one applies (window classes).
internal sealed record OrdinalRef(int Ordinal, string? Name);

internal sealed class GoldenFile
{
    public HeaderInfo Header { get; set; } = new();
    public List<ModuleDump> Modules { get; set; } = [];
}

internal sealed class HeaderInfo
{
    public string Generator { get; set; } = "Bwc.Oracle.Resources";
    public int SchemaVersion { get; set; } = 1;
    public List<SourceInfo> Sources { get; set; } = [];
}

internal sealed class SourceInfo
{
    public string Module { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? FileVersion { get; set; }
    public string? ProductVersion { get; set; }
    public string? ProductName { get; set; }
    public string? FileDescription { get; set; }
    public string? OriginalFileName { get; set; }
    public string? InternalName { get; set; }
    public string? CompanyName { get; set; }
}

internal sealed class ModuleDump
{
    public string Module { get; set; } = "";
    public string Path { get; set; } = "";
    public int ErrorCount { get; set; }
    public List<TypeInventory> ResourceTypes { get; set; } = [];
    public List<MenuRecord> Menus { get; set; } = [];
    public List<DialogRecord> Dialogs { get; set; } = [];
    public List<AcceleratorRecord> Accelerators { get; set; } = [];
    public List<StringBlockRecord> StringTables { get; set; } = [];
}

internal sealed class TypeInventory
{
    public string Type { get; set; } = "";
    public int? TypeId { get; set; }
    public List<InventoryEntry> Resources { get; set; } = [];
}

internal sealed class InventoryEntry
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int Language { get; set; }
    public string LanguageTag { get; set; } = "";
    public uint ByteLength { get; set; }
    public string? Error { get; set; }
}

/// Fields shared by every parsed resource record.
internal abstract class ResourceRecord
{
    public string Type { get; set; } = "";
    public int? TypeId { get; set; }
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int Language { get; set; }
    public string LanguageTag { get; set; } = "";
    public uint ByteLength { get; set; }
    public string? Error { get; set; }
}

internal sealed class MenuRecord : ResourceRecord
{
    /// "MENU" (classic) or "MENUEX".
    public string? Template { get; set; }
    public int? TemplateVersion { get; set; }
    /// MENUEX header dwHelpId.
    public uint? HelpId { get; set; }
    public List<MenuItemRecord>? Items { get; set; }
}

internal sealed class MenuItemRecord
{
    /// Command id. Zero for classic popup items (they carry no id in the template).
    public uint Id { get; set; }
    /// Label, with '&' mnemonics and any "\t" accelerator text kept exactly.
    public string Text { get; set; } = "";
    /// MFT_SEPARATOR / MF_SEPARATOR, or (classic only) an empty, id-0, non-popup item. The raw flags stay in Flags/Type.
    public bool Separator { get; set; }
    public bool Popup { get; set; }
    /// Classic MENU only: raw MF_* option word and decoded names.
    public string? Flags { get; set; }
    public List<string>? FlagNames { get; set; }
    /// MENUEX only: dwType, dwState and bResInfo (raw) with decoded names.
    public string? Type { get; set; }
    public List<string>? TypeNames { get; set; }
    public string? State { get; set; }
    public List<string>? StateNames { get; set; }
    public string? ResInfo { get; set; }
    /// MENUEX popup items only.
    public uint? HelpId { get; set; }
    public List<MenuItemRecord>? Children { get; set; }
}

internal sealed class DialogRecord : ResourceRecord
{
    /// "DIALOG" or "DIALOGEX".
    public string? Template { get; set; }
    public string? Style { get; set; }
    public string? ExStyle { get; set; }
    /// DIALOGEX only.
    public uint? HelpId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Cx { get; set; }
    public int Cy { get; set; }
    public int? DeclaredControlCount { get; set; }
    /// string, or OrdinalRef, or null when the template has no menu.
    public object? Menu { get; set; }
    /// string, or OrdinalRef (with name for the standard atoms), or null for the default class.
    public object? WindowClass { get; set; }
    public string? Title { get; set; }
    public FontRecord? Font { get; set; }
    public List<ControlRecord>? Controls { get; set; }
}

internal sealed class FontRecord
{
    public int PointSize { get; set; }
    /// DIALOGEX only.
    public int? Weight { get; set; }
    /// DIALOGEX only.
    public bool? Italic { get; set; }
    /// DIALOGEX only.
    public int? Charset { get; set; }
    public string Typeface { get; set; } = "";
}

internal sealed class ControlRecord
{
    public uint Id { get; set; }
    /// DIALOGEX only.
    public uint? HelpId { get; set; }
    public string ExStyle { get; set; } = "";
    public string Style { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Cx { get; set; }
    public int Cy { get; set; }
    /// string, or OrdinalRef with name for the standard atoms (0x80 Button ... 0x85 ComboBox).
    public object? WindowClass { get; set; }
    /// string, or OrdinalRef, or null when empty.
    public object? Title { get; set; }
    public int CreationDataLength { get; set; }
}

internal sealed class AcceleratorRecord : ResourceRecord
{
    public List<AcceleratorEntry>? Entries { get; set; }
}

internal sealed class AcceleratorEntry
{
    public int Key { get; set; }
    /// "VK_F5", "'A'" for virtual keys; "'x'" for ASCII (non-FVIRTKEY) entries.
    public string KeyName { get; set; } = "";
    public string Flags { get; set; } = "";
    public List<string> FlagNames { get; set; } = [];
    public int Command { get; set; }
}

internal sealed class StringBlockRecord : ResourceRecord
{
    public List<StringEntry>? Strings { get; set; }
}

internal sealed class StringEntry
{
    /// Real string id: (blockId - 1) * 16 + slot.
    public int Id { get; set; }
    public string Text { get; set; } = "";
}
