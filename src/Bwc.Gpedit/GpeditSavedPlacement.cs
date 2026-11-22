namespace Bwc.Gpedit;

/// <summary>The window rectangle saved at the last close in physical pixels, and whether it was maximized.</summary>
/// <param name="X">Left edge of the normal window.</param>
/// <param name="Y">Top edge of the normal window.</param>
/// <param name="Width">Width of the normal window.</param>
/// <param name="Height">Height of the normal window.</param>
/// <param name="Maximized">True when the window was maximized at close.</param>
public readonly record struct GpeditSavedPlacement(int X, int Y, int Width, int Height, bool Maximized);
