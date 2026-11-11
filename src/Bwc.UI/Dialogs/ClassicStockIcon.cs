using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The system stock icons for a classic message box, read at run time from the Windows icon resources.
/// Information, warning, and error come from SHGetStockIconInfo. The question icon comes from
/// LoadIcon(IDI_QUESTION). Nothing is copied into the application: the icon is drawn from the system
/// each time it is needed.
/// </summary>
/// <remarks>
/// A stock icon has per-pixel alpha, and DrawIconEx does not return that alpha. The icon is therefore
/// drawn once over black and once over white. The difference between the two renders is 255 minus the
/// alpha, so the straight alpha and color are recovered per pixel and written as a 32-bit BMP that
/// BitmapImage reads. A failure at any step returns null, and the caller keeps its vector fallback.
/// </remarks>
internal static unsafe partial class ClassicStockIcon
{
    internal enum Kind
    {
        Information,
        Warning,
        Error,
        Question,
    }

    private const uint SiidWarning = 78;
    private const uint SiidInfo = 79;
    private const uint SiidError = 80;
    private const uint ShgsiIcon = 0x100;
    private const uint DiNormal = 0x0003;
    private const uint DibRgbColors = 0;
    private const int IdiQuestion = 32514;
    private const int MaxPixels = 512;
    private const int V4HeaderSize = 108;
    private const int FileHeaderSize = 14;

    /// <summary>
    /// Renders the icon as a BMP file of <paramref name="pixels"/> by <paramref name="pixels"/>, or returns null.
    /// </summary>
    internal static byte[]? RenderBmp(Kind kind, int pixels)
    {
        if (pixels <= 0 || pixels > MaxPixels)
        {
            return null;
        }

        nint icon = 0;
        bool destroy = false;
        try
        {
            icon = LoadSystemIcon(kind, out destroy);
            if (icon == 0)
            {
                return null;
            }

            byte[] black = Render(icon, pixels, 0x00);
            byte[] white = Render(icon, pixels, 0xFF);
            return EncodeBmp(black, white, pixels);
        }
        catch (Exception)
        {
            // The system did not draw the icon. The message box keeps its vector fallback.
            return null;
        }
        finally
        {
            if (destroy && icon != 0)
            {
                _ = DestroyIcon(icon);
            }
        }
    }

    private static nint LoadSystemIcon(Kind kind, out bool destroy)
    {
        destroy = false;
        if (kind == Kind.Question)
        {
            // The shared IDI_QUESTION icon belongs to the system and must not be destroyed.
            return LoadIconW(0, IdiQuestion);
        }

        uint siid = kind switch
        {
            Kind.Warning => SiidWarning,
            Kind.Error => SiidError,
            _ => SiidInfo,
        };

        var info = new ShStockIconInfo { cbSize = (uint)Unsafe.SizeOf<ShStockIconInfo>() };
        int hr = SHGetStockIconInfo(siid, ShgsiIcon, ref info);
        if (hr != 0 || info.hIcon == 0)
        {
            return 0;
        }

        destroy = true;
        return info.hIcon;
    }

    /// <summary>Draws the icon over a solid background into a top-down 32-bit DIB and returns its BGRA bytes.</summary>
    private static byte[] Render(nint icon, int pixels, byte background)
    {
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Unsafe.SizeOf<BitmapInfoHeader>(),
            biWidth = pixels,
            biHeight = -pixels,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        nint dc = ClassicGdi.CreateCompatibleDC(0);
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            bitmap = CreateDIBSection(dc, ref header, DibRgbColors, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0)
            {
                throw new InvalidOperationException("CreateDIBSection failed while drawing a stock icon.");
            }

            previous = ClassicGdi.SelectObject(dc, bitmap);
            int byteCount = pixels * pixels * 4;
            new Span<byte>((void*)bits, byteCount).Fill(background);
            if (DrawIconEx(dc, 0, 0, icon, pixels, pixels, 0, 0, DiNormal) == 0)
            {
                throw new InvalidOperationException("DrawIconEx failed while drawing a stock icon.");
            }

            var pixelBytes = new byte[byteCount];
            new ReadOnlySpan<byte>((void*)bits, byteCount).CopyTo(pixelBytes);
            return pixelBytes;
        }
        finally
        {
            if (previous != 0)
            {
                _ = ClassicGdi.SelectObject(dc, previous);
            }

            if (bitmap != 0)
            {
                _ = ClassicGdi.DeleteObject(bitmap);
            }

            _ = ClassicGdi.DeleteDC(dc);
        }
    }

    /// <summary>
    /// Recovers straight BGRA from the two renders. Over black a pixel is color times alpha. Over white it
    /// is that plus 255 times (1 - alpha), so alpha = 255 - (white - black), averaged over the channels.
    /// </summary>
    private static byte[] EncodeBmp(byte[] black, byte[] white, int pixels)
    {
        var bgra = new byte[black.Length];
        for (int i = 0; i < black.Length; i += 4)
        {
            int difference = (white[i] - black[i]) + (white[i + 1] - black[i + 1]) + (white[i + 2] - black[i + 2]);
            int alpha = Math.Clamp(255 - (int)Math.Round(difference / 3.0), 0, 255);
            if (alpha == 0)
            {
                continue;
            }

            for (int channel = 0; channel < 3; channel++)
            {
                bgra[i + channel] = (byte)Math.Clamp(black[i + channel] * 255 / alpha, 0, 255);
            }

            bgra[i + 3] = (byte)alpha;
        }

        return WriteV4Bmp(bgra, pixels);
    }

    /// <summary>A top-down BMP with BI_BITFIELDS masks, so the alpha channel is read as alpha.</summary>
    private static byte[] WriteV4Bmp(byte[] bgra, int pixels)
    {
        int dataOffset = FileHeaderSize + V4HeaderSize;
        using var stream = new MemoryStream(dataOffset + bgra.Length);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write((uint)(dataOffset + bgra.Length));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((uint)dataOffset);

            writer.Write((uint)V4HeaderSize);
            writer.Write(pixels);
            writer.Write(-pixels);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)3);
            writer.Write((uint)bgra.Length);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0x00FF0000u);
            writer.Write(0x0000FF00u);
            writer.Write(0x000000FFu);
            writer.Write(0xFF000000u);
            writer.Write(0x73524742u);
            writer.Write(new byte[36]);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(bgra);
        }

        return stream.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShStockIconInfo
    {
        public uint cbSize;
        public nint hIcon;
        public int iSysImageIndex;
        public int iIcon;
        public fixed byte szPath[520];
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint siid, uint flags, ref ShStockIconInfo info);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIconW(nint instance, nint iconName);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int DrawIconEx(
        nint hdc,
        int x,
        int y,
        nint icon,
        int width,
        int height,
        uint animationStep,
        nint flickerFreeBrush,
        uint flags);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(
        nint hdc,
        ref BitmapInfoHeader info,
        uint usage,
        out nint bits,
        nint section,
        uint offset);
}
