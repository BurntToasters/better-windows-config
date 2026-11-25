using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Bwc.UI.Icons;

/// <summary>
/// Supplies the classic tree and list icons as WinUI image sources. Each icon is a 16 DIP glyph
/// rendered at the window's pixel size, so the image is 32 px at 200 percent. Icons are loaded from
/// the system at runtime and cached per kind and pixel size. Call from the UI thread only: the
/// bitmaps are UI objects and the cache is not locked.
/// </summary>
public static class IconProvider
{
    /// <summary>The classic icon size in DIPs.</summary>
    public const double IconDip = 16;

    private static readonly Dictionary<(IconKind Kind, int Size), ImageSource> Cache = new();
    private static readonly Dictionary<(ModuleIcon Icon, int Size, IconKind? Fallback), ImageSource> ModuleCache = new();
    private static bool _loadFailureLogged;

    /// <summary>
    /// Returns the icon for a kind at the given rasterization scale (1.0 at 96 DPI, 2.0 at 192 DPI).
    /// Never returns null: if the system icons cannot be read the BWC fallback glyph is returned.
    /// </summary>
    public static ImageSource Get(IconKind kind, double rasterizationScale)
    {
#if BWC_E2E
        IconSelfCheck.RunOnce();
#endif
        int size = PixelSize(rasterizationScale);

        if (Cache.TryGetValue((kind, size), out ImageSource? cached))
        {
            return cached;
        }

        IconExtraction.Pixels pixels = Load(kind, size);
        ImageSource image = CreateBitmap(pixels);
        Cache[(kind, size)] = image;
        return image;
    }

    /// <summary>
    /// Returns an icon read from an installed module at the given rasterization scale. Never returns
    /// null and never throws: a module, id or index that is absent gives the fallback glyph for
    /// <paramref name="fallback"/>, or the neutral generic glyph when it is null. The result, including a
    /// fallback, is cached per icon and pixel size.
    /// </summary>
    public static ImageSource Get(ModuleIcon icon, double rasterizationScale, IconKind? fallback = null)
    {
#if BWC_E2E
        IconSelfCheck.RunOnce();
#endif
        int size = PixelSize(rasterizationScale);

        if (ModuleCache.TryGetValue((icon, size, fallback), out ImageSource? cached))
        {
            return cached;
        }

        ImageSource image = CreateBitmap(LoadModulePixels(icon, size, fallback));
        ModuleCache[(icon, size, fallback)] = image;
        return image;
    }

    /// <summary>Returns the image for a classic icon or a module icon reference.</summary>
    public static ImageSource Get(IconReference icon, double rasterizationScale)
    {
        if (icon.Module is { } module)
        {
            return Get(module, rasterizationScale, icon.Fallback);
        }

        return Get(icon.Kind ?? IconKind.ClosedFolder, rasterizationScale);
    }

    /// <summary>The pixel size of a 16 DIP icon at the scale. A non-finite or non-positive scale counts as 1.0.</summary>
    internal static int PixelSize(double rasterizationScale)
    {
        double scale = double.IsFinite(rasterizationScale) && rasterizationScale > 0 ? rasterizationScale : 1.0;
        return Math.Max(1, (int)Math.Round(IconDip * scale, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The pixels <see cref="Get(ModuleIcon, double, IconKind?)"/> shows at this pixel size: the module icon,
    /// or the fallback glyph when it cannot be read. Never throws.
    /// </summary>
    internal static IconExtraction.Pixels LoadModulePixels(ModuleIcon icon, int size, IconKind? fallback)
    {
        IconExtraction.Pixels? pixels = null;
        try
        {
            pixels = IconExtraction.TryLoadModuleIcon(icon, size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogOnce($"{icon} icon load failed: {ex.Message}");
        }

        if (pixels is null)
        {
            LogOnce($"Using the BWC fallback glyph for {icon} at {size} px.");
            return fallback is { } kind ? FallbackGlyph.Render(kind, size) : FallbackGlyph.RenderGeneric(size);
        }

        return pixels.Value;
    }

    /// <summary>The icon kind for a value type. String types use the "ab" icon, every other type the binary icon.</summary>
    public static IconKind ValueIconKind(string registryType) => registryType switch
    {
        "REG_SZ" or "REG_EXPAND_SZ" or "REG_MULTI_SZ" => IconKind.StringValue,
        _ => IconKind.BinaryValue,
    };

    private static IconExtraction.Pixels Load(IconKind kind, int size)
    {
        IconExtraction.Pixels? pixels = null;
        try
        {
            pixels = IconExtraction.TryLoadRegeditIcon(kind, size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogOnce($"regedit.exe icon load failed: {ex.Message}");
        }

        if (pixels is null && kind is IconKind.Computer or IconKind.ClosedFolder)
        {
            try
            {
                pixels = IconExtraction.TryLoadStockIcon(kind, size);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogOnce($"stock icon load failed: {ex.Message}");
            }
        }

        if (pixels is null)
        {
            LogOnce($"Using the BWC fallback glyph for {kind} at {size} px.");
            return FallbackGlyph.Render(kind, size);
        }

        return pixels.Value;
    }

    private static ImageSource CreateBitmap(IconExtraction.Pixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        pixels.Bgra.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        return bitmap;
    }

    private static void LogOnce(string message)
    {
        if (_loadFailureLogged)
        {
            return;
        }

        _loadFailureLogged = true;
        System.Diagnostics.Debug.WriteLine($"[Bwc.UI.Icons] {message}");
    }
}
