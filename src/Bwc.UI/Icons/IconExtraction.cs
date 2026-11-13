using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.LibraryLoader;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bwc.UI.Icons;

/// <summary>
/// Reads icons at runtime and converts them to premultiplied BGRA pixels. Nothing is copied from
/// Microsoft's files: the group icons are read from the installed regedit.exe as a data file and
/// the stock icons come from SHGetStockIconInfo. Every native handle is released before return.
/// </summary>
internal static unsafe class IconExtraction
{
    /// <summary>Group icon ids in C:\Windows\regedit.exe for build 10.0.26100.8457 (docs/parity/oracle).</summary>
    private const int ComputerGroupId = 201;
    private const int ClosedFolderGroupId = 203;
    private const int StringGroupId = 205;
    private const int BinaryGroupId = 206;

    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    // CsWin32 generates DestroyIcon with a SafeHandle parameter, so the raw handle is released here.
    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    // GetIconInfo is declared locally for the same reason: the generated signature takes a SafeHandle.
    [DllImport("user32.dll", EntryPoint = "GetIconInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }
    private const uint ShgsiIcon = 0x100;
    private const uint ShgsiSmallIcon = 0x1;
    private const uint ShgsiLargeIcon = 0x0;

    /// <summary>A pixel grid in premultiplied BGRA, top row first.</summary>
    internal readonly record struct Pixels(int Width, int Height, byte[] Bgra);

    /// <summary>
    /// Loads the regedit group icon at the requested size. The module is opened and freed inside this
    /// call. Returns null when the file or the group is missing.
    /// </summary>
    public static Pixels? TryLoadRegeditIcon(IconKind kind, int size)
    {
        int groupId = kind switch
        {
            IconKind.Computer => ComputerGroupId,
            IconKind.ClosedFolder => ClosedFolderGroupId,
            IconKind.StringValue => StringGroupId,
            _ => BinaryGroupId,
        };

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        HMODULE module;
        fixed (char* pathChars = path)
        {
            module = PInvoke.LoadLibraryEx(
                new PCWSTR(pathChars),
                default,
                LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_AS_DATAFILE);
        }

        if (module.Value == null)
        {
            return null;
        }

        try
        {
            // An integer resource id is passed as a pointer value: MAKEINTRESOURCE.
            HANDLE handle = PInvoke.LoadImage(
                new HINSTANCE(module.Value),
                new PCWSTR((char*)groupId),
                GDI_IMAGE_TYPE.IMAGE_ICON,
                size,
                size,
                IMAGE_FLAGS.LR_DEFAULTCOLOR);
            if (handle.Value == null)
            {
                return null;
            }

            nint icon = (nint)handle.Value;
            try
            {
                return IconToPixels(icon);
            }
            finally
            {
                DestroyIcon(icon);
            }
        }
        finally
        {
            PInvoke.FreeLibrary(module);
        }
    }

    /// <summary>Reads the stock folder or computer icon. Null when the shell has no such icon.</summary>
    public static Pixels? TryLoadStockIcon(IconKind kind, int size)
    {
        SHSTOCKICONID id = kind == IconKind.Computer ? SHSTOCKICONID.SIID_DESKTOPPC : SHSTOCKICONID.SIID_FOLDER;
        uint flags = ShgsiIcon | (size <= 16 ? ShgsiSmallIcon : ShgsiLargeIcon);

        var info = new SHSTOCKICONINFO { cbSize = (uint)sizeof(SHSTOCKICONINFO) };
        if (PInvoke.SHGetStockIconInfo(id, (SHGSI_FLAGS)flags, ref info).Failed || info.hIcon.Value == null)
        {
            return null;
        }

        nint icon = (nint)info.hIcon.Value;
        try
        {
            return IconToPixels(icon);
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    /// <summary>
    /// Converts an HICON to premultiplied BGRA. The color bitmap supplies the pixels. When it has no
    /// alpha at all, the AND mask supplies the shape: a black mask pixel is opaque, a white one is clear.
    /// </summary>
    private static Pixels? IconToPixels(nint icon)
    {
        if (!GetIconInfo(icon, out IconInfo info))
        {
            return null;
        }

        var colorBitmap = new HBITMAP(info.hbmColor);
        var maskBitmap = new HBITMAP(info.hbmMask);
        try
        {
            if (info.hbmColor == 0 || info.hbmMask == 0)
            {
                return null;
            }

            var bitmap = default(BITMAP);
            if (PInvoke.GetObject(colorBitmap, sizeof(BITMAP), &bitmap) == 0 || bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0)
            {
                return null;
            }

            int width = bitmap.bmWidth;
            int height = bitmap.bmHeight;
            byte[]? color = ReadBgra(colorBitmap, width, height);
            byte[]? mask = ReadBgra(maskBitmap, width, height);
            if (color is null || mask is null)
            {
                return null;
            }

            bool hasAlpha = false;
            for (int i = 3; i < color.Length; i += 4)
            {
                if (color[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            for (int i = 0; i < color.Length; i += 4)
            {
                int alpha = hasAlpha ? color[i + 3] : (mask[i] == 0 ? 255 : 0);
                color[i] = (byte)(color[i] * alpha / 255);
                color[i + 1] = (byte)(color[i + 1] * alpha / 255);
                color[i + 2] = (byte)(color[i + 2] * alpha / 255);
                color[i + 3] = (byte)alpha;
            }

            return new Pixels(width, height, color);
        }
        finally
        {
            PInvoke.DeleteObject(colorBitmap);
            PInvoke.DeleteObject(maskBitmap);
        }
    }

    /// <summary>Reads a bitmap as a top-down 32 bpp array. Returns null if the device call fails.</summary>
    private static byte[]? ReadBgra(HBITMAP bitmap, int width, int height)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BiRgb,
        };
        var info = new BITMAPINFO { bmiHeader = header };
        var buffer = new byte[width * height * 4];

        HDC screen = PInvoke.GetDC(default);
        try
        {
            fixed (byte* pixels = buffer)
            {
                int lines = PInvoke.GetDIBits(screen, bitmap, 0, (uint)height, pixels, &info, (DIB_USAGE)DibRgbColors);
                if (lines != height)
                {
                    return null;
                }
            }
        }
        finally
        {
            PInvoke.ReleaseDC(default, screen);
        }

        return buffer;
    }
}
