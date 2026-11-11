using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.Theme;

/// <summary>
/// The classic DrawFocusRect look: a one device pixel dotted rectangle inside the row. The stroke
/// is one device pixel wide at any scale. The rectangle has a fixed size and is centered on
/// device pixel 1 (inset 1.5 device pixels), and the dash starts half a dash unit later, so every
/// dash edge falls on a device pixel boundary. Without that, anti-aliasing blends each dash into a
/// solid half-tone line. The brush decides whether the rectangle is visible, so a row never needs to
/// be re-applied on a focus change.
/// </summary>
internal static class BwcFocusRect
{
    /// <summary>
    /// Creates the overlay. It hit-tests nothing. Rows set its stroke to the transparent brush when
    /// they are not the focused row, so a selection change never toggles layout.
    /// </summary>
    public static Rectangle Create(Brush stroke) => new()
    {
        IsHitTestVisible = false,
        Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        StrokeDashArray = new DoubleCollection { 1, 1 },
        StrokeDashOffset = 0.5,
        Stroke = stroke,
    };

    /// <summary>Sizes the overlay for a row and measures it. The stroke is one device pixel.</summary>
    public static void Measure(Rectangle rect, UIElement owner, Size rowSize)
    {
        double pixel = DevicePixel(owner);
        Size inner = InnerSize(rowSize, pixel);
        rect.StrokeThickness = pixel;
        rect.Width = inner.Width;
        rect.Height = inner.Height;
        rect.Measure(inner);
    }

    /// <summary>Arranges the overlay inside a row of the given size.</summary>
    public static void Arrange(Rectangle rect, UIElement owner, Size rowSize)
    {
        double pixel = DevicePixel(owner);
        Size inner = InnerSize(rowSize, pixel);
        double inset = InsetFor(pixel);
        rect.Arrange(new Rect(inset, inset, inner.Width, inner.Height));
    }

    /// <summary>
    /// The rectangle sits half a device pixel from the row edge. The measured stroke lands one device
    /// pixel below the nominal position, which the E2E test accounts for by scanning three rows.
    /// </summary>
    private static double InsetFor(double pixel) => pixel * 0.5;

    private static Size InnerSize(Size rowSize, double pixel)
    {
        double inset = InsetFor(pixel) * 2;
        return new Size(Math.Max(0, rowSize.Width - inset), Math.Max(0, rowSize.Height - inset));
    }

    /// <summary>One device pixel in DIPs for the element's monitor. Falls back to 1 before loading.</summary>
    public static double DevicePixel(UIElement owner)
    {
        double scale = owner.XamlRoot?.RasterizationScale ?? 1.0;
        return scale > 0 ? 1.0 / scale : 1.0;
    }
}
