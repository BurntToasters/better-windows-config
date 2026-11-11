using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Dialog base units for one font at one DPI, and the DLU-to-pixel conversion Windows uses for
/// dialog templates (<c>MapDialogRect</c>): x = MulDiv(dlu, baseX, 4), y = MulDiv(dlu, baseY, 8).
/// The base units come from the font: baseX is the average character width of the 52 ASCII
/// letters, and baseY is the font height. The font is created at the requested DPI, so the
/// result matches what a dialog created on a window of that DPI would use.
/// </summary>
public readonly record struct ClassicDialogMetrics
{
    /// <summary>The face name classic templates declare. FontSubstitutes maps it to a real face.</summary>
    public const string TemplateFontFace = "MS Shell Dlg";

    private const string FallbackFace = "Microsoft Sans Serif";
    private const string LetterSample = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private ClassicDialogMetrics(int baseX, int baseY, uint dpi, string faceName)
    {
        BaseX = baseX;
        BaseY = baseY;
        Dpi = dpi;
        FaceName = faceName;
    }

    /// <summary>Average character width in pixels (the x unit of a DLU is baseX / 4).</summary>
    public int BaseX { get; }

    /// <summary>Font height in pixels (the y unit of a DLU is baseY / 8).</summary>
    public int BaseY { get; }

    /// <summary>The DPI the base units were measured at.</summary>
    public uint Dpi { get; }

    /// <summary>The face the font was created with after substitution.</summary>
    public string FaceName { get; }

    /// <summary>Scale relative to 96 DPI: 1.0 at 100 %, 2.0 at 200 %.</summary>
    public double Scale => Dpi == 0 ? 1.0 : Dpi / 96.0;

    /// <summary>Creates the metrics for a font at a DPI. A zero DPI is treated as 96.</summary>
    public static ClassicDialogMetrics Create(string faceName, int pointSize, uint dpi)
    {
        ArgumentException.ThrowIfNullOrEmpty(faceName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pointSize, 0);

        uint effectiveDpi = dpi == 0 ? 96u : dpi;
        string resolved = ResolveFaceName(faceName);
        (int baseX, int baseY) = Measure(resolved, pointSize, effectiveDpi);
        return new ClassicDialogMetrics(baseX, baseY, effectiveDpi, resolved);
    }

    /// <summary>Converts a horizontal DLU value to pixels, rounded as Windows rounds it.</summary>
    public int ToPixelsX(int dlu) => MulDiv(dlu, BaseX, 4);

    /// <summary>Converts a vertical DLU value to pixels, rounded as Windows rounds it.</summary>
    public int ToPixelsY(int dlu) => MulDiv(dlu, BaseY, 8);

    /// <summary>Converts a horizontal DLU value to device independent pixels.</summary>
    public double ToDipX(int dlu) => ToPixelsX(dlu) / Scale;

    /// <summary>Converts a vertical DLU value to device independent pixels.</summary>
    public double ToDipY(int dlu) => ToPixelsY(dlu) / Scale;

    /// <summary>
    /// Maps a template face name to the face GDI and XAML use. "MS Shell Dlg" becomes the face in
    /// FontSubstitutes (Microsoft Sans Serif on the target machine). Other names pass through.
    /// </summary>
    public static string ResolveFaceName(string faceName)
    {
        if (!string.Equals(faceName, TemplateFontFace, StringComparison.OrdinalIgnoreCase))
        {
            return faceName;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontSubstitutes",
                writable: false);
            if (key?.GetValue(TemplateFontFace) is string substitute && substitute.Length > 0)
            {
                int comma = substitute.IndexOf(',', StringComparison.Ordinal);
                string face = (comma < 0 ? substitute : substitute[..comma]).Trim();
                if (face.Length > 0)
                {
                    return face;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The substitution table is unreadable. The fallback face is the same on Windows 10 and 11.
        }

        return FallbackFace;
    }

    /// <summary>Rounded integer product, the same rounding as the Win32 MulDiv function.</summary>
    internal static int MulDiv(int number, int numerator, int denominator)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(denominator, 0);
        long product = (long)number * numerator;
        long half = denominator / 2;
        long rounded = product >= 0 ? (product + half) / denominator : (product - half) / denominator;
        return (int)rounded;
    }

    private static (int BaseX, int BaseY) Measure(string faceName, int pointSize, uint dpi)
    {
        int height = -MulDiv(pointSize, (int)dpi, 72);
        nint dc = ClassicGdi.CreateCompatibleDC(0);
        if (dc == 0)
        {
            throw new InvalidOperationException("CreateCompatibleDC failed while measuring dialog units.");
        }

        nint font = 0;
        nint previous = 0;
        try
        {
            font = ClassicGdi.CreateFontW(
                height,
                0,
                0,
                0,
                ClassicGdi.FwNormal,
                0,
                0,
                0,
                ClassicGdi.DefaultCharset,
                ClassicGdi.OutDefaultPrecis,
                ClassicGdi.ClipDefaultPrecis,
                ClassicGdi.DefaultQuality,
                ClassicGdi.DefaultPitch,
                faceName);
            if (font == 0)
            {
                throw new InvalidOperationException($"CreateFontW failed for '{faceName}'.");
            }

            previous = ClassicGdi.SelectObject(dc, font);
            int tmHeight = ReadTextMetricHeight(dc);

            if (ClassicGdi.GetTextExtentPoint32W(dc, LetterSample, LetterSample.Length, out ClassicGdi.GdiSize size) == 0)
            {
                throw new InvalidOperationException("GetTextExtentPoint32W failed while measuring dialog units.");
            }

            int baseX = (size.Cx / 26 + 1) / 2;
            return (baseX, tmHeight);
        }
        finally
        {
            if (previous != 0)
            {
                _ = ClassicGdi.SelectObject(dc, previous);
            }

            if (font != 0)
            {
                _ = ClassicGdi.DeleteObject(font);
            }

            _ = ClassicGdi.DeleteDC(dc);
        }
    }

    private static unsafe int ReadTextMetricHeight(nint dc)
    {
        // TEXTMETRICW is 60 bytes. tmHeight is its first field, so a larger buffer is safe.
        byte[] buffer = new byte[256];
        fixed (byte* pointer = buffer)
        {
            if (ClassicGdi.GetTextMetricsW(dc, pointer) == 0)
            {
                throw new InvalidOperationException("GetTextMetricsW failed while measuring dialog units.");
            }
        }

        return BitConverter.ToInt32(buffer, 0);
    }
}

/// <summary>GDI entry points used only to measure dialog base units.</summary>
internal static partial class ClassicGdi
{
    internal const int FwNormal = 400;
    internal const uint DefaultCharset = 1;
    internal const uint OutDefaultPrecis = 0;
    internal const uint ClipDefaultPrecis = 0;
    internal const uint DefaultQuality = 0;
    internal const uint DefaultPitch = 0;

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int DeleteObject(nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextMetricsW", SetLastError = true)]
    internal static unsafe partial int GetTextMetricsW(nint hdc, byte* metrics);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetTextExtentPoint32W(nint hdc, string text, int count, out GdiSize size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiSize
    {
        public int Cx;
        public int Cy;
    }
}
