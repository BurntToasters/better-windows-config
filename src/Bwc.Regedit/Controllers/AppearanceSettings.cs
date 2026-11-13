using Bwc.UI.Theme;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// Converts the saved appearance values to the runtime types. A value that is missing or not valid
/// falls back to the default, so a damaged settings file never produces a broken window.
/// </summary>
public static class AppearanceSettings
{
    /// <summary>The oracle font size in DIPs (9 point).</summary>
    public const double DefaultFontSizeDip = DenseMetrics.BaseFontSize;

    /// <summary>The smallest font size the font dialog offers, 6 point, in DIPs.</summary>
    public const double MinimumFontSizeDip = 6 * 96.0 / 72.0;

    /// <summary>The largest font size the font dialog offers, 72 point, in DIPs.</summary>
    public const double MaximumFontSizeDip = 72 * 96.0 / 72.0;

    /// <summary>Parses the saved theme name. Anything other than Light or Dark means System.</summary>
    public static BwcThemeOverride ParseTheme(string? value) => value switch
    {
        "Light" => BwcThemeOverride.Light,
        "Dark" => BwcThemeOverride.Dark,
        _ => BwcThemeOverride.System,
    };

    /// <summary>The name written to settings for an override.</summary>
    public static string FormatTheme(BwcThemeOverride value) => value switch
    {
        BwcThemeOverride.Light => "Light",
        BwcThemeOverride.Dark => "Dark",
        _ => "System",
    };

    /// <summary>
    /// The saved font size in DIPs. A value that is not finite, not positive, or outside the 6 to 72
    /// point range is replaced by the oracle size.
    /// </summary>
    public static double ValidFontSizeDip(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return DefaultFontSizeDip;
        }

        return Math.Clamp(value, MinimumFontSizeDip, MaximumFontSizeDip);
    }

    /// <summary>Converts a point size from the font dialog to DIPs.</summary>
    public static double PointsToDip(double points) => points * 96.0 / 72.0;
}
