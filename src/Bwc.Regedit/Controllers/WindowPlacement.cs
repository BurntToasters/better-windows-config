namespace Bwc.Regedit.Controllers;

/// <summary>A window rectangle in physical pixels: the unit <c>AppWindow</c> uses.</summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Fits a saved window rectangle into the work area of a display, so a window saved on a monitor
/// that is no longer connected, or at a different scale, still opens fully visible.
/// </summary>
public static class WindowPlacement
{
    /// <summary>The largest width or height a saved rectangle may have. Larger values are ignored.</summary>
    public const int MaximumDimension = 32767;

    /// <summary>True when the saved size is a positive size no larger than <see cref="MaximumDimension"/>.</summary>
    public static bool IsUsable(WindowBounds saved) =>
        saved.Width > 0 && saved.Height > 0 &&
        saved.Width <= MaximumDimension && saved.Height <= MaximumDimension;

    /// <summary>
    /// Returns the saved size, reduced to the work area when it is larger, and moved so that the whole
    /// rectangle lies inside the work area. The caller chooses the work area of the display the saved
    /// rectangle overlaps most, or the nearest one when it overlaps none.
    /// </summary>
    public static WindowBounds FitInto(WindowBounds saved, WindowBounds workArea)
    {
        int width = Math.Min(saved.Width, workArea.Width);
        int height = Math.Min(saved.Height, workArea.Height);
        int x = Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - width);
        int y = Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - height);
        return new WindowBounds(x, y, width, height);
    }
}
