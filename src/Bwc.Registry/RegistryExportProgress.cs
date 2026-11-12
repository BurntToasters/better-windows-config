namespace Bwc.Registry;

/// <summary>The stage an export has reached when it reports progress.</summary>
public enum ExportPhase
{
    Starting,
    KeyExported,
    ValueExported,
    KeySkipped,
    Completed,
}

/// <summary>
/// One export progress report. <see cref="Path"/> is the key being written or skipped.
/// <see cref="ValueName"/> is set only for a value event, where null means "not a value".
/// Counts are running totals for the whole export, including every root of a whole-registry
/// export.
/// </summary>
public sealed record ExportProgress(
    ExportPhase Phase,
    RegistryPath Path,
    string? ValueName,
    int KeysExported,
    int ValuesExported,
    int KeysSkipped);
