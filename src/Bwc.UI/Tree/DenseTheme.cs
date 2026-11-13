using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Tree;

/// <summary>
/// Shared brushes for the dense tree and list rows. Each brush is a live palette brush from
/// <see cref="BwcBrushes"/>: BwcTheme writes its color when the palette or a pane focus changes, so
/// the accessors return the same instance every time and nothing caches a stale color. Reset is
/// kept for existing callers and does nothing.
/// </summary>
internal static class DenseTheme
{
    private static Brush? _transparent;

    /// <summary>Tree selection fill. COLOR_HIGHLIGHT when the tree has focus, else COLOR_BTNFACE.</summary>
    public static Brush TreeSelectionBackground => BwcBrushes.TreeSelection;

    /// <summary>Text on the tree selection.</summary>
    public static Brush TreeSelectionForeground => BwcBrushes.TreeSelectionText;

    /// <summary>Border of the selected tree row. Transparent when the palette draws none.</summary>
    public static Brush TreeSelectionBorder => BwcBrushes.TreeSelectionBorder;

    /// <summary>Focus rectangle on the selected tree row. Transparent unless the tree has focus.</summary>
    public static Brush TreeFocusRect => BwcBrushes.TreeFocusRect;

    /// <summary>List selection fill. COLOR_HIGHLIGHT when the list has focus, else COLOR_BTNFACE.</summary>
    public static Brush ListSelectionBackground => BwcBrushes.ListSelection;

    /// <summary>Text on the list selection.</summary>
    public static Brush ListSelectionForeground => BwcBrushes.ListSelectionText;

    /// <summary>Border of the single selected list row. Transparent on the classic list.</summary>
    public static Brush ListSelectionBorder => BwcBrushes.ListSelectionBorder;

    /// <summary>Focus rectangle on the single selected list row. Transparent unless the list has focus.</summary>
    public static Brush ListFocusRect => BwcBrushes.ListFocusRect;

    /// <summary>Always-focused selection fill, for rows outside the tree and list panes.</summary>
    public static Brush SelectionBackground => BwcBrushes.Selection;

    /// <summary>Text on <see cref="SelectionBackground"/>.</summary>
    public static Brush SelectionForeground => BwcBrushes.SelectionText;

    /// <summary>Window text (COLOR_WINDOWTEXT).</summary>
    public static Brush Text => BwcBrushes.WindowText;

    /// <summary>Gray text (COLOR_GRAYTEXT).</summary>
    public static Brush GrayText => BwcBrushes.GrayText;

    public static Brush Transparent =>
        _transparent ??= new SolidColorBrush(Colors.Transparent);

    /// <summary>Header column dividers (COLOR_3DSHADOW).</summary>
    public static Brush Divider => BwcBrushes.Shadow;

    /// <summary>Header and pane background (COLOR_WINDOW).</summary>
    public static Brush HeaderBackground => BwcBrushes.Window;

    /// <summary>Splitter and shell face (COLOR_3DFACE).</summary>
    public static Brush Face => BwcBrushes.Face;

    /// <summary>Kept for existing callers. The palette brushes are updated in place, so there is nothing to clear.</summary>
    public static void Reset()
    {
    }
}
