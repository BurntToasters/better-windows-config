using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Bwc.UI.Menu;

/// <summary>
/// Sets the menu chrome brushes from the palette. The light and dark menus follow the window,
/// face, and shadow brushes, so no menu color is written here as a literal for a theme.
/// </summary>
public static class ClassicMenuBrushes
{
    private const string MenuKey = "BwcMenuBrush";
    private const string MenuBorderKey = "BwcMenuBorderBrush";
    private const string MenuHighlightKey = "BwcMenuHighlightBrush";
    private const string MenuSeparatorKey = "BwcMenuSeparatorBrush";

    // Fractions of the way from one palette color to another. On the light palette they give the
    // oracle colors: menu F9F9F9 from white and face, border E5E5E5 and separator D7D7D7 from white
    // and the shadow gray.
    private const double MenuMix = 0.4;
    private const double BorderMix = 0.27;
    private const double SeparatorMix = 0.42;

    /// <summary>Recomputes the four menu brushes from the current palette. Call on the UI thread.</summary>
    public static void Refresh()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        Color window = BwcBrushes.Window.Color;
        Color face = BwcBrushes.Face.Color;
        Color shadow = BwcBrushes.Shadow.Color;

        SetColor(resources, MenuKey, Mix(window, face, MenuMix));
        SetColor(resources, MenuBorderKey, Mix(window, shadow, BorderMix));
        SetColor(resources, MenuHighlightKey, face);
        SetColor(resources, MenuSeparatorKey, Mix(window, shadow, SeparatorMix));
    }

    private static void SetColor(ResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private static Color Mix(Color from, Color to, double t)
    {
        return ColorHelper.FromArgb(
            255,
            Channel(from.R, to.R, t),
            Channel(from.G, to.G, t),
            Channel(from.B, to.B, t));
    }

    private static byte Channel(byte from, byte to, double t)
    {
        return (byte)Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero);
    }
}
