using System.Text.Json.Serialization;

namespace Bwc.Gpedit.Policy;

/// <summary>
/// Persisted Local Group Policy Editor view state, written when the window closes and shortly after a change settles.
/// It holds what the Registry Editor also keeps (window rectangle, tree width, column widths) plus the view mode and the
/// Extended or Standard tab. The last selected node is not persisted: classic opens on Local Computer Policy every time
/// (docs/failure-modes/gpedit-tree-list.md, section 11).
/// </summary>
public sealed class GpeditSettings
{
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

    /// <summary>The tree pane width in DIPs, or null when none was saved.</summary>
    public double? TreePaneWidth { get; set; }

    /// <summary>List column widths in DIPs by column id (setting, state, comment, name).</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The list view mode by name (LargeIcons, SmallIcons, List, Detail), or null for the default.</summary>
    public string? ViewMode { get; set; }

    /// <summary>The selected tab by name (Extended or Standard), or null for the default.</summary>
    public string? ExtendedTab { get; set; }

    /// <summary>
    /// The sort of each list by node: the names of the node from the root joined with " > ", then the columns sorted so far,
    /// oldest first, each as "column id:Ascending" or "column id:Descending". A click sorts the order shown now, so the
    /// whole list of clicks is what restores an order (docs/failure-modes/gpedit-filter.md, section 15).
    /// </summary>
    public Dictionary<string, List<string>> Sorts { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GpeditSettings))]
internal sealed partial class GpeditSettingsContext : JsonSerializerContext;
