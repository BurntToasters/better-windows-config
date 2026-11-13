using System.Text.Json.Serialization;

namespace Bwc.Regedit.Registry;

/// <summary>The Find dialog look-at flags, persisted as one integer.</summary>
[Flags]
public enum RegeditFindFlags
{
    None = 0,
    Keys = 1,
    Values = 2,
    Data = 4,
    MatchWholeString = 8,
    SearchInSubtree = 16,
    Default = Keys | Values | Data | SearchInSubtree,
}

/// <summary>Persisted Registry Editor view state. Only written when the window closes.</summary>
public sealed class RegeditSettings
{
    /// <summary>Display path of the selected key, or <c>Computer</c> for the virtual root.</summary>
    public string LastKey { get; set; } = "Computer";

    /// <summary>True when the address bar row is showing.</summary>
    public bool AddressBarVisible { get; set; } = true;

    /// <summary>List column widths keyed by Name, Type, and Data.</summary>
    public Dictionary<string, double> ListColumnWidths { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The last text shown in the Find dialog.</summary>
    public string FindText { get; set; } = string.Empty;

    /// <summary>The last Find look-at and scope flags, as <see cref="RegeditFindFlags"/>.</summary>
    public int FindFlags { get; set; } = (int)RegeditFindFlags.Default;

    /// <summary>The query text that produced <see cref="FindLastHitKey"/>, if any.</summary>
    public string FindQueryText { get; set; } = string.Empty;

    /// <summary>The look-at flags of the query that produced the last hit.</summary>
    public int FindQueryFlags { get; set; }

    /// <summary>Display path of the last hit key, or null when there is no persisted hit.</summary>
    public string? FindLastHitKey { get; set; }

    /// <summary>
    /// Raw name of the last hit value. Null for a key-name hit, the empty string for the default
    /// value, and the registry name otherwise.
    /// </summary>
    public string? FindLastHitValue { get; set; }

    /// <summary>The last hit kind, matching RegistryFindHitKind: 0 key, 1 name, 2 data. -1 is none.</summary>
    public int FindLastHitKind { get; set; } = -1;

    /// <summary>The text the finder matched for the last hit.</summary>
    public string? FindLastHitText { get; set; }

    /// <summary>Left edge of the normal (not maximized) window in physical pixels. Null until saved.</summary>
    public int? WindowX { get; set; }

    /// <summary>Top edge of the normal window in physical pixels. Null until saved.</summary>
    public int? WindowY { get; set; }

    /// <summary>Width of the normal window in physical pixels. Null until saved.</summary>
    public int? WindowWidth { get; set; }

    /// <summary>Height of the normal window in physical pixels. Null until saved.</summary>
    public int? WindowHeight { get; set; }

    /// <summary>True when the window was maximized at close. The normal rectangle is kept beside it.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// The tree pane width in DIPs at close. The splitter is set to it at startup. Null when no width was saved.
    /// </summary>
    public double? TreePaneWidth { get; set; }

    /// <summary>The font size in DIPs chosen with View > Font. 12 is the oracle size (9 point).</summary>
    public double FontSize { get; set; } = 12;

    /// <summary>The View > Theme choice: System, Light, or Dark.</summary>
    public string ThemeOverride { get; set; } = "System";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RegeditSettings))]
public partial class RegeditSettingsContext : JsonSerializerContext;
