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

    internal PrintDevice(nint deviceContext, nint devMode, nint devNames, string deviceName)
    {
        DeviceContext = deviceContext;
        DevMode = devMode;
        DevNames = devNames;
        DeviceName = deviceName;
    }

    internal nint DeviceContext { get; }

    private nint DevMode { get; }

    private nint DevNames { get; }

    /// <summary>The printer name from the dialog or the CreateDC call. Empty when it is not known.</summary>
    public string DeviceName { get; }

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
/// Sends the registry report to the device returned by PrintDlgEx. The report is the text export format
/// (<see cref="RegistryExportService.ExportTextFile(RegistryPath?, TextWriter, RegistryExportScope,
/// CancellationToken, IProgress{ExportProgress}?)"/>), so print and File > Export write the same text. The
/// dialog is always shown by the caller's UI thread; the caller may then run <see cref="PrintToDevice"/> on
/// the thread pool. The service never writes to the registry.
/// </summary>
public static class RegistryPrinter
{
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

    // commdlg.h PrintDlgEx values: PD_PAGENOTSPECIFIED, PD_RESULT_PRINT (cancel is 0, apply is 2), and S_FALSE.
    private const uint PdPageNotSpecified = 0xFFFFFFFF;
    private const uint PdResultPrint = 1;
    private const int SFalse = 1;

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
    public static PrintResult Print(nint ownerHwnd, RegistryReader reader, RegistryPath startKey)
    {
        if (!TryShowPrintDialog(ownerHwnd, out PrintDevice? device, out PrintResult dialogResult))
        {
            return dialogResult;
        }

        using (device)
        {
            return PrintToDevice(device!, reader, startKey);
        }
    }

    /// <summary>
    /// Writes the report as the text export writes it. A null start key is the whole registry, for the
    /// Computer node. Streams key by key, with no size cap and no per-value cut. Nothing is written to the
    /// registry, and a start key that cannot be read throws <see cref="RegistryOperationException"/>.
    /// </summary>
    public static void WriteReport(
        RegistryReader reader,
        RegistryPath? startKey,
        TextWriter writer,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null,
        Func<RegistryPath, string>? keyDisplay = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        RegistryExportScope scope = startKey is null
            ? RegistryExportScope.WholeRegistry
            : RegistryExportScope.Selection;
        new RegistryExportService(reader).ExportTextFile(startKey, writer, scope, cancellationToken, progress, keyDisplay);
    }

#if BWC_E2E
    /// <summary>The outcome of a named-printer job, and whether a device context was created for it.</summary>
    internal readonly record struct E2EPrintOutcome(PrintResult Result, bool DeviceContextCreated);

    /// <summary>
    /// E2E only. The guarded path for a named printer. The guard runs before CreateDC, so a refused name or output
    /// never creates a device context. The PDF test uses this path, and the guard probe uses it with refused names.
    /// </summary>
    internal static E2EPrintOutcome PrintNamedForE2E(
        string printerName,
        string? outputFile,
        RegistryReader reader,
        RegistryPath? startKey,
        CancellationToken cancellationToken,
        Func<RegistryPath, string>? keyDisplay = null)
    {
        if (!PrintE2EGuard.Allows(printerName, outputFile, out string refusal))
        {
            return new E2EPrintOutcome(PrintE2EGuard.Refuse(refusal), DeviceContextCreated: false);
        }

        nint deviceContext = PrintNativeMethods.CreateDCW("WINSPOOL", printerName, null, 0);
        if (deviceContext == 0)
        {
            int win32Error = Marshal.GetLastWin32Error();
            return new E2EPrintOutcome(
                PrintResult.Failed(win32Error, FailedAt("CreateDC", win32Error).Message),
                DeviceContextCreated: false);
        }

        using var device = new PrintDevice(deviceContext, 0, 0, printerName);
        return new E2EPrintOutcome(
            PrintJob(device, reader, startKey, cancellationToken, outputFile, keyDisplay),
            DeviceContextCreated: true);
    }
#endif

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

        // PrintDlgEx takes no template handle, so the dialog is the system's own. The range is decided by the caller:
        // Selected branch at a key, All at Computer (docs/failure-modes/print.md).
        var dialog = new PRINTDLGEXW
        {
            lStructSize = (uint)Marshal.SizeOf<PRINTDLGEXW>(),
            hwndOwner = ownerHwnd,
            Flags = PrintDialogFlags,
            nStartPage = PdPageNotSpecified,
        };

        int hresult;
        try
        {
            hresult = PrintNativeMethods.PrintDlgExW(ref dialog);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            FreeDialogBlocks(dialog.hDevMode, dialog.hDevNames);
            result = PrintResult.Failed(0, "The Windows print dialog is not available: " + ex.Message);
            return false;
        }

        if (hresult < 0)
        {
            FreeDialogBlocks(dialog.hDevMode, dialog.hDevNames);
            result = PrintResult.Failed(
                hresult,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The Windows print dialog failed with HRESULT 0x{0:X8}.",
                    hresult));
            return false;
        }

        // S_FALSE and PD_RESULT_CANCEL both mean the user cancelled. Only PD_RESULT_PRINT starts a job.
        if (hresult == SFalse || dialog.dwResultAction != PdResultPrint)
        {
            FreeDialogBlocks(dialog.hDevMode, dialog.hDevNames);
            result = PrintResult.Cancelled();
            return false;
        }

        if (dialog.hDC == 0)
        {
            FreeDialogBlocks(dialog.hDevMode, dialog.hDevNames);
            result = PrintResult.Failed(0, "The Windows print dialog returned no printer device context.");
            return false;
        }

        device = new PrintDevice(
            dialog.hDC,
            dialog.hDevMode,
            dialog.hDevNames,
            ReadDeviceName(dialog.hDevNames));
        result = PrintResult.Printed();
        return true;
    }

    /// <summary>The printer name from a DEVNAMES block. The offsets count characters, not bytes.</summary>
    internal static string ReadDeviceName(nint devNames)
    {
        if (devNames == 0)
        {
            return string.Empty;
        }

        nint memory = PrintNativeMethods.GlobalLock(devNames);
        if (memory == 0)
        {
            return string.Empty;
        }

        try
        {
            int deviceOffset = Marshal.ReadInt16(memory, 2);
            return Marshal.PtrToStringUni(memory + (deviceOffset * sizeof(char))) ?? string.Empty;
        }
        finally
        {
            _ = PrintNativeMethods.GlobalUnlock(devNames);
        }
    }

    /// <summary>
    /// Draws the report for startKey through an existing device context. The report is streamed: the export writes
    /// each line into a page writer, which draws it and starts a new page when the next line would not fit. Every GDI
    /// call is checked. A failure after StartDoc aborts the job, so a partial document is not left in the spooler.
    /// </summary>
    public static PrintResult PrintToDevice(
        PrintDevice device,
        RegistryReader reader,
        RegistryPath? startKey,
        CancellationToken cancellationToken = default,
        Func<RegistryPath, string>? keyDisplay = null) =>
        PrintJob(device, reader, startKey, cancellationToken, outputFile: null, keyDisplay);

    /// <summary>
    /// The one print job. A null start key prints the whole registry (Computer). <paramref name="outputFile"/> is
    /// the DOCINFO output file and is set only by the E2E seam; ship builds always pass null. In E2E builds the
    /// guard runs first, so a refused job never reaches CreateDC use, font creation, or StartDoc.
    /// </summary>
    internal static PrintResult PrintJob(
        PrintDevice device,
        RegistryReader reader,
        RegistryPath? startKey,
        CancellationToken cancellationToken,
        string? outputFile,
        Func<RegistryPath, string>? keyDisplay)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(reader);

#if BWC_E2E
        if (!PrintE2EGuard.Allows(device.DeviceName, outputFile, out string refusal))
        {
            return PrintE2EGuard.Refuse(refusal);
        }
#endif

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
            return PrintReport(deviceContext, reader, startKey, dpiX, dpiY, cancellationToken, outputFile, keyDisplay);
        }
        finally
        {
            _ = PrintNativeMethods.SelectObject(deviceContext, previousFont);
            _ = PrintNativeMethods.DeleteObject(font);
        }
    }

    private static PrintResult PrintReport(
        nint deviceContext,
        RegistryReader reader,
        RegistryPath? startKey,
        int dpiX,
        int dpiY,
        CancellationToken cancellationToken,
        string? outputFile,
        Func<RegistryPath, string>? keyDisplay)
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

        // A start key that cannot be read ends here, before any job exists. Null is the whole registry.
        if (startKey is not null)
        {
            try
            {
                _ = reader.GetKeyInfo(startKey);
            }
            catch (RegistryOperationException ex)
            {
                return PrintResult.Failed(ex.Win32Error, ex.Message);
            }
        }

        // The abort proc is installed before StartDoc, as the spooler requires. Cancel makes it return FALSE,
        // which stops the job; the walk stops at its next line through the token.
        AbortProcDelegate abortProc = (_, _) => cancellationToken.IsCancellationRequested ? 0 : 1;
        if (PrintNativeMethods.SetAbortProc(deviceContext, abortProc) <= 0)
        {
            return FailedAt("SetAbortProc", Marshal.GetLastWin32Error());
        }

        nint outputPointer = string.IsNullOrEmpty(outputFile) ? 0 : Marshal.StringToHGlobalUni(outputFile);
        try
        {
            return PrintDocument(
                deviceContext,
                reader,
                startKey,
                new PageGeometry(left, top, bottom, lineHeight, columns),
                cancellationToken,
                outputPointer,
                abortProc,
                keyDisplay);
        }
        finally
        {
            if (outputPointer != 0)
            {
                Marshal.FreeHGlobal(outputPointer);
            }
        }
    }

    private readonly record struct PageGeometry(int Left, int Top, int Bottom, int LineHeight, int Columns);

    private static PrintResult PrintDocument(
        nint deviceContext,
        RegistryReader reader,
        RegistryPath? startKey,
        PageGeometry geometry,
        CancellationToken cancellationToken,
        nint outputPointer,
        AbortProcDelegate abortProc,
        Func<RegistryPath, string>? keyDisplay)
    {
        var document = new DOCINFOW
        {
            structSize = Marshal.SizeOf<DOCINFOW>(),
            documentName = "Registry Editor - " + (startKey?.ToDisplayString() ?? "Computer"),
            output = outputPointer,
        };

        // Maps a failed GDI step to a cancel when the user asked for it, because cancel makes GDI fail.
        PrintResult Failure(string stage, int win32Error) =>
            cancellationToken.IsCancellationRequested ? PrintResult.Cancelled() : FailedAt(stage, win32Error);

        if (cancellationToken.IsCancellationRequested)
        {
            return PrintResult.Cancelled();
        }

        if (PrintNativeMethods.StartDocW(deviceContext, ref document) <= 0)
        {
            return Failure("StartDoc", Marshal.GetLastWin32Error());
        }

        bool documentStarted = true;
        try
        {
            if (PrintNativeMethods.StartPage(deviceContext) <= 0)
            {
                return Failure("StartPage", Marshal.GetLastWin32Error());
            }

            var page = new ReportPageWriter(
                deviceContext,
                geometry.Left,
                geometry.Top,
                geometry.Bottom,
                geometry.LineHeight,
                geometry.Columns);
            WriteReport(reader, startKey, page, cancellationToken, null, keyDisplay);
            page.Finish();

            if (PrintNativeMethods.EndPage(deviceContext) <= 0)
            {
                return Failure("EndPage", Marshal.GetLastWin32Error());
            }

            if (PrintNativeMethods.EndDoc(deviceContext) <= 0)
            {
                return Failure("EndDoc", Marshal.GetLastWin32Error());
            }

            documentStarted = false;
            return PrintResult.Printed();
        }
        catch (OperationCanceledException)
        {
            return PrintResult.Cancelled();
        }
        catch (RegistryOperationException ex)
        {
            return cancellationToken.IsCancellationRequested
                ? PrintResult.Cancelled()
                : PrintResult.Failed(ex.Win32Error, ex.Message);
        }
        catch (PrintStepException ex)
        {
            return cancellationToken.IsCancellationRequested ? PrintResult.Cancelled() : ex.Result;
        }
        finally
        {
            if (documentStarted && PrintNativeMethods.AbortDoc(deviceContext) <= 0)
            {
                Log.Warn(
                    "Registry Editor could not abort the failed print job, Win32 error " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ".");
            }

            GC.KeepAlive(abortProc);
        }
    }

    // Draws the report one line at a time. A line is drawn when its line break arrives, so only the current line is
    // held in memory. A GDI failure throws PrintStepException, which stops the registry walk and aborts the job.
    private sealed class ReportPageWriter : TextWriter
    {
        private readonly nint _deviceContext;
        private readonly int _left;
        private readonly int _top;
        private readonly int _bottom;
        private readonly int _lineHeight;
        private readonly int _columns;
        private readonly StringBuilder _line = new();
        private int _y;

        public ReportPageWriter(nint deviceContext, int left, int top, int bottom, int lineHeight, int columns)
        {
            _deviceContext = deviceContext;
            _left = left;
            _top = top;
            _bottom = bottom;
            _lineHeight = lineHeight;
            _columns = columns;
            _y = top;
        }

        public override Encoding Encoding => Encoding.Unicode;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                EmitLine();
                return;
            }

            _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            int start = 0;
            for (int index = 0; index < value.Length; index += 1)
            {
                if (value[index] == '\n')
                {
                    _line.Append(value, start, index - start);
                    EmitLine();
                    start = index + 1;
                }
            }

            _line.Append(value, start, value.Length - start);
        }

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        // Draws a last line that has no line break.
        public void Finish()
        {
            if (_line.Length > 0)
            {
                EmitLine();
            }
        }

        private void EmitLine()
        {
#if BWC_E2E
            // The E2E cancel test needs a job long enough to cancel. The delay is set only by the test.
            PrintE2EGuard.PaceLine();
#endif
            string line = _line.ToString();
            _line.Clear();
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (line.Length == 0)
            {
                EnsureRoom();
                _y += _lineHeight;
                return;
            }

            int offset = 0;
            while (offset < line.Length)
            {
                EnsureRoom();
                int chunkLength = Math.Min(_columns, line.Length - offset);
                if (!PrintNativeMethods.TextOutW(_deviceContext, _left, _y, line.Substring(offset, chunkLength), chunkLength))
                {
                    throw new PrintStepException(FailedAt("TextOut", Marshal.GetLastWin32Error()));
                }

                _y += _lineHeight;
                offset += chunkLength;
            }
        }

        private void EnsureRoom()
        {
            if (_y + _lineHeight > _bottom)
            {
                StartNextPage();
            }
        }

        private void StartNextPage()
        {
            if (PrintNativeMethods.EndPage(_deviceContext) <= 0)
            {
                throw new PrintStepException(FailedAt("EndPage", Marshal.GetLastWin32Error()));
            }

            if (PrintNativeMethods.StartPage(_deviceContext) <= 0)
            {
                throw new PrintStepException(FailedAt("StartPage", Marshal.GetLastWin32Error()));
            }

            _y = _top;
        }
    }

    // Carries a failed GDI step out of the registry walk. PrintReport turns it into the result.
    private sealed class PrintStepException(PrintResult result) : Exception(result.Message)
    {
        public PrintResult Result { get; } = result;
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

// commdlg.h PRINTDLGEXW, in header order. It has no template handle: only hInstance and lpPrintTemplateName.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PRINTDLGEXW
{
    public uint lStructSize;
    public nint hwndOwner;
    public nint hDevMode;
    public nint hDevNames;
    public nint hDC;
    public uint Flags;
    public uint Flags2;
    public uint ExclusionFlags;
    public uint nPageRanges;
    public uint nMaxPageRanges;
    public nint lpPageRanges;
    public uint nMinPage;
    public uint nMaxPage;
    public uint nCopies;
    public nint hInstance;
    public nint lpPrintTemplateName;
    public nint lpCallback;
    public uint nPropertyPages;
    public nint lphPropertyPages;
    public uint nStartPage;
    public uint dwResultAction;
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

/// <summary>The spooler's abort proc (wingdi.h AbortProc). Returns nonzero to continue and zero to abort the job.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int AbortProcDelegate(nint deviceContext, int code);

#pragma warning disable SYSLIB1054 // The small, fixed print surface does not justify LibraryImport marshalling plumbing.
internal static class PrintNativeMethods
{
    internal const nint HgdiError = -1;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW", ExactSpelling = true, SetLastError = true)]
    internal static extern nint CreateDCW(string driver, string device, string? port, nint devMode);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int SetAbortProc(nint deviceContext, AbortProcDelegate abortProc);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(nint memory);

    // Returns an HRESULT: S_OK, S_FALSE for cancel, or a failure code.
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrintDlgExW", ExactSpelling = true)]
    internal static extern int PrintDlgExW(ref PRINTDLGEXW dialog);

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
