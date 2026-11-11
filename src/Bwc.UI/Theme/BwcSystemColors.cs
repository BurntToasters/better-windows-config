using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace Bwc.UI.Theme;

/// <summary>
/// Reads the system color table and builds the palettes. Light and HighContrast are the system
/// colors. Dark is a fixed neutral set plus the Windows accent. Nothing here touches XAML, so it
/// can be called from any thread, but <see cref="BwcTheme"/> calls it on the UI thread.
/// </summary>
internal static class BwcSystemColors
{
    private static readonly Color DarkWindow = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);
    private static readonly Color DarkWindowText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkInactiveSelection = Color.FromArgb(0xFF, 0x3F, 0x3F, 0x46);
    private static readonly Color DarkFace = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);
    private static readonly Color DarkGrayText = Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C);
    private static readonly Color DarkShadow = Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A);
    private static readonly Color DarkHighlight3D = Color.FromArgb(0xFF, 0x5C, 0x5C, 0x5C);
    private static readonly Color Black = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

    /// <summary>The border of a selection that draws none. Transparent, so the brush shows nothing.</summary>
    private static readonly Color NoBorder = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

    /// <summary>
    /// The palette made from GetSysColor. The light palette takes the tree selection from the
    /// Explorer visual style when it can be read, and falls back to the system colors. HighContrast
    /// always uses the system colors.
    /// </summary>
    public static BwcPalette SystemPalette(BwcThemeKind kind)
    {
        Color window = SystemColor(SYS_COLOR_INDEX.COLOR_WINDOW);
        Color windowText = SystemColor(SYS_COLOR_INDEX.COLOR_WINDOWTEXT);
        Color highlight = SystemColor(SYS_COLOR_INDEX.COLOR_HIGHLIGHT);
        Color highlightText = SystemColor(SYS_COLOR_INDEX.COLOR_HIGHLIGHTTEXT);
        Color btnFace = SystemColor(SYS_COLOR_INDEX.COLOR_BTNFACE);

        // Fallback: the classic system colors, as the tree used before the Explorer read.
        var treeFocused = new BwcSelection(highlight, NoBorder, highlightText);
        var treeInactive = new BwcSelection(btnFace, NoBorder, windowText);
        if (kind == BwcThemeKind.Light
            && BwcExplorerSelection.TryReadTreeSelection(out ExplorerPaint focused, out ExplorerPaint unfocused))
        {
            treeFocused = FromExplorer(focused, windowText);
            treeInactive = FromExplorer(unfocused, windowText);
        }

        // The classic list view draws COLOR_HIGHLIGHT when focused and hides the selection otherwise.
        return new(
            kind,
            Window: window,
            WindowText: windowText,
            Highlight: highlight,
            HighlightText: highlightText,
            InactiveSelection: btnFace,
            Face: SystemColor(SYS_COLOR_INDEX.COLOR_3DFACE),
            GrayText: SystemColor(SYS_COLOR_INDEX.COLOR_GRAYTEXT),
            Shadow: SystemColor(SYS_COLOR_INDEX.COLOR_3DSHADOW),
            Highlight3D: SystemColor(SYS_COLOR_INDEX.COLOR_3DHILIGHT),
            TreeFocused: treeFocused,
            TreeInactive: treeInactive,
            ListFocused: new BwcSelection(highlight, NoBorder, highlightText),
            ListInactive: new BwcSelection(window, NoBorder, windowText));
    }

    /// <summary>
    /// The dark palette. The focused selection is the Windows accent, and its text is black on a
    /// light accent so the selected label always has contrast. Both states use a fill and a border,
    /// and the dark border is transparent.
    /// </summary>
    public static BwcPalette DarkPalette(Color accent)
    {
        Color highlightText = Luminance(accent) > 0.6 ? Black : DarkWindowText;
        var focused = new BwcSelection(accent, NoBorder, highlightText);
        var inactive = new BwcSelection(DarkInactiveSelection, NoBorder, DarkWindowText);
        return new(
            BwcThemeKind.Dark,
            Window: DarkWindow,
            WindowText: DarkWindowText,
            Highlight: accent,
            HighlightText: highlightText,
            InactiveSelection: DarkInactiveSelection,
            Face: DarkFace,
            GrayText: DarkGrayText,
            Shadow: DarkShadow,
            Highlight3D: DarkHighlight3D,
            TreeFocused: focused,
            TreeInactive: inactive,
            ListFocused: focused,
            ListInactive: inactive);
    }

    /// <summary>
    /// Converts an Explorer paint to a selection. The theme text color is used when the theme sets
    /// one, and COLOR_WINDOWTEXT otherwise.
    /// </summary>
    private static BwcSelection FromExplorer(ExplorerPaint paint, Color windowText) =>
        new(paint.Fill, paint.Border ?? NoBorder, paint.Text ?? windowText);

    /// <summary>True when the Windows apps background is dark, using UISettings.</summary>
    public static bool SystemAppsAreDark(UISettings settings) =>
        Luminance(settings.GetColorValue(UIColorType.Background)) < 0.5;

    public static Color Accent(UISettings settings) =>
        settings.GetColorValue(UIColorType.Accent);

    /// <summary>Relative luminance in the range 0 to 1, from the sRGB channels.</summary>
    public static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static Color SystemColor(SYS_COLOR_INDEX index)
    {
        // GetSysColor returns a COLORREF: red in the low byte, then green, then blue.
        uint colorRef = PInvoke.GetSysColor(index);
        return Color.FromArgb(
            0xFF,
            (byte)(colorRef & 0xFF),
            (byte)((colorRef >> 8) & 0xFF),
            (byte)((colorRef >> 16) & 0xFF));
    }
}
