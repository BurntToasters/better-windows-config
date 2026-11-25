using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.LibraryLoader;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// The toolbar images, read at run time from the installed mmc.exe: bitmap resource 202 is a strip of 16 by 16 images
/// on a black mask (the standard console toolbar). Nothing is copied into BWC. A module that is absent, a strip of
/// another shape, or any failure gives no image, and the toolbar draws a font glyph instead.
/// </summary>
internal static unsafe class ConsoleToolbarIcons
{
    /// <summary>E2E only: a file path that replaces the module, so a missing module can be tested.</summary>
    internal const string E2EModuleVariable = "BWC_E2E_CONSOLE_ICON_MODULE";

    private const string ModuleName = "mmc.exe";
    private const int StripBitmapId = 202;
    private const int Cell = 16;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    // Strip positions of the standard buttons, read from the strip of mmc.exe on build 10.0.26100.8457.
    private static readonly Dictionary<int, int> StripIndexOf = new()
    {
        [ConsoleToolbarIds.Back] = 0,
        [ConsoleToolbarIds.Forward] = 1,
        [ConsoleToolbarIds.Refresh] = 3,
        [ConsoleToolbarIds.Properties] = 8,
        [ConsoleToolbarIds.UpOneLevel] = 9,
        [ConsoleToolbarIds.ShowHideConsoleTree] = 10,
        [ConsoleToolbarIds.Help] = 15,
        [ConsoleToolbarIds.ExportList] = 16,
    };

    private static readonly Dictionary<(int Id, int Size, bool Disabled), ImageSource> Cache = new();
    private static bool _loaded;
    private static byte[]? _strip;
    private static int _stripWidth;

    /// <summary>The module and resource the images came from, or "fallback" when they could not be read.</summary>
    internal static string Source
    {
        get
        {
            EnsureLoaded();
            return _strip is null ? "fallback" : $"{ModuleName}#{StripBitmapId}";
        }
    }

    /// <summary>
    /// The image of a standard button at a pixel size, or false when the strip is unavailable or the button has no
    /// image in it (an extension button).
    /// </summary>
    internal static bool TryGet(int toolbarId, int pixelSize, bool disabled, out ImageSource? image)
    {
        image = null;
        EnsureLoaded();
        if (_strip is null || pixelSize < 1 || !StripIndexOf.TryGetValue(toolbarId, out int index) ||
            (index + 1) * Cell > _stripWidth)
        {
            return false;
        }

        if (Cache.TryGetValue((toolbarId, pixelSize, disabled), out ImageSource? cached))
        {
            image = cached;
            return true;
        }

        byte[] pixels = CutCell(_strip, _stripWidth, index, pixelSize, disabled);
        var bitmap = new WriteableBitmap(pixelSize, pixelSize);
        pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        Cache[(toolbarId, pixelSize, disabled)] = bitmap;
        image = bitmap;
        return true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            (_strip, _stripWidth) = LoadStrip();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _strip = null;
            UiDiagnostics.Warn?.Invoke($"Console toolbar images could not be read: {ex.Message}");
        }

        if (_strip is null)
        {
            UiDiagnostics.Warn?.Invoke("Console toolbar: using the font glyphs, the image strip is unavailable.");
        }
    }

    private static string ModulePath()
    {
#if BWC_E2E
        string? seam = Environment.GetEnvironmentVariable(E2EModuleVariable);
        if (!string.IsNullOrEmpty(seam))
        {
            return seam;
        }
#endif
        return Path.Combine(Environment.SystemDirectory, ModuleName);
    }

    /// <summary>Opens the module as a data file, reads the strip as top-down BGRA, frees everything.</summary>
    private static (byte[]?, int) LoadStrip()
    {
        string path = ModulePath();
        if (!File.Exists(path))
        {
            return (null, 0);
        }

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
            return (null, 0);
        }

        try
        {
            HANDLE handle = PInvoke.LoadImage(
                new HINSTANCE(module.Value),
                new PCWSTR((char*)StripBitmapId),
                GDI_IMAGE_TYPE.IMAGE_BITMAP,
                0,
                0,
                IMAGE_FLAGS.LR_DEFAULTCOLOR);
            if (handle.Value == null)
            {
                return (null, 0);
            }

            var bitmap = new HBITMAP((nint)handle.Value);
            try
            {
                var description = default(BITMAP);
                if (PInvoke.GetObject(bitmap, sizeof(BITMAP), &description) == 0)
                {
                    return (null, 0);
                }

                int width = description.bmWidth;
                int height = description.bmHeight;
                if (height != Cell || width < Cell || width % Cell != 0)
                {
                    return (null, 0);
                }

                byte[]? bgra = ReadBgra(bitmap, width, height);
                if (bgra is null)
                {
                    return (null, 0);
                }

                // The strip has no alpha. Black is the mask, everything else is opaque.
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    bool mask = bgra[i] == 0 && bgra[i + 1] == 0 && bgra[i + 2] == 0;
                    bgra[i + 3] = (byte)(mask ? 0 : 255);
                }

                return (bgra, width);
            }
            finally
            {
                PInvoke.DeleteObject(bitmap);
            }
        }
        finally
        {
            PInvoke.FreeLibrary(module);
        }
    }

    private static byte[]? ReadBgra(HBITMAP bitmap, int width, int height)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            },
        };
        var buffer = new byte[width * height * 4];
        HDC screen = PInvoke.GetDC(default);
        try
        {
            fixed (byte* pixels = buffer)
            {
                int lines = PInvoke.GetDIBits(screen, bitmap, 0, (uint)height, pixels, &info, (DIB_USAGE)DibRgbColors);
                return lines == height ? buffer : null;
            }
        }
        finally
        {
            PInvoke.ReleaseDC(default, screen);
        }
    }

    /// <summary>One 16 by 16 cell scaled to size by nearest neighbour; a disabled cell is gray and lighter.</summary>
    private static byte[] CutCell(byte[] strip, int stripWidth, int index, int size, bool disabled)
    {
        var cell = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            int sy = Math.Min(Cell - 1, y * Cell / size);
            for (int x = 0; x < size; x++)
            {
                int sx = (index * Cell) + Math.Min(Cell - 1, x * Cell / size);
                int from = ((sy * stripWidth) + sx) * 4;
                int to = ((y * size) + x) * 4;
                byte alpha = strip[from + 3];
                if (alpha == 0)
                {
                    continue;
                }

                if (disabled)
                {
                    int gray = ((strip[from + 2] * 30) + (strip[from + 1] * 59) + (strip[from] * 11)) / 100;
                    byte light = (byte)(128 + (gray / 2));
                    cell[to] = light;
                    cell[to + 1] = light;
                    cell[to + 2] = light;
                }
                else
                {
                    cell[to] = strip[from];
                    cell[to + 1] = strip[from + 1];
                    cell[to + 2] = strip[from + 2];
                }

                cell[to + 3] = alpha;
            }
        }

        return cell;
    }
}
