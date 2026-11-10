using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Tree;

/// <summary>
/// Shared brushes for the dense tree and list rows. Selection and text come from theme resources so
/// dark mode and high contrast keep working. The cache is reset when the active theme changes.
/// </summary>
internal static class DenseTheme
{
    private static Brush? _selectionBackground;
    private static Brush? _selectionForeground;
    private static Brush? _text;
    private static Brush? _transparent;

    public static Brush SelectionBackground =>
        _selectionBackground ??= Resolve(
            "ListViewItemBackgroundSelected",
            "AccentFillColorSecondaryBrush",
            "SystemControlHighlightAccentBrush",
            "SystemControlBackgroundAccentBrush");

    public static Brush SelectionForeground =>
        _selectionForeground ??= Resolve(
            "ListViewItemForegroundSelected",
            "TextOnAccentFillColorPrimaryBrush",
            "SystemControlForegroundAltHighBrush",
            "SystemControlHighlightAltChromeWhiteBrush");

    public static Brush Text =>
        _text ??= Resolve(
            "TextFillColorPrimaryBrush",
            "SystemControlForegroundBaseHighBrush",
            "SystemControlForegroundBaseMediumHighBrush");

    public static Brush Transparent =>
        _transparent ??= new SolidColorBrush(Colors.Transparent);

    public static Brush Divider =>
        Resolve("DividerStrokeColorDefaultBrush", "SystemControlForegroundBaseLowBrush");

    public static Brush HeaderBackground =>
        Resolve("LayerFillColorAltBrush", "SystemControlBackgroundAltHighBrush", "LayerFillColorDefaultBrush");

    public static void Reset()
    {
        _selectionBackground = null;
        _selectionForeground = null;
        _text = null;
        _transparent = null;
    }

    private static Brush Resolve(params string[] keys)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            foreach (var key in keys)
            {
                if (resources.TryGetValue(key, out var value) && value is Brush brush)
                {
                    return brush;
                }
            }
        }

        // Last resort. A missing theme resource should not crash a row; the E2E height checks do
        // not depend on color. Every normal WinUI theme provides at least one key above.
        return new SolidColorBrush(Colors.Transparent);
    }
}
