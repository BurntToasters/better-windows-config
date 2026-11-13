using System.Runtime.InteropServices;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Finds the owned progress and result windows of the Registry Editor. A progress window is identified
/// by its ProgressBar automation id (export), or by its native shape (import, DLG-019: a #32770 dialog with
/// the msctls_progress32 child 101). The captions differ by direction: the export progress uses
/// "Registry Editor" and the import progress uses "Import Registry File". A result window is
/// identified by its result text and the "Registry Editor" caption of the classic message box.
/// </summary>
internal static class ImportExportWindows
{
    private const string Caption = "Registry Editor";

    /// <summary>winuser.h style bits: WS_CAPTION (title bar) and WS_SYSMENU (system menu and Close box).</summary>
    internal const long WsCaption = 0x00C00000;
    internal const long WsSysMenu = 0x00080000;
    private const int GwlStyle = -16;

    /// <summary>The window style (GWL_STYLE) of an owned window.</summary>
    internal static long StyleOf(IntPtr hwnd) => GetWindowLongPtr(hwnd, GwlStyle).ToInt64();


    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>The control id of the import progress bar and of the file name line (DLG-019, RT_DIALOG 110).</summary>
    internal const int ImportProgressBarId = 101;
    internal const int ImportFileNameId = 100;
    internal const int ImportFileLabelId = 0xFFFF;
    internal const string NativeDialogClass = "#32770";
    internal const string NativeProgressClass = "msctls_progress32";

    private const uint PbmGetPos = 0x0408;
    private const uint PbmGetRange = 0x0407;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const int DefaultCharset = 1;
    private const int FontWeightNormal = 400;
    private const int TemplateFontPointSize = 8;
    private const string TemplateFontFace = "MS Shell Dlg";
    private const string FallbackFontFace = "Microsoft Sans Serif";
    private const string LetterSample = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// True for the native import progress window: a #32770 dialog whose child 101 is the msctls_progress32 bar.
    /// The WinUI export window does not have this shape.
    /// </summary>
    internal static bool IsNativeImportProgress(IntPtr hwnd)
    {
        if (Win32.ClassNameOf(hwnd) != NativeDialogClass)
        {
            return false;
        }

        IntPtr bar = GetDlgItem(hwnd, ImportProgressBarId);
        return bar != IntPtr.Zero && Win32.ClassNameOf(bar) == NativeProgressClass;
    }

    /// <summary>Reads the text of a control of the native import window, read through its window text.</summary>
    internal static string ReadNativeText(IntPtr progressHwnd, int controlId)
    {
        IntPtr child = GetDlgItem(progressHwnd, controlId);
        return child == IntPtr.Zero ? string.Empty : Win32.TitleOf(child);
    }

    /// <summary>
    /// The native progress bar state, read with PBM_GETRANGE and PBM_GETPOS. The read uses a timed send so a
    /// stuck window cannot hang the test.
    /// </summary>
    internal static BarState ReadNativeProgress(IntPtr progressHwnd)
    {
        IntPtr bar = GetDlgItem(progressHwnd, ImportProgressBarId);
        if (bar == IntPtr.Zero)
        {
            return new BarState(false, 0, 0);
        }

        int low = SendTimed(bar, PbmGetRange, new UIntPtr(1), IntPtr.Zero);
        int high = SendTimed(bar, PbmGetRange, UIntPtr.Zero, IntPtr.Zero);
        int position = SendTimed(bar, PbmGetPos, UIntPtr.Zero, IntPtr.Zero);
        return new BarState(true, position - low, high - low);
    }

    /// <summary>
    /// Compares the client size of the native window with the classic template: 240 x 59 DLU converted with the
    /// base units of the dialog font at the window DPI. Each edge must be within 1 px.
    /// </summary>
    internal static bool ClientSizeMatchesTemplate(IntPtr hwnd, int widthDlu, int heightDlu, out string detail)
    {
        uint dpi = GetDpiForWindow(hwnd);
        (int baseX, int baseY) = DialogBaseUnits(dpi);
        int expectedWidth = MulDiv(widthDlu, baseX, 4);
        int expectedHeight = MulDiv(heightDlu, baseY, 8);

        if (!GetClientRect(hwnd, out NativeRect client))
        {
            detail = "GetClientRect failed.";
            return false;
        }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        detail = $"dpi={dpi}, base units={baseX}x{baseY}, client={width}x{height} px, " +
            $"expected={expectedWidth}x{expectedHeight} px ({widthDlu}x{heightDlu} DLU)";
        return Math.Abs(width - expectedWidth) <= 1 && Math.Abs(height - expectedHeight) <= 1;
    }

    /// <summary>The DWMWA_USE_IMMERSIVE_DARK_MODE value of a window, or -1 when DWM does not report it.</summary>
    internal static int ImmersiveDarkCaption(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, out int value, sizeof(int)) == 0 ? value : -1;

    /// <summary>The screen color at a point inside the client area, read with a one pixel capture of the screen.</summary>
    internal static System.Drawing.Color ClientPixel(IntPtr hwnd, int clientX, int clientY)
    {
        var point = new NativePoint { X = clientX, Y = clientY };
        _ = ClientToScreen(hwnd, ref point);
        using var pixel = new System.Drawing.Bitmap(1, 1);
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(pixel))
        {
            graphics.CopyFromScreen(point.X, point.Y, 0, 0, new System.Drawing.Size(1, 1));
        }

        return pixel.GetPixel(0, 0);
    }

    /// <summary>
    /// The top-level window at the face sample point: the middle of the bottom margin, clear of the rounded corners.
    /// </summary>
    internal static IntPtr TopWindowAtClientCorner(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out NativeRect client))
        {
            return IntPtr.Zero;
        }

        var point = new NativePoint { X = (client.Right - client.Left) / 2, Y = client.Bottom - client.Top - 12 };
        _ = ClientToScreen(hwnd, ref point);
        return GetAncestor(WindowFromPoint(point), GaRoot);
    }

    /// <summary>
    /// A PNG of the window rectangle as it is on the screen right now. Unlike <see cref="CaptureWindow"/>, this is not
    /// settle-judged, because a window whose bar is moving never settles. The file is evidence of what was on screen.
    /// </summary>
    internal static string SaveScreenCopy(IntPtr hwnd, string path)
    {
        if (!GetWindowRect(hwnd, out NativeRect rect))
        {
            return path;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return path;
        }

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    /// <summary>
    /// Six screen colors in the bottom margin of the client area, below the bar and clear of the rounded corners.
    /// Every one of them is the dialog face when the face is painted.
    /// </summary>
    internal static System.Drawing.Color[] ClientFaceSamples(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out NativeRect client))
        {
            return Array.Empty<System.Drawing.Color>();
        }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        int[] xs = [width / 2 - 60, width / 2, width / 2 + 60];
        int[] ys = [height - 10, height - 16];
        var colors = new List<System.Drawing.Color>();
        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                colors.Add(ClientPixel(hwnd, x, y));
            }
        }

        return colors.ToArray();
    }

    /// <summary>The dialog base units (average character width and font height) for the template font at a DPI.</summary>
    private static (int BaseX, int BaseY) DialogBaseUnits(uint dpi)
    {
        uint effectiveDpi = dpi == 0 ? 96u : dpi;
        string face = ResolveTemplateFace();
        int height = -MulDiv(TemplateFontPointSize, (int)effectiveDpi, 72);
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr font = CreateFontW(height, 0, 0, 0, FontWeightNormal, 0, 0, 0, DefaultCharset, 0, 0, 0, 0, face);
        IntPtr previous = SelectObject(dc, font);
        try
        {
            var metrics = new byte[256];
            if (!GetTextMetricsW(dc, metrics))
            {
                throw new InvalidOperationException("GetTextMetricsW failed while measuring the dialog units.");
            }

            int tmHeight = BitConverter.ToInt32(metrics, 0);
            if (!GetTextExtentPoint32W(dc, LetterSample, LetterSample.Length, out GdiSize size))
            {
                throw new InvalidOperationException("GetTextExtentPoint32W failed while measuring the dialog units.");
            }

            return ((size.Cx / 26 + 1) / 2, tmHeight);
        }
        finally
        {
            _ = SelectObject(dc, previous);
            _ = DeleteObject(font);
            _ = DeleteDC(dc);
        }
    }

    /// <summary>The face that "MS Shell Dlg" maps to in FontSubstitutes, as ClassicDialogMetrics resolves it.</summary>
    private static string ResolveTemplateFace()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
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

        return FallbackFontFace;
    }

    private static int SendTimed(IntPtr target, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(target, message, wParam, lParam, SmtoAbortIfHung, 1000, out IntPtr result) == IntPtr.Zero)
        {
            return 0;
        }

        return (int)result;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr hDlg, int id);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private const uint GaRoot = 2;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontW(
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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextMetricsW(IntPtr hdc, [Out] byte[] metrics);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out GdiSize size);

    [DllImport("kernel32.dll")]
    private static extern int MulDiv(int number, int numerator, int denominator);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiSize
    {
        public int Cx;
        public int Cy;
    }

    /// <summary>Waits for the owned progress dialog and returns its HWND, or zero on timeout.</summary>
    internal static IntPtr WaitForProgress(UIA3Automation automation, IntPtr mainHwnd, TimeSpan timeout)
    {
        IntPtr found = IntPtr.Zero;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                found = FindProgress(automation, mainHwnd);
                return found != IntPtr.Zero;
            },
            timeout);
        return found;
    }

    internal static IntPtr FindProgress(UIA3Automation automation, IntPtr mainHwnd)
    {
        foreach ((IntPtr hwnd, string _, string _) in AppServicesSupport.OwnedWindows(mainHwnd))
        {
            if (IsNativeImportProgress(hwnd))
            {
                return hwnd;
            }

            try
            {
                AutomationElement window = automation.FromHandle(hwnd);
                if (window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressBar")) is not null)
                {
                    return hwnd;
                }
            }
            catch (Exception)
            {
                // The window closed while it was being inspected. The next candidate is tried.
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Reads the status line of the progress dialog, or an empty string when it is gone.</summary>
    internal static string ProgressStatus(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? status = window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressStatus"));
            return status?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The progress bar state: whether it was found, its value, and its maximum (sections or keys).</summary>
    internal readonly record struct BarState(bool Present, double Value, double Maximum);

    /// <summary>Reads the progress bar value and maximum through the RangeValue pattern.</summary>
    internal static BarState ReadProgress(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? bar = window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressBar"));
            if (bar is null || !bar.Patterns.RangeValue.IsSupported)
            {
                return new BarState(false, 0, 0);
            }

            var range = bar.Patterns.RangeValue.Pattern;
            return new BarState(true, range.Value.Value, range.Maximum.Value);
        }
        catch (Exception)
        {
            return new BarState(false, 0, 0);
        }
    }

    /// <summary>
    /// The names of every button UI Automation reports inside the progress window, or null when the window
    /// cannot be read. The title bar Close button is one of them, so callers filter it by name.
    /// </summary>
    internal static IReadOnlyList<string>? ButtonNames(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement[] buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            return buttons.Select(button => button.Name ?? string.Empty).ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the Name of the element with this automation id inside the progress window, or empty.</summary>
    internal static string ReadText(UIA3Automation automation, IntPtr progressHwnd, string automationId)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The caption of an owned window, read from the window text, or empty when it is not owned.</summary>
    internal static string TitleOf(IntPtr mainHwnd, IntPtr hwnd)
    {
        foreach ((IntPtr candidate, string _, string title) in AppServicesSupport.OwnedWindows(mainHwnd))
        {
            if (candidate == hwnd)
            {
                return title;
            }
        }

        return string.Empty;
    }

    /// <summary>Clicks the Cancel button of the progress dialog with a real mouse click.</summary>
    internal static bool ClickCancel(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? cancel = window.FindFirstDescendant(cf => cf.ByAutomationId("DialogSecondary"));
            if (cancel is null)
            {
                return false;
            }

            if (!ForegroundGuard.Ensure(progressHwnd, "dialog.progress.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            var rect = cancel.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            AppServicesSupport.Click(
                (int)(rect.Left + (rect.Width / 2)),
                (int)(rect.Top + (rect.Height / 2)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes a screenshot of the whole dialog window, including its frame, as a PNG.</summary>
    internal static string CaptureWindow(IntPtr hwnd, string path)
    {
        // The picture is a verified, settled screen copy (WindowCapture), so nothing is raised before the capture.
        return WindowCapture.SaveArtifact("dialog.artifact." + Path.GetFileNameWithoutExtension(path), hwnd, path);
    }

    /// <summary>
    /// Waits for an owned result message whose text matches. The progress dialog is skipped even
    /// though it has the same title.
    /// </summary>
    internal static IntPtr WaitForResult(
        IntPtr mainHwnd,
        Func<string, bool> matches,
        TimeSpan timeout,
        params IntPtr[] exclude)
    {
        IntPtr found = IntPtr.Zero;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                foreach ((IntPtr hwnd, string _, string title) in AppServicesSupport.OwnedWindows(mainHwnd))
                {
                    if (title != Caption || exclude.Contains(hwnd))
                    {
                        continue;
                    }

                    string text = AppServicesSupport.DialogText(hwnd);
                    if (matches(text))
                    {
                        found = hwnd;
                        return true;
                    }
                }

                return false;
            },
            timeout);
        return found;
    }
}
