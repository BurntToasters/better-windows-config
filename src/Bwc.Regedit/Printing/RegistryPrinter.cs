using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bwc.Core;
using Bwc.Registry;

namespace Bwc.Regedit.Printing;

/// <summary>The outcome of one print request.</summary>
public enum PrintStatus
{
    Printed,
    Cancelled,
    Failed,
}

/// <summary>
/// A print request result. Cancellation is not an error, so its code is zero and its message is
/// empty. A failure always carries the Win32 or common dialog code and a readable message.
/// </summary>
public readonly record struct PrintResult(PrintStatus Status, int ErrorCode, string Message)
{
    public static PrintResult Printed() => new(PrintStatus.Printed, 0, string.Empty);

    public static PrintResult Cancelled() => new(PrintStatus.Cancelled, 0, string.Empty);

    public static PrintResult Failed(int errorCode, string message) =>
        new(PrintStatus.Failed, errorCode, message);
}

/// <summary>
/// Owns one device context and the DEVMODE and DEVNAMES blocks allocated by PrintDlg. The context
/// may be printed through on a worker thread, but Dispose must run exactly once after the job.
/// </summary>
public sealed class PrintDevice : IDisposable
{
    private bool _disposed;

    internal PrintDevice(nint deviceContext, nint devMode, nint devNames)
    {
        DeviceContext = deviceContext;
        DevMode = devMode;
        DevNames = devNames;
    }

    internal nint DeviceContext { get; }

    private nint DevMode { get; }

    private nint DevNames { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (DeviceContext != 0 && !PrintNativeMethods.DeleteDC(DeviceContext))
        {
            Log.Warn("Registry Editor could not delete the print device context.");
        }

        FreeGlobal(DevMode, "DEVMODE");
        FreeGlobal(DevNames, "DEVNAMES");
    }

    private static void FreeGlobal(nint handle, string name)
    {
        if (handle == 0)
        {
            return;
        }

        if (PrintNativeMethods.GlobalFree(handle) != 0)
        {
            Log.Warn($"Registry Editor could not free the print dialog {name} block.");
        }
    }
}

/// <summary>
/// Builds the classic registry print report and sends it to the device returned by PrintDlgW. The
/// dialog is always shown by the caller's UI thread; the caller may then run
/// <see cref="PrintToDevice"/> on the thread pool. The service never writes to the registry.
/// </summary>
public static class RegistryPrinter
{
    public const int DefaultMaxChars = 1_000_000;

    private const int ValueDataMaxChars = 512;
    private const string TruncationMarker = "\n[Report truncated at the size limit]\n";

    // commdlg.h flags. The command prints the selected branch only, so the Selection and Pages
    // controls are removed. PD_RETURNDC gives us the HDC to draw into, and print-to-file is hidden
    // because this implementation cannot write a spool file itself.
    private const uint PdNoSelection = 0x00000004;
    private const uint PdNoPageNumbers = 0x00000008;
    private const uint PdReturnDc = 0x00000100;
    private const uint PdUseDevModeCopiesAndCollate = 0x00040000;
    private const uint PdHidePrintToFile = 0x00100000;
    private const uint PrintDialogFlags =
        PdNoSelection |
        PdNoPageNumbers |
        PdReturnDc |
        PdUseDevModeCopiesAndCollate |
        PdHidePrintToFile;

    // wingdi.h indexes used by GetDeviceCaps.
    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private const int DefaultDpi = 96;

    /// <summary>
    /// Shows the Windows print dialog and then prints the selected key synchronously on this
    /// thread. The UI uses <see cref="TryShowPrintDialog"/> plus <see cref="PrintToDevice"/> so the
    /// GDI work does not run on the dispatcher.
    /// </summary>
    public static PrintResult Print(nint ownerHwnd, RegistryReader reader, RegistryPath startKey, PrintReportLabels labels)
    {
        if (!TryShowPrintDialog(ownerHwnd, out PrintDevice? device, out PrintResult dialogResult))
        {
            return dialogResult;
        }

        using (device)
        {
            return PrintToDevice(device!, reader, startKey, labels);
        }
    }

    /// <summary>
    /// Shows the owned modal print dialog. On success the caller owns the returned device and must
    /// dispose it after printing. Cancellation and dialog failures return false with the result.
    /// </summary>
    public static bool TryShowPrintDialog(
        nint ownerHwnd,
        out PrintDevice? device,
        out PrintResult result)
    {
        device = null;
        var dialog = new PRINTDLGW
        {
            structSize = (uint)Marshal.SizeOf<PRINTDLGW>(),
            owner = ownerHwnd,
            flags = PrintDialogFlags,
        };

        bool accepted;
        try
        {
            accepted = PrintNativeMethods.PrintDlgW(ref dialog);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            FreeDialogBlocks(dialog.devMode, dialog.devNames);
            result = PrintResult.Failed(0, "The Windows print dialog is not available: " + ex.Message);
            return false;
        }

        if (!accepted)
        {
            uint dialogError = PrintNativeMethods.CommDlgExtendedError();
            FreeDialogBlocks(dialog.devMode, dialog.devNames);

            if (dialogError == 0)
            {
                result = PrintResult.Cancelled();
                return false;
            }

            result = PrintResult.Failed(
                (int)dialogError,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The Windows print dialog failed with common dialog error 0x{0:X4}.",
                    dialogError));
            return false;
        }

        if (dialog.deviceContext == 0)
        {
            FreeDialogBlocks(dialog.devMode, dialog.devNames);
            result = PrintResult.Failed(0, "The Windows print dialog returned no printer device context.");
            return false;
        }

        device = new PrintDevice(dialog.deviceContext, dialog.devMode, dialog.devNames);
        result = PrintResult.Printed();
        return true;
    }

    /// <summary>
    /// Formats the report without touching a printer. The selected key and each readable descendant
    /// use registry enumeration order. Each key starts with the classic Key Name, Class Name, and Last
    /// Write Time lines, and each value has a Value n block with its name, type, data size, and data
    /// (oracle strings 8101 to 8110). Throws <see cref="RegistryOperationException"/> when the selected
    /// key itself cannot be read. An unreadable descendant is marked and skipped.
    /// </summary>
    public static string FormatReport(
        RegistryReader reader,
        RegistryPath startKey,
        int maxChars,
        PrintReportLabels labels)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(startKey);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        if (maxChars == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        var pending = new Stack<(RegistryPath Path, bool IsStart)>();
        pending.Push((startKey, true));
        bool truncated = false;

        while (pending.Count > 0)
        {
            (RegistryPath path, bool isStart) = pending.Pop();

            RegistryKeyInfo info;
            try
            {
                info = reader.GetKeyInfo(path);
            }
            catch (RegistryOperationException ex) when (!isStart)
            {
                if (!TryAppend(
                        builder,
                        "(not readable: Win32 " + ex.Win32Error.ToString(CultureInfo.InvariantCulture) + ")\n\n",
                        maxChars,
                        ref truncated))
                {
                    break;
                }

                continue;
            }

            string className = string.IsNullOrEmpty(info.ClassName) ? labels.NoClass : info.ClassName;
            string header =
                labels.KeyName + path.ToDisplayString() + "\n" +
                labels.ClassName + className + "\n" +
                labels.LastWriteTime + info.LastWriteTimeUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) + "\n\n";

            if (!TryAppend(builder, header, maxChars, ref truncated))
            {
                break;
            }

            IReadOnlyList<RegistryValueInfo> values;
            try
            {
                values = reader.EnumerateValues(path);
            }
            catch (RegistryOperationException ex) when (!isStart)
            {
                if (!TryAppend(
                        builder,
                        "(values not readable: Win32 " + ex.Win32Error.ToString(CultureInfo.InvariantCulture) + ")\n\n",
                        maxChars,
                        ref truncated))
                {
                    break;
                }

                continue;
            }

            for (int index = 0; index < values.Count; index++)
            {
                RegistryValueInfo value = values[index];
                string name = value.Name.Length == 0 ? labels.NoName : value.Name;
                string block =
                    labels.ValueHeading + (index + 1).ToString(CultureInfo.InvariantCulture) + "\n" +
                    labels.ValueName + Sanitize(name) + "\n" +
                    labels.ValueType + RegistryValueDisplay.FormatType(value) + "\n" +
                    labels.ValueDataSize + value.DataLength.ToString(CultureInfo.InvariantCulture) + "\n" +
                    labels.ValueData + Sanitize(RegistryValueDisplay.FormatData(value, ValueDataMaxChars)) + "\n\n";

                if (!TryAppend(builder, block, maxChars, ref truncated))
                {
                    break;
                }
            }

            if (truncated)
            {
                break;
            }

            IReadOnlyList<string> childNames;
            try
            {
                childNames = reader.EnumerateSubKeyNames(path);
            }
            catch (RegistryOperationException ex) when (!isStart)
            {
                if (!TryAppend(
                        builder,
                        "(subkeys not readable: Win32 " +
                        ex.Win32Error.ToString(CultureInfo.InvariantCulture) + ")\n\n",
                        maxChars,
                        ref truncated))
                {
                    break;
                }

                continue;
            }

            for (int index = childNames.Count - 1; index >= 0; index -= 1)
            {
                RegistryPath child;
                try
                {
                    child = path.Append(childNames[index]);
                }
                catch (ArgumentException)
                {
                    // Enumeration returned a name that RegistryPath cannot represent. Skip it
                    // instead of failing the whole report.
                    continue;
                }

                pending.Push((child, false));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The classic report labels, read from the Registry Editor resources. Each one already carries its
    /// own spacing, as the oracle strings do, so the values line up in the same columns.
    /// </summary>
    public sealed record PrintReportLabels(
        string KeyName,
        string ClassName,
        string LastWriteTime,
        string ValueHeading,
        string ValueName,
        string ValueType,
        string ValueDataSize,
        string ValueData,
        string NoName,
        string NoClass);

    /// <summary>
    /// Draws a formatted report through an existing device context. Every GDI call is checked; a
    /// failure after StartDoc is aborted so a partial document is not left in the spooler.
    public static PrintResult PrintToDevice(
        PrintDevice device,
        RegistryReader reader,
        RegistryPath startKey,
        PrintReportLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(startKey);

        string report;
        try
        {
            report = FormatReport(reader, startKey, DefaultMaxChars, labels);
        }
        catch (RegistryOperationException ex)
        {
            return PrintResult.Failed(ex.Win32Error, ex.Message);
        }

        nint deviceContext = device.DeviceContext;
        int dpiX = PrintNativeMethods.GetDeviceCaps(deviceContext, LogPixelsX);
        int dpiY = PrintNativeMethods.GetDeviceCaps(deviceContext, LogPixelsY);
        if (dpiX <= 0)
        {
            dpiX = DefaultDpi;
        }

        if (dpiY <= 0)
        {
            dpiY = DefaultDpi;
        }

        int fontHeight = -(int)Math.Max(1L, (9L * dpiY) / 72L);
        nint font = PrintNativeMethods.CreateFontW(
            fontHeight,
            0,
            0,
            0,
            400,
            0,
            0,
            0,
            1, // DEFAULT_CHARSET
            0, // OUT_DEFAULT_PRECIS
            0, // CLIP_DEFAULT_PRECIS
            0, // DEFAULT_QUALITY
            0, // DEFAULT_PITCH | FF_DONTCARE
            "Consolas");

        if (font == 0)
        {
            int win32Error = Marshal.GetLastWin32Error();
            return PrintResult.Failed(
                win32Error,
                "Could not create the Consolas 9 point printer font (Win32 error " +
                win32Error.ToString(CultureInfo.InvariantCulture) + ").");
        }

        nint previousFont = PrintNativeMethods.SelectObject(deviceContext, font);
        if (previousFont == 0 || previousFont == PrintNativeMethods.HgdiError)
        {
            int win32Error = Marshal.GetLastWin32Error();
            _ = PrintNativeMethods.DeleteObject(font);
            return PrintResult.Failed(
                win32Error,
                "Could not select the printer font (Win32 error " +
                win32Error.ToString(CultureInfo.InvariantCulture) + ").");
        }

        try
        {
            return PrintReport(deviceContext, report, dpiX, dpiY, startKey);
        }
        finally
        {
            _ = PrintNativeMethods.SelectObject(deviceContext, previousFont);
            _ = PrintNativeMethods.DeleteObject(font);
        }
    }

    private static PrintResult PrintReport(
        nint deviceContext,
        string report,
        int dpiX,
        int dpiY,
        RegistryPath startKey)
    {
        int pageWidth = PrintNativeMethods.GetDeviceCaps(deviceContext, HorzRes);
        int pageHeight = PrintNativeMethods.GetDeviceCaps(deviceContext, VertRes);
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return PrintResult.Failed(
                0,
                "The printer did not report a usable page size.");
        }

        int characterWidth = MeasureCharacterWidth(deviceContext);
        int lineHeight = MeasureLineHeight(deviceContext);
        if (characterWidth <= 0 || lineHeight <= 0)
        {
            return PrintResult.Failed(
                0,
                "The printer font could not be measured; the report was not printed.");
        }

        int marginX = Math.Max(1, (dpiX * 3) / 4);
        int marginY = Math.Max(1, (dpiY * 3) / 4);
        int left = marginX;
        int top = marginY;
        int right = pageWidth - marginX;
        int bottom = pageHeight - marginY;
        if (right <= left || bottom <= top)
        {
            return PrintResult.Failed(
                0,
                "The printer page is too small for the configured margins.");
        }

        int columns = Math.Max(1, (right - left) / characterWidth);
        var document = new DOCINFOW
        {
            structSize = Marshal.SizeOf<DOCINFOW>(),
            documentName = "Registry Editor - " + startKey.ToDisplayString(),
        };

        if (PrintNativeMethods.StartDocW(deviceContext, ref document) <= 0)
        {
            int win32Error = Marshal.GetLastWin32Error();
            return FailedAt("StartDoc", win32Error);
        }

        bool documentStarted = true;
        try
        {
            if (PrintNativeMethods.StartPage(deviceContext) <= 0)
            {
                return FailedAt("StartPage", Marshal.GetLastWin32Error());
            }

            int y = top;
            foreach (string rawLine in report.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.Length == 0)
                {
                    if (y + lineHeight > bottom)
                    {
                        PrintResult pageResult = StartNextPage(deviceContext, ref y, top);
                        if (pageResult.Status == PrintStatus.Failed)
                        {
                            return pageResult;
                        }
                    }

                    y += lineHeight;
                    continue;
                }

                int offset = 0;
                while (offset < line.Length)
                {
                    if (y + lineHeight > bottom)
                    {
                        PrintResult pageResult = StartNextPage(deviceContext, ref y, top);
                        if (pageResult.Status == PrintStatus.Failed)
                        {
                            return pageResult;
                        }
                    }

                    int chunkLength = Math.Min(columns, line.Length - offset);
                    string chunk = line.Substring(offset, chunkLength);
                    if (!PrintNativeMethods.TextOutW(deviceContext, left, y, chunk, chunkLength))
                    {
                        return FailedAt("TextOut", Marshal.GetLastWin32Error());
                    }

                    y += lineHeight;
                    offset += chunkLength;
                }
            }

            if (PrintNativeMethods.EndPage(deviceContext) <= 0)
            {
                return FailedAt("EndPage", Marshal.GetLastWin32Error());
            }

            if (PrintNativeMethods.EndDoc(deviceContext) <= 0)
            {
                return FailedAt("EndDoc", Marshal.GetLastWin32Error());
            }

            documentStarted = false;
            return PrintResult.Printed();
        }
        finally
        {
            if (documentStarted)
            {
                if (PrintNativeMethods.AbortDoc(deviceContext) <= 0)
                {
                    Log.Warn(
                        "Registry Editor could not abort the failed print job, Win32 error " +
                        Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
        }
    }

    private static PrintResult StartNextPage(nint deviceContext, ref int y, int top)
    {
        if (PrintNativeMethods.EndPage(deviceContext) <= 0)
        {
            return FailedAt("EndPage", Marshal.GetLastWin32Error());
        }

        if (PrintNativeMethods.StartPage(deviceContext) <= 0)
        {
            return FailedAt("StartPage", Marshal.GetLastWin32Error());
        }

        y = top;
        return PrintResult.Printed();
    }

    private static int MeasureCharacterWidth(nint deviceContext)
    {
        const string sample = "0000000000";
        if (!PrintNativeMethods.GetTextExtentPoint32W(
                deviceContext,
                sample,
                sample.Length,
                out SIZE size) ||
            size.cx <= 0)
        {
            return 0;
        }

        return Math.Max(1, size.cx / sample.Length);
    }

    private static int MeasureLineHeight(nint deviceContext)
    {
        if (!PrintNativeMethods.GetTextExtentPoint32W(deviceContext, "Ag", 2, out SIZE size) ||
            size.cy <= 0)
        {
            return 0;
        }

        return size.cy + Math.Max(1, size.cy / 5);
    }

    private static PrintResult FailedAt(string stage, int win32Error) =>
        PrintResult.Failed(
            win32Error,
            stage + " failed with Win32 error " +
            win32Error.ToString(CultureInfo.InvariantCulture) + ".");

    private static bool TryAppend(
        StringBuilder builder,
        string text,
        int maxChars,
        ref bool truncated)
    {
        int remaining = maxChars - builder.Length;
        if (remaining <= 0)
        {
            truncated = true;
            return false;
        }

        if (text.Length <= remaining)
        {
            builder.Append(text);
            return true;
        }

        int markerLength = Math.Min(remaining, TruncationMarker.Length);
        builder.Append(TruncationMarker, 0, markerLength);
        truncated = true;
        return false;
    }

    private static string Sanitize(string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (char character in text)
        {
            switch (character)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void FreeDialogBlocks(nint devMode, nint devNames)
    {
        if (devMode != 0 && PrintNativeMethods.GlobalFree(devMode) != 0)
        {
            Log.Warn("Registry Editor could not free the print dialog DEVMODE block.");
        }

        if (devNames != 0 && PrintNativeMethods.GlobalFree(devNames) != 0)
        {
            Log.Warn("Registry Editor could not free the print dialog DEVNAMES block.");
        }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PRINTDLGW
{
    public uint structSize;
    public nint owner;
    public nint devMode;
    public nint devNames;
    public nint deviceContext;
    public uint flags;
    public ushort fromPage;
    public ushort toPage;
    public ushort minPage;
    public ushort maxPage;
    public ushort copies;
    public nint instance;
    public nint customData;
    public nint printHook;
    public nint setupHook;
    public nint printTemplateName;
    public nint setupTemplateName;
    public nint printTemplate;
    public nint setupTemplate;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DOCINFOW
{
    public int structSize;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string documentName;

    public nint output;
    public nint dataType;
    public uint type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

#pragma warning disable SYSLIB1054 // The small, fixed print surface does not justify LibraryImport marshalling plumbing.
internal static class PrintNativeMethods
{
    internal const nint HgdiError = -1;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrintDlgW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintDlgW(ref PRINTDLGW dialog);

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    internal static extern uint CommDlgExtendedError();

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW", ExactSpelling = true, SetLastError = true)]
    internal static extern nint CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint SelectObject(nint deviceContext, nint gdiObject);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int GetDeviceCaps(nint deviceContext, int index);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextExtentPoint32W(
        nint deviceContext,
        string text,
        int length,
        out SIZE size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartDocW", ExactSpelling = true, SetLastError = true)]
    internal static extern int StartDocW(nint deviceContext, ref DOCINFOW document);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int StartPage(nint deviceContext);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int EndPage(nint deviceContext);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int EndDoc(nint deviceContext);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int AbortDoc(nint deviceContext);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TextOutW(
        nint deviceContext,
        int x,
        int y,
        string text,
        int length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint GlobalFree(nint memory);
}
#pragma warning restore SYSLIB1054
