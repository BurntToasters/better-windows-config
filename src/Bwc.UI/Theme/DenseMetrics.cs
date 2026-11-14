using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Theme;

/// <summary>
/// Classic Registry Editor metrics in DIPs. The oracle capture is in physical pixels at 200 percent
/// scale, so every captured pixel value is halved exactly once here.
///
/// Row heights scale with the font. The formula is:
///   scaled = round(oracleHeight * fontSize / 12)
/// with a minimum of 1 DIP, and midpoint values rounded away from zero. At the default font size
/// of 12 every result is exactly the oracle value. Horizontal metrics (tree indent and pane width)
/// do not scale with the font.
///
/// The x:Double keys in Themes/Dense.xaml mirror the defaults below. State changes are raised on
/// the thread that calls <see cref="ApplyFont"/>; call it on the UI thread.
/// </summary>
public static class DenseMetrics
{
    /// <summary>Font size used by the oracle, in DIPs.</summary>
    public const double BaseFontSize = 12;

    /// <summary>The classic UI font family.</summary>
    public const string FontFamilyName = "Segoe UI";

    private const double OracleTreeRowHeight = 17;
    private const double OracleListRowHeight = 18;
    private const double OracleHeaderHeight = 20;
    private const double OracleMenuBarHeight = 19;

    /// <summary>Font family for the classic shell. Created on first use, which is the UI thread.</summary>
    public static FontFamily FontFamily { get; private set; } = new(FontFamilyName);

    /// <summary>The face name in use. Defaults to Segoe UI.</summary>
    public static string FaceName { get; private set; } = FontFamilyName;

    /// <summary>The font weight in use, 100 to 900. Defaults to 400 (normal).</summary>
    public static int Weight { get; private set; } = 400;

    /// <summary>True when the italic style is in use.</summary>
    public static bool Italic { get; private set; }

    /// <summary>Current font size in DIPs. Defaults to 12.</summary>
    public static double FontSize { get; private set; } = BaseFontSize;

    /// <summary>Tree row height in DIPs. Defaults to 17 at font size 12.</summary>
    public static double TreeRowHeight { get; private set; } = OracleTreeRowHeight;

    /// <summary>List row height in DIPs. Defaults to 18 at font size 12.</summary>
    public static double ListRowHeight { get; private set; } = OracleListRowHeight;

    /// <summary>List header height in DIPs. Defaults to 20 at font size 12.</summary>
    public static double HeaderHeight { get; private set; } = OracleHeaderHeight;

    /// <summary>Menu bar height in DIPs. Defaults to 19 at font size 12.</summary>
    public static double MenuBarHeight { get; private set; } = OracleMenuBarHeight;

    /// <summary>The address bar is the same height as the menu bar in the oracle, 19 DIP.</summary>
    public static double AddressBarHeight => MenuBarHeight;

    /// <summary>Tree indentation per level in DIPs. Defaults to 16.</summary>
    public static double TreeIndent { get; private set; } = 16;

    /// <summary>Tree pane width in DIPs. Defaults to 216.</summary>
    public static double TreePaneWidth { get; private set; } = 216;

    /// <summary>Raised after <see cref="ApplyFont"/> or <see cref="Reset"/> changes the metrics.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Applies a font size and recomputes the row heights proportionally. The default size of 12
    /// reproduces the oracle values exactly.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a finite positive number.</exception>
    public static void ApplyFont(double fontSize) => ApplyFont(fontSize, FaceName, Weight, Italic);

    /// <summary>
    /// Applies a face, weight, italic style, and size. An empty or over-long face name keeps the current
    /// face, and the weight is clamped to 100 to 900.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a finite positive number.</exception>
    public static void ApplyFont(double fontSize, string faceName, int weight, bool italic)
    {
        if (double.IsNaN(fontSize) || double.IsInfinity(fontSize) || fontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), fontSize, "Font size must be a finite positive number.");
        }

        if (!string.IsNullOrWhiteSpace(faceName) && faceName.Length < 32 && !string.Equals(faceName, FaceName, StringComparison.Ordinal))
        {
            FaceName = faceName;
            FontFamily = new FontFamily(faceName);
        }

        Weight = Math.Clamp(weight, 100, 900);
        Italic = italic;
        FontSize = fontSize;
        TreeRowHeight = Scale(OracleTreeRowHeight, fontSize);
        ListRowHeight = Scale(OracleListRowHeight, fontSize);
        HeaderHeight = Scale(OracleHeaderHeight, fontSize);
        MenuBarHeight = Scale(OracleMenuBarHeight, fontSize);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Returns every metric to the 12 DIP oracle defaults.</summary>
    public static void Reset()
    {
        FontSize = BaseFontSize;
        if (!string.Equals(FaceName, FontFamilyName, StringComparison.Ordinal))
        {
            FaceName = FontFamilyName;
            FontFamily = new FontFamily(FontFamilyName);
        }

        Weight = 400;
        Italic = false;
        TreeRowHeight = OracleTreeRowHeight;
        ListRowHeight = OracleListRowHeight;
        HeaderHeight = OracleHeaderHeight;
        MenuBarHeight = OracleMenuBarHeight;
        TreeIndent = 16;
        TreePaneWidth = 216;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static double Scale(double oracleHeight, double fontSize)
    {
        var scaled = Math.Round(oracleHeight * fontSize / BaseFontSize, MidpointRounding.AwayFromZero);
        return Math.Max(1, scaled);
    }
}
