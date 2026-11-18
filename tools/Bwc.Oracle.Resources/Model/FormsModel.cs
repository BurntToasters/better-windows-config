namespace Bwc.Oracle.Resources.Model;

// Shapes of gpedit-forms.json (the managed WinForms resources). Property declaration order is JSON order.
// Null properties are omitted. Everything here is Microsoft content except the identifiers, so the file is untracked.

internal sealed class FormsFile
{
    public string Generator { get; set; } = "Bwc.Oracle.Resources";
    public int SchemaVersion { get; set; } = 1;
    public List<ManagedAssemblyInfo> Assemblies { get; set; } = [];
    public List<ManagedResourceFile> ResourceFiles { get; set; } = [];
    public List<SkippedManagedResource> Skipped { get; set; } = [];
}

internal sealed class ManagedAssemblyInfo
{
    public string Assembly { get; set; } = "";
    /// "main" or "satellite".
    public string Role { get; set; } = "";
    public string? Culture { get; set; }
    /// The GAC root folder the copy came from (GAC_MSIL, GAC_64, GAC_Arm64, GAC_32).
    public string GacRoot { get; set; } = "";
    public string VersionFolder { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    /// Other GAC copies of the same assembly that were not used.
    public List<OtherCopy> OtherCopies { get; set; } = [];
}

internal sealed class OtherCopy
{
    public string GacRoot { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// One .resources stream from a manifest resource.
internal sealed class ManagedResourceFile
{
    public string Assembly { get; set; } = "";
    public string Name { get; set; } = "";
    public int EntryCount { get; set; }
    public string? Error { get; set; }
    public List<ManagedResourceEntry> Entries { get; set; } = [];
}

internal sealed class ManagedResourceEntry
{
    public string Name { get; set; } = "";
    /// The type name from the resource type table, or ResourceTypeCode.* for primitives and strings.
    public string Type { get; set; } = "";
    /// string, primitive, object (decoded serialized object), bytes (raw), or error.
    public string Kind { get; set; } = "";
    /// Decoded string or primitive, or the readable form of a decoded object ("5, 6" for a Point).
    public object? Value { get; set; }
    /// Decoded fields of a serialized object, in serialized member order.
    public List<DecodedField>? Fields { get; set; }
    /// Raw bytes when the value was not decoded.
    public string? Base64 { get; set; }
    public int ByteLength { get; set; }
    public string? Error { get; set; }
}

internal sealed record DecodedField(string Name, object? Value);

internal sealed class SkippedManagedResource
{
    public string Assembly { get; set; } = "";
    public string Name { get; set; } = "";
    public long ByteLength { get; set; }
    public string Reason { get; set; } = "";
}
