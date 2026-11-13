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

    /// <summary>Classic popup item layout in DIPs (66, 44, and 45 device px at 192 DPI): the text starts 33 DIP in.</summary>
    public const double PopupTextLeftDip = 33;

    /// <summary>The gap between the widest label and the accelerator column, in DIPs.</summary>
    public const double PopupAcceleratorGapDip = 22;

    /// <summary>The accelerator column's right inset from the item edge, in DIPs (45 px at 192 DPI).</summary>
    public const double PopupAcceleratorInsetDip = 22.5;

    /// <summary>
    /// The width in DIPs of a popup item, sized as Win32 sizes a menu: the classic text inset, the widest label,
    /// the gap, the widest accelerator, and the right inset. The text is measured with GDI in the menu font at the
    /// window scale. Returns 0 when the popup has no labels or the font cannot be measured.
    /// </summary>
    public static double PopupItemWidthDip(IReadOnlyList<string> labels, IReadOnlyList<string> accelerators, double scale)
    {
        if (labels.Count == 0 || scale <= 0)
        {
            return 0;
        }

        double labelPx = MaxTextWidthPx(labels, scale);
        double acceleratorPx = MaxTextWidthPx(accelerators, scale);
        if (labelPx < 0 || acceleratorPx < 0)
        {
            return 0;
        }

        return PopupTextLeftDip + (labelPx / scale) + PopupAcceleratorGapDip + (acceleratorPx / scale) + PopupAcceleratorInsetDip;
    }

    /// <summary>The widest text in device pixels in the menu font, or -1 when the font cannot be measured.</summary>
    private static double MaxTextWidthPx(IReadOnlyList<string> texts, double scale)
    {
        uint dpi = (uint)Math.Round(scale * 96, MidpointRounding.AwayFromZero);
        var metrics = new NonClientMetrics { cbSize = (uint)Marshal.SizeOf<NonClientMetrics>() };
        if (!SystemParametersInfoForDpi(SpiGetNonClientMetrics, metrics.cbSize, ref metrics, 0, dpi))
        {
            return -1;
        }

        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr font = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            font = CreateFontIndirectW(ref metrics.lfMenuFont);
            if (font == IntPtr.Zero)
            {
                return -1;
            }

            previous = SelectObject(dc, font);
            double widest = 0;
            foreach (string text in texts)
            {
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (!GetTextExtentPoint32W(dc, text, text.Length, out Size size))
                {
                    return -1;
                }

                widest = Math.Max(widest, size.cx);
            }

            return widest;
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
