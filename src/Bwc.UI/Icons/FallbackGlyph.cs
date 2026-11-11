namespace Bwc.UI.Icons;

/// <summary>
/// BWC-drawn stand-ins for the four classic icons. They are used only when the system icons cannot
/// be read. Shapes are drawn on a 16 by 16 grid and scaled to the requested size; the output is
/// opaque premultiplied BGRA, so it needs no alpha conversion.
/// </summary>
internal static class FallbackGlyph
{
    private const int Grid = 16;

    public static IconExtraction.Pixels Render(IconKind kind, int size)
    {
        int side = Math.Max(1, size);
        var bgra = new byte[side * side * 4];

        switch (kind)
        {
            case IconKind.Computer:
                Fill(bgra, side, 1, 2, 15, 10, 0x1E, 0x8C, 0xD2);
                Fill(bgra, side, 6, 11, 10, 13, 0x80, 0x80, 0x80);
                Fill(bgra, side, 4, 13, 12, 14, 0x80, 0x80, 0x80);
                break;
            case IconKind.ClosedFolder:
                Fill(bgra, side, 1, 2, 6, 4, 0xF5, 0xC2, 0x3C);
                Fill(bgra, side, 1, 4, 15, 14, 0xF5, 0xC2, 0x3C);
                break;
            case IconKind.StringValue:
                Fill(bgra, side, 2, 1, 14, 15, 0xF8, 0xF8, 0xF8);
                Fill(bgra, side, 4, 6, 12, 10, 0xD0, 0x40, 0x30);
                break;
            default:
                Fill(bgra, side, 2, 1, 14, 15, 0xF8, 0xF8, 0xF8);
                Fill(bgra, side, 4, 4, 12, 7, 0x20, 0x50, 0xE0);
                Fill(bgra, side, 4, 9, 12, 12, 0x20, 0x50, 0xE0);
                break;
        }

        return new IconExtraction.Pixels(side, side, bgra);
    }

    /// <summary>Fills the half-open grid rectangle [x0, x1) by [y0, y1) with an opaque color.</summary>
    private static void Fill(byte[] bgra, int side, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        int left = x0 * side / Grid;
        int right = x1 * side / Grid;
        int top = y0 * side / Grid;
        int bottom = y1 * side / Grid;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int index = (y * side + x) * 4;
                bgra[index] = b;
                bgra[index + 1] = g;
                bgra[index + 2] = r;
                bgra[index + 3] = 255;
            }
        }
    }
}
