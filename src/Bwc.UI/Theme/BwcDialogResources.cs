using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Theme;

/// <summary>
/// Maps the Fluent theme keys that a classic dialog's controls read (buttons, text boxes, check and radio marks, focus
/// visuals, and the text colors) to the BWC palette brushes. A classic dialog sets this map as its root resources, so its
/// controls paint from the palette whatever the Windows app theme or contrast setting is. The brushes are live, so a
/// palette change repaints the dialog without a new map.
/// </summary>
internal static class BwcDialogResources
{
    /// <summary>Builds the map. The keys are the names the Fluent control templates look up.</summary>
    internal static ResourceDictionary Create()
    {
        var map = new ResourceDictionary();
        Add(map, BwcBrushes.Face,
            "ControlFillColorDefaultBrush",
            "ControlFillColorSecondaryBrush",
            "ControlFillColorDisabledBrush",
            "TextControlBackgroundDisabled");
        Add(map, BwcBrushes.Window,
            "ControlSolidFillColorDefaultBrush",
            "TextControlBackground",
            "TextControlBackgroundPointerOver",
            "TextControlBackgroundFocused",
            "TextOnAccentFillColorPrimaryBrush",
            "FocusVisualSecondaryBrush");
        Add(map, BwcBrushes.WindowText,
            "TextFillColorPrimaryBrush",
            "TextFillColorSecondaryBrush",
            "TextControlForeground",
            "TextControlForegroundPointerOver",
            "TextControlForegroundFocused",
            "TextControlBorderBrush",
            "TextControlBorderBrushPointerOver",
            "TextControlBorderBrushFocused",
            "ControlStrokeColorDefaultBrush",
            "ControlStrokeColorSecondaryBrush",
            "ControlStrongStrokeColorDefaultBrush",
            "AccentFillColorDefaultBrush",
            "AccentFillColorSecondaryBrush",
            "FocusVisualPrimaryBrush");
        Add(map, BwcBrushes.GrayText,
            "TextFillColorDisabledBrush",
            "TextFillColorTertiaryBrush",
            "TextControlForegroundDisabled",
            "TextControlPlaceholderForeground",
            "TextControlBorderBrushDisabled");
        Add(map, BwcBrushes.Selection,
            "ControlFillColorTertiaryBrush",
            "AccentFillColorTertiaryBrush");
        Add(map, BwcBrushes.SelectionText,
            "TextOnAccentFillColorSecondaryBrush");
        return map;
    }

    private static void Add(ResourceDictionary map, SolidColorBrush brush, params string[] keys)
    {
        foreach (string key in keys)
        {
            map[key] = brush;
        }
    }
}
