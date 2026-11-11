using System.Runtime.InteropServices;

namespace Bwc.UI.Menu;

/// <summary>
/// Sizes the classic menu bar items from GDI, the way the classic menu bar does. WinUI's DirectWrite
/// text is wider than the GDI text the oracle was measured with, so a WinUI-measured width drifts from
/// the oracle rectangles. The width here is the GDI width of the label in the system menu font at the
/// window DPI, plus the classic padding on each side of the text.
/// </summary>
public static class ClassicMenuMetrics
{
    /// <summary>Classic padding on each side of a top-level label, in DIPs (14 px at 192 DPI).</summary>
    public const double BarItemPaddingDip = 7;

    private const uint SpiGetNonClientMetrics = 0x0029;
    private const int LfFaceSize = 32;

    /// <summary>
    /// The width in DIPs of a top-level item for the label (no mnemonic marker) at the given scale.
    /// Returns 0 when the font or the device context cannot be created, so the item keeps its own width.
    /// </summary>
    public static double BarItemWidthDip(string label, double scale)
    {
        if (string.IsNullOrEmpty(label) || scale <= 0)
        {
            return 0;
        }

        uint dpi = (uint)Math.Round(scale * 96, MidpointRounding.AwayFromZero);
        var metrics = new NonClientMetrics { cbSize = (uint)Marshal.SizeOf<NonClientMetrics>() };
        if (!SystemParametersInfoForDpi(SpiGetNonClientMetrics, metrics.cbSize, ref metrics, 0, dpi))
        {
            return 0;
        }

        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return 0;
        }

        IntPtr font = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            font = CreateFontIndirectW(ref metrics.lfMenuFont);
            if (font == IntPtr.Zero)
            {
                return 0;
            }

            previous = SelectObject(dc, font);
            if (!GetTextExtentPoint32W(dc, label, label.Length, out Size size))
            {
                return 0;
            }

            return size.cx / scale + (2 * BarItemPaddingDip);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                _ = SelectObject(dc, previous);
            }

            if (font != IntPtr.Zero)
            {
                _ = DeleteObject(font);
            }

            _ = DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LfFaceSize)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NonClientMetrics
    {
        public uint cbSize;
        public int iBorderWidth;
        public int iScrollWidth;
        public int iScrollHeight;
        public int iCaptionWidth;
        public int iCaptionHeight;
        public LogFont lfCaptionFont;
        public int iSmCaptionWidth;
        public int iSmCaptionHeight;
        public LogFont lfSmCaptionFont;
        public int iMenuWidth;
        public int iMenuHeight;
        public LogFont lfMenuFont;
        public LogFont lfStatusFont;
        public LogFont lfMessageFont;
        public int iPaddedBorderWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int cx;
        public int cy;
    }

#pragma warning disable SYSLIB1054 // Small fixed set of GDI calls; LibraryImport plumbing is not needed.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoForDpi(uint action, uint uiParam, ref NonClientMetrics pvParam, uint fWinIni, uint dpi);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontIndirectW(ref LogFont lplf);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out Size lpSize);
#pragma warning restore SYSLIB1054
}
