namespace Bwc.UI;

/// <summary>
/// Diagnostics hook for the shared UI library. Bwc.UI does not reference Bwc.Core, so each app sets
/// <see cref="Warn"/> at startup to its own logger. When it is null, warnings are dropped.
/// </summary>
public static class UiDiagnostics
{
    /// <summary>Receives a warning message. Null means the warning is dropped.</summary>
    public static Action<string>? Warn { get; set; }
}
