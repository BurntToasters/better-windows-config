using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Bwc.Core;
using Bwc.UI.Theme;

namespace Bwc.Regedit.Dialogs;

/// <summary>The point size the user chose in the system font dialog.</summary>
public readonly record struct FontChoice(double PointSize, string FaceName, int Weight, bool Italic);

/// <summary>
/// The system ChooseFont dialog for the face, style (weight and italic), and size. The rows and the
/// shell read the choice from <see cref="Bwc.UI.Theme.DenseMetrics"/>.
/// </summary>
public static class FontChooser
{
    /// <summary>The smallest and largest sizes ChooseFont accepts, in points.</summary>
    public const int MinimumPointSize = 6;
    public const int MaximumPointSize = 72;

    private const uint CfScreenFonts = 0x00000001;
    private const uint CfInitToLogFontStruct = 0x00000040;
    private const uint CfLimitSize = 0x00002000;

    /// <summary>
    /// Shows the font dialog owned by <paramref name="owner"/>, starting at <paramref name="current"/>.
    /// Returns null on Cancel or when the dialog cannot be shown.
    /// </summary>
    public static FontChoice? Show(nint owner, FontChoice current)
    {
        if (owner == 0)
        {
            return null;
        }

        uint dpi = ShellDialogNative.GetDpiForWindow(owner);
        if (dpi == 0)
        {
            dpi = 96;
        }

        int startPoints = Math.Clamp(
            (int)Math.Round(current.PointSize, MidpointRounding.AwayFromZero),
            MinimumPointSize,
            MaximumPointSize);

        unsafe
        {
            var logFont = new ShellDialogNative.LogFontStruct
            {
                lfHeight = -(int)Math.Round(startPoints * dpi / 72.0, MidpointRounding.AwayFromZero),
                lfCharSet = 1,
                lfWeight = Math.Clamp(current.Weight, 100, 900),
                lfItalic = (byte)(current.Italic ? 1 : 0),
            };

            // The dialog opens on the face, weight, and italic style in use. All three come back below.
            string face = string.IsNullOrWhiteSpace(current.FaceName) ? DenseMetrics.FontFamilyName : current.FaceName;
            for (int index = 0; index < face.Length && index < 31; index++)
            {
                logFont.lfFaceName[index] = face[index];
            }

            var choose = new ShellDialogNative.ChooseFontStruct
            {
                lStructSize = (uint)Marshal.SizeOf<ShellDialogNative.ChooseFontStruct>(),
                hwndOwner = owner,
                lpLogFont = &logFont,
                iPointSize = startPoints * 10,
                Flags = CfScreenFonts | CfInitToLogFontStruct | CfLimitSize,
                nSizeMin = MinimumPointSize,
                nSizeMax = MaximumPointSize,
            };

            if (!ShellDialogNative.ChooseFontW(ref choose))
            {
                return null;
            }

            var chosenFace = new System.Text.StringBuilder();
            for (int index = 0; index < 32 && logFont.lfFaceName[index] != '\0'; index++)
            {
                chosenFace.Append(logFont.lfFaceName[index]);
            }

            string picked = chosenFace.Length == 0 ? face : chosenFace.ToString();
            return new FontChoice(
                Math.Clamp(choose.iPointSize / 10.0, MinimumPointSize, MaximumPointSize),
                picked,
                Math.Clamp(logFont.lfWeight, 100, 900),
                logFont.lfItalic != 0);
        }
    }
}

/// <summary>
/// Help > About. Classic regedit calls ShellAbout, and the oracle has no About dialog resource, so
/// the native box is the closest match.
/// </summary>
public static class AboutBox
{
    private const string ProductFallback = "Better Windows Config";

    /// <summary>
    /// Shows the About box owned by <paramref name="owner"/>. The icon comes from the executable when a
    /// path is given. Returns false when the call failed.
    /// </summary>
    public static bool Show(nint owner, string applicationName, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentNullException.ThrowIfNull(executablePath);

        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AboutBox).Assembly;
        string text = BuildText(assembly);
        nint icon = string.IsNullOrEmpty(executablePath)
            ? 0
            : ShellDialogNative.ExtractIconW(0, executablePath, 0);
        int result;
        try
        {
            result = ShellDialogNative.ShellAboutW(owner, applicationName, text, icon);
        }
        finally
        {
            // ExtractIconW returns a copy the caller owns, so it is released once ShellAbout has used it.
            if (icon != 0)
            {
                _ = ShellDialogNative.DestroyIcon(icon);
            }
        }

        if (result == 0)
        {
            Log.Warn("Registry Editor could not show the About box.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The lines below the title: product, version, and copyright. The version is the informational
    /// version without build metadata, the same source the Hub uses.
    /// </summary>
    public static string BuildText(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? ProductFallback;
        string copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        return product + "\r\n" +
            RegeditStrings.Get("AboutVersion").Replace("{0}", VersionText(assembly), StringComparison.Ordinal) + "\r\n" +
            copyright;
    }

    /// <summary>The informational version without "+metadata", or the file version when none is set.</summary>
    public static string VersionText(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return (plus < 0 ? informational : informational[..plus]).Trim();
        }

        Version? version = assembly.GetName().Version;
        if (version is null)
        {
            return RegeditStrings.Get("AboutVersionUnknown");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");
    }
}

internal static unsafe partial class ShellDialogNative
{
    /// <summary>LOGFONTW. The face name is a fixed buffer so the struct is blittable.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LogFontStruct
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
        public fixed char lfFaceName[32];
    }

    /// <summary>CHOOSEFONTW with the 64-bit layout. The padding field keeps nSizeMin at its offset.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ChooseFontStruct
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hDC;
        public LogFontStruct* lpLogFont;
        public int iPointSize;
        public uint Flags;
        public uint rgbColors;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint hInstance;
        public nint lpszStyle;
        public ushort nFontType;
        public ushort MissingAlignment;
        public int nSizeMin;
        public int nSizeMax;
    }

#pragma warning disable SYSLIB1054 // The struct holds a fixed buffer and a pointer, which DllImport marshals directly.
    [DllImport("comdlg32.dll", EntryPoint = "ChooseFontW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChooseFontW(ref ChooseFontStruct choose);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int DestroyIcon(nint icon);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ShellAboutW(nint hwnd, string applicationName, string otherStuff, nint icon);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint ExtractIconW(nint instance, string file, uint iconIndex);
}
