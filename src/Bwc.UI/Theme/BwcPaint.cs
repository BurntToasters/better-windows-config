using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Theme;

/// <summary>
/// Public access to the three palette brushes that dialogs outside Bwc.UI draw with: the button face,
/// the window text, and the shadow edge. The brushes themselves stay in <see cref="BwcBrushes"/>.
/// </summary>
public static class BwcPaint
{
    /// <summary>The button face and dialog surface brush.</summary>
    public static SolidColorBrush Face => BwcBrushes.Face;

    /// <summary>The window text brush.</summary>
    public static SolidColorBrush WindowText => BwcBrushes.WindowText;

    /// <summary>The shadow edge brush, used for etched borders.</summary>
    public static SolidColorBrush Shadow => BwcBrushes.Shadow;
}
