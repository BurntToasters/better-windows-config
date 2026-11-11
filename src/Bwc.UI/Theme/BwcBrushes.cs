
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Bwc.UI.Theme;

/// <summary>Pane selection state reported by the shell.</summary>
internal readonly record struct BwcPaneState(bool HasFocus, bool ShowFocusRect);

/// <summary>
/// The one place that owns palette brushes. Each brush is a SolidColorBrush declared in
/// Themes/Dense.xaml. A lookup returns the same instance every time, and a palette change writes
/// Color on that instance, so realized rows and XAML bindings change without being re-applied.
/// If an app did not merge Dense.xaml, the brush is created and registered in the application
/// resources so the palette still applies.
/// </summary>
internal static class BwcBrushes
{
    private const string WindowKey = "BwcWindowBrush";
    private const string WindowTextKey = "BwcWindowTextBrush";
    private const string FaceKey = "BwcFaceBrush";
    private const string ShadowKey = "BwcShadowBrush";
    private const string Highlight3DKey = "BwcHighlight3DBrush";
    private const string GrayTextKey = "BwcGrayTextBrush";
    private const string SelectionKey = "BwcSelectionBrush";
    private const string SelectionTextKey = "BwcSelectionTextBrush";
    private const string TreeSelectionKey = "BwcTreeSelectionBrush";
    private const string TreeSelectionTextKey = "BwcTreeSelectionTextBrush";
    private const string TreeFocusRectKey = "BwcTreeFocusRectBrush";
    private const string TreeSelectionBorderKey = "BwcTreeSelectionBorderBrush";
    private const string ListSelectionKey = "BwcListSelectionBrush";
    private const string ListSelectionTextKey = "BwcListSelectionTextBrush";
    private const string ListFocusRectKey = "BwcListFocusRectBrush";
    private const string ListSelectionBorderKey = "BwcListSelectionBorderBrush";

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    /// <summary>Pane and header background, text boxes, menu bar (COLOR_WINDOW).</summary>
    public static SolidColorBrush Window => Get(WindowKey);

    /// <summary>Text on the window background (COLOR_WINDOWTEXT).</summary>
    public static SolidColorBrush WindowText => Get(WindowTextKey);

    /// <summary>Splitter and shell face (COLOR_3DFACE).</summary>
    public static SolidColorBrush Face => Get(FaceKey);

    /// <summary>Header dividers (COLOR_3DSHADOW).</summary>
    public static SolidColorBrush Shadow => Get(ShadowKey);

    /// <summary>Splitter edge highlight (COLOR_3DHILIGHT).</summary>
    public static SolidColorBrush Highlight3D => Get(Highlight3DKey);

    /// <summary>Gray text (COLOR_GRAYTEXT).</summary>
    public static SolidColorBrush GrayText => Get(GrayTextKey);

    /// <summary>Always-focused selection, used by the hex editor and other non-pane rows.</summary>
    public static SolidColorBrush Selection => Get(SelectionKey);

    /// <summary>Text on <see cref="Selection"/>.</summary>
    public static SolidColorBrush SelectionText => Get(SelectionTextKey);

    /// <summary>Tree selection fill: the Explorer tree selection when the tree has focus, else its unfocused fill.</summary>
    public static SolidColorBrush TreeSelection => Get(TreeSelectionKey);

    /// <summary>Tree selected text: COLOR_WINDOWTEXT on the light palette, the palette text otherwise.</summary>
    public static SolidColorBrush TreeSelectionText => Get(TreeSelectionTextKey);

    /// <summary>Tree selection border, one device pixel. Transparent when the palette draws none.</summary>
    public static SolidColorBrush TreeSelectionBorder => Get(TreeSelectionBorderKey);

    /// <summary>Tree focus rectangle: the selection text color on the focused row, else transparent.</summary>
    public static SolidColorBrush TreeFocusRect => Get(TreeFocusRectKey);

    /// <summary>List selection fill: COLOR_HIGHLIGHT when the list has focus, else COLOR_WINDOW (hidden).</summary>
    public static SolidColorBrush ListSelection => Get(ListSelectionKey);

    /// <summary>List selected text: COLOR_HIGHLIGHTTEXT when focused, else COLOR_WINDOWTEXT.</summary>
    public static SolidColorBrush ListSelectionText => Get(ListSelectionTextKey);

    /// <summary>List selection border, one device pixel. Transparent on the classic list.</summary>
    public static SolidColorBrush ListSelectionBorder => Get(ListSelectionBorderKey);

    /// <summary>List focus rectangle: shown only with one selected row and list focus.</summary>
    public static SolidColorBrush ListFocusRect => Get(ListFocusRectKey);

    /// <summary>Writes every brush for a palette and the current pane states.</summary>
    public static void Apply(BwcPalette palette, BwcPaneState tree, BwcPaneState list)
    {
        Window.Color = palette.Window;
        WindowText.Color = palette.WindowText;
        Face.Color = palette.Face;
        Shadow.Color = palette.Shadow;
        Highlight3D.Color = palette.Highlight3D;
        GrayText.Color = palette.GrayText;
        Selection.Color = palette.Highlight;
        SelectionText.Color = palette.HighlightText;
        ApplyPane(palette, BwcPane.Tree, tree);
        ApplyPane(palette, BwcPane.List, list);
    }

    /// <summary>Writes the selection, border and focus brushes of one pane. Rows are not touched.</summary>
    public static void ApplyPane(BwcPalette palette, BwcPane pane, BwcPaneState state)
    {
        BwcSelection selection = pane == BwcPane.Tree
            ? (state.HasFocus ? palette.TreeFocused : palette.TreeInactive)
            : (state.HasFocus ? palette.ListFocused : palette.ListInactive);
        Color focusRect = state.HasFocus && state.ShowFocusRect ? selection.Text : Microsoft.UI.Colors.Transparent;

        if (pane == BwcPane.Tree)
        {
            TreeSelection.Color = selection.Fill;
            TreeSelectionText.Color = selection.Text;
            TreeSelectionBorder.Color = selection.Border;
            TreeFocusRect.Color = focusRect;
        }
        else
        {
            ListSelection.Color = selection.Fill;
            ListSelectionText.Color = selection.Text;
            ListSelectionBorder.Color = selection.Border;
            ListFocusRect.Color = focusRect;
        }
    }

    private static SolidColorBrush Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resources = Application.Current?.Resources;
        if (resources is not null
            && resources.TryGetValue(key, out var value)
            && value is SolidColorBrush existing)
        {
            Cache[key] = existing;
            return existing;
        }

        var created = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources?.Add(key, created);
        Cache[key] = created;
        return created;
    }
}
