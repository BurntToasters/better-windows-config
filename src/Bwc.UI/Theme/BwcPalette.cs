using Windows.UI;

namespace Bwc.UI.Theme;

/// <summary>The palette in effect. HighContrast uses the same system colors as Light.</summary>
public enum BwcThemeKind
{
    Light,
    Dark,
    HighContrast,
}

/// <summary>The app override. System follows Windows, Light and Dark force a palette.</summary>
public enum BwcThemeOverride
{
    System,
    Light,
    Dark,
}

/// <summary>A pane of the classic window. Each pane has its own selection state.</summary>
public enum BwcPane
{
    Tree,
    List,
}

/// <summary>
/// One resolved set of colors. Light and HighContrast read the system color table with GetSysColor.
/// Dark is computed, with the Windows accent as the focused selection. Field names follow the
/// classic system color names so the mapping is easy to check against the oracle.
/// </summary>
public readonly record struct BwcPalette(
    BwcThemeKind Kind,
    Color Window,
    Color WindowText,
    Color Highlight,
    Color HighlightText,
    Color InactiveSelection,
    Color Face,
    Color GrayText,
    Color Shadow,
    Color Highlight3D);
