using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;

namespace Bwc.UI.Theme;

/// <summary>
/// One pane selection as the Explorer visual style draws it. A null border means the theme draws
/// none, and a null text means the theme sets no text color.
/// </summary>
internal readonly record struct ExplorerPaint(Color Fill, Color? Border, Color? Text);

/// <summary>
/// Reads the tree selection from the Explorer visual style at runtime. Classic regedit's tree uses
/// that style, so its selection fill and border come from the theme and not from a fixed value.
/// Each state is drawn with DrawThemeBackground into a 32 bit DIB at the system DPI, and the colors
/// are sampled from the bitmap: the fill from the center pixel, and the border from the top edge.
/// </summary>
/// <remarks>
/// The result is cached by system DPI and by whether a visual style is active. A change in either
/// renders again on the next call. The list is not read here: classic regedit's list is not drawn
/// by the Explorer style, so its selection stays on the system colors.
/// </remarks>
internal static unsafe class BwcExplorerSelection
{
    private const string TreeViewClass = "Explorer::TreeView";
    /// <summary>TVP_TREEITEM. Part 2 is TVP_GLYPH, the expand chevron.</summary>
    private const int TvpTreeItem = 1;
    private const int TreisSelected = 3;
    private const int TreisSelectedNotFocus = 5;
    private const int TmtTextColor = 3803;
    private const int BaseWidthDip = 64;
    private const int BaseHeightDip = 32;

    private static bool _cacheValid;
    private static int _cachedDpi;
    private static bool _cachedActive;
    private static bool _cachedOk;
    private static ExplorerPaint _cachedFocused;
    private static ExplorerPaint _cachedUnfocused;

    /// <summary>
    /// Tries to read the focused and unfocused tree selection. Returns false when the visual style
    /// is off or the theme cannot be opened or drawn. The caller then uses the system colors.
    /// </summary>
    internal static bool TryReadTreeSelection(out ExplorerPaint focused, out ExplorerPaint unfocused)
    {
        bool active = PInvoke.IsThemeActive();
        int dpi = (int)PInvoke.GetDpiForSystem();
        if (!_cacheValid || _cachedDpi != dpi || _cachedActive != active)
        {
            _cachedOk = active && TryRender(dpi, out _cachedFocused, out _cachedUnfocused);
            _cachedDpi = dpi;
            _cachedActive = active;
            _cacheValid = true;
        }

        focused = _cachedFocused;
        unfocused = _cachedUnfocused;
        return _cachedOk;
    }

    private static bool TryRender(int dpi, out ExplorerPaint focused, out ExplorerPaint unfocused)
    {
        focused = default;
        unfocused = default;

        var themeHandle = PInvoke.OpenThemeData(PInvoke.GetDesktopWindow(), TreeViewClass);
        if (themeHandle.IsInvalid)
        {
            return false;
        }

        using (themeHandle)
        {
            int width = Math.Max(8, BaseWidthDip * dpi / 96);
            int height = Math.Max(8, BaseHeightDip * dpi / 96);

            HDC screen = PInvoke.GetDC(HWND.Null);
            HDC dc = PInvoke.CreateCompatibleDC(screen);
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
            if (dc.IsNull)
            {
                return false;
            }

            try
            {
                var info = new BITMAPINFO();
                info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                info.bmiHeader.biWidth = width;
                info.bmiHeader.biHeight = -height;
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = 0;

                void* bits;
                var bitmap = PInvoke.CreateDIBSection(dc, &info, DIB_USAGE.DIB_RGB_COLORS, out bits, null, 0);
                if (bitmap.IsInvalid || bits is null)
                {
                    return false;
                }

                HGDIOBJ previous = PInvoke.SelectObject(dc, new HGDIOBJ(bitmap.DangerousGetHandle()));
                try
                {
                    bool okFocused = TryDraw(themeHandle, dc, (uint*)bits, width, height, TreisSelected, out focused);
                    bool okUnfocused = TryDraw(themeHandle, dc, (uint*)bits, width, height, TreisSelectedNotFocus, out unfocused);
                    return okFocused && okUnfocused;
                }
                finally
                {
                    _ = PInvoke.SelectObject(dc, previous);
                    bitmap.Dispose();
                }
            }
            finally
            {
                _ = PInvoke.DeleteDC(dc);
            }
        }
    }

    private static bool TryDraw(CloseThemeDataSafeHandle themeHandle, HDC dc, uint* pixels, int width, int height, int state, out ExplorerPaint paint)
    {
        paint = default;
        new Span<uint>(pixels, width * height).Fill(0x00FFFFFF);

        var rect = new RECT { left = 0, top = 0, right = width, bottom = height };
        var theme = new HTHEME(themeHandle.DangerousGetHandle());
        HRESULT drawn = PInvoke.DrawThemeBackground(theme, dc, TvpTreeItem, state, &rect, null);
        if (drawn.Failed)
        {
            return false;
        }

        Color fill = FromPixel(pixels[(height / 2) * width + (width / 2)]);
        Color edge = FromPixel(pixels[width / 2]);
        Color? border = edge == fill ? null : edge;

        Color? text = null;
        if (PInvoke.GetThemeColor(themeHandle, TvpTreeItem, state, (THEME_PROPERTY_SYMBOL_ID)TmtTextColor, out COLORREF textRef).Succeeded)
        {
            text = FromColorRef(textRef);
        }

        paint = new ExplorerPaint(fill, border, text);
        return true;
    }

    /// <summary>GDI pixels are 0xAARRGGBB as a 32 bit value on a little endian machine.</summary>
    private static Color FromPixel(uint pixel) =>
        Color.FromArgb(0xFF, (byte)((pixel >> 16) & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)(pixel & 0xFF));

    /// <summary>COLORREF is red in the low byte, then green, then blue.</summary>
    private static Color FromColorRef(uint colorRef) =>
        Color.FromArgb(0xFF, (byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
}
