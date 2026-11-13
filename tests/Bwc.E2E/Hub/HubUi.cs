using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Bwc.E2E.Hub;

/// <summary>
/// UI helpers for the Hub tests. Popups and dialogs are separate top-level windows, some owned by the
/// Hub's main window, so they are found by enumerating the visible windows of the Hub's process id.
/// Captures are screen copies of each window's visible frame, saved under artifacts/e2e/visual/hub.
/// </summary>
internal static class HubUi
{
    public static string VisualDir(E2EConfig config) => Path.Combine(config.ArtifactsDir, "visual", "hub");

    /// <summary>Finds an element by automation id in a top-level window or popup of the process.</summary>
    public static AutomationElement? FindInProcess(UIA3Automation automation, int processId, string automationId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            foreach (AutomationElement window in ProcessWindows(automation, processId))
            {
                if (!IsProcess(window, processId))
                {
                    continue;
                }

                try
                {
                    AutomationElement? found = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                    if (found is not null)
                    {
                        return found;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    // The window closed while it was being searched.
                }
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>Finds a top-level window of the process by its title.</summary>
    public static AutomationElement? FindWindowInProcess(UIA3Automation automation, int processId, string title, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            foreach (AutomationElement window in ProcessWindows(automation, processId))
            {
                if (IsProcess(window, processId) && string.Equals(SafeName(window), title, StringComparison.Ordinal))
                {
                    return window;
                }
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>
    /// Finds a top-level window of the process that has the title and holds an element with the automation
    /// id. Two windows can share a title: an interactive check shows the progress window "Check for Updates"
    /// first, and the result message box uses the same title, so a title-only lookup can return the progress.
    /// </summary>
    public static AutomationElement? FindWindowWithChild(
        UIA3Automation automation,
        int processId,
        string title,
        string automationId,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            foreach (AutomationElement window in ProcessWindows(automation, processId))
            {
                if (!string.Equals(SafeName(window), title, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null)
                    {
                        return window;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    // The window is still loading or has closed.
                }
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>True when no top-level window of the process has the title.</summary>
    public static bool WaitForWindowGone(UIA3Automation automation, int processId, string title, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            bool present = false;
            foreach (AutomationElement window in ProcessWindows(automation, processId))
            {
                if (IsProcess(window, processId) && string.Equals(SafeName(window), title, StringComparison.Ordinal))
                {
                    present = true;
                    break;
                }
            }

            if (!present)
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    public static string TextOf(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Makes a Hub window the foreground window for input, through <see cref="ForegroundGuard"/>.</summary>
    public static bool Foreground(IntPtr hwnd, TimeSpan timeout) => ForegroundGuard.Ensure(hwnd, "gui.foreground.hub", timeout);

    /// <summary>
    /// Makes a Hub dialog the foreground window before a click or a read. Clicks aimed at the dialog are sent only
    /// after this returns true, so they cannot land on the Hub or on a window that covers it. Failure is a recorded check.
    /// </summary>
    public static bool RaiseDialog(IntPtr hwnd, TimeSpan timeout) => ForegroundGuard.Ensure(hwnd, "dialog.hub.foreground", timeout);

    /// <summary>Brings a Hub window to the foreground through the guard. A foreign foreground is re-asserted.</summary>
    public static bool ForceForeground(IntPtr hwnd, TimeSpan timeout) => ForegroundGuard.Ensure(hwnd, "gui.foreground.hub", timeout);

    /// <summary>
    /// Names the foreground window, its process, and whether a Registry Editor process is still running. The text goes
    /// into the run record, so a failed About open shows which window held the focus.
    /// </summary>
    public static string DescribeForeground(IntPtr hub)
    {
        IntPtr foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint processId);
        string name;
        try
        {
            name = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "(unknown)";
        }

        int regedits = System.Diagnostics.Process.GetProcessesByName("BwcRegedit").Length;
        return $"foreground=0x{foreground.ToInt64():X} pid={processId} process={name} hub=0x{hub.ToInt64():X} " +
            $"hubIsForeground={foreground == hub} bwcRegeditRunning={regedits}";
    }

    /// <summary>
    /// Opens the Help menu by clicking its menu bar item. The mouse path does not depend on the
    /// keyboard state of the menu router, which a previous Alt+H and Escape can leave behind. The
    /// window is forced to the foreground before each attempt, because a click that only activates
    /// an inactive window is consumed and leaves the menu closed. The click is retried until the
    /// Check for Updates item is present, so a menu that opened and closed is opened again.
    /// </summary>
    public static void OpenHelpMenuByMouse(AppSession hub, UIA3Automation automation)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            _ = ForceForeground(hub.MainHwnd, TimeSpan.FromSeconds(2));
            AutomationElement? help = FindInProcess(automation, hub.ProcessId, "MenuCommand_Help", TimeSpan.FromSeconds(3));
            if (help is null)
            {
                continue;
            }

            WaitForSettledBounds(help, TimeSpan.FromSeconds(1));
            help.Click();
            Thread.Sleep(250);
            if (FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", TimeSpan.FromSeconds(2)) is not null)
            {
                return;
            }
        }
        while (DateTime.UtcNow < deadline);
    }

    /// <summary>
    /// The visible top-level windows of one process, read by window handle. The UIA desktop children
    /// do not reliably include a window that is owned by another window, and the Hub's dialogs are
    /// owned by the Hub's main window, so the desktop list missed them. An error on one window skips it.
    /// </summary>
    private static AutomationElement[] ProcessWindows(UIA3Automation automation, int processId)
    {
        var handles = new List<IntPtr>();
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (IsWindowVisible(hwnd) && GetWindowThreadProcessId(hwnd, out uint owner) != 0 && owner == unchecked((uint)processId))
                {
                    handles.Add(hwnd);
                }

                return true;
            },
            IntPtr.Zero);

        var windows = new List<AutomationElement>(handles.Count);
        foreach (IntPtr hwnd in handles)
        {
            try
            {
                windows.Add(automation.FromHandle(hwnd));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The window closed between the enumeration and the lookup.
            }
        }

        return windows.ToArray();
    }

    /// <summary>Presses Alt+H, which opens the Help menu through the menu router.</summary>
    public static void OpenHelpMenu(IntPtr mainHwnd)
    {
        if (!Foreground(mainHwnd, TimeSpan.FromSeconds(3)))
        {
            throw new InvalidOperationException("The Hub did not come to the foreground for Alt+H.");
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_H);
        Thread.Sleep(300);
    }

    public static void InvokeButton(AutomationElement? element)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            element.Click();
        }
    }

    /// <summary>
    /// Clicks a menu item with the mouse. A flyout item does not reliably run its click handler
    /// through the Invoke pattern, so the mouse is the activation path for menus.
    /// </summary>
    public static void ClickMenuItem(AutomationElement? element)
    {
        if (element is null)
        {
            return;
        }

        // The pointer must rest on the item before the press: a press that arrives with the first move
        // was dropped on some runs, and the flyout stayed open with nothing run. The click is repeated while
        // the item is still showing, which means the flyout did not close and the command did not run.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 && !IsShowing(element))
            {
                return;
            }

            WaitForSettledBounds(element, TimeSpan.FromSeconds(2));
            try
            {
                Rectangle bounds = element.BoundingRectangle;
                Mouse.MoveTo(new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2)));
                Thread.Sleep(150);
                element.Click();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The item left the tree while it was clicked, which is what a successful click does.
                return;
            }

            Thread.Sleep(400);
        }
    }

    /// <summary>
    /// Presses a dialog button with the mouse, the way a user does. The dialog is raised first, so the press lands on
    /// the button and not on a window that covers it. The pointer rests on the button's centre before the press.
    /// Returns false, and presses nothing, when the dialog could not be raised.
    /// </summary>
    public static bool ClickDialogButton(AutomationElement button, AutomationElement dialog)
    {
        var hwnd = (IntPtr)dialog.Properties.NativeWindowHandle.ValueOrDefault;
        if (!RaiseDialog(hwnd, TimeSpan.FromSeconds(3)))
        {
            return false;
        }

        WaitForSettledBounds(button, TimeSpan.FromSeconds(2));
        Rectangle bounds = button.BoundingRectangle;
        Mouse.MoveTo(new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2)));
        Thread.Sleep(150);
        button.Click();
        return true;
    }

    /// <summary>True while an element is still on screen. An element that cannot be read has left the tree.</summary>
    private static bool IsShowing(AutomationElement element)
    {
        try
        {
            return !element.Properties.IsOffscreen.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until an element's rectangle stops changing. A flyout animates into place after it opens, and a
    /// click sent to the item's first position misses it: the Help menu then stayed open and nothing ran.
    /// </summary>
    private static void WaitForSettledBounds(AutomationElement element, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Rectangle? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Rectangle current = element.BoundingRectangle;
                if (current.Width > 0 && current == last)
                {
                    return;
                }

                last = current;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                last = null;
            }

            Thread.Sleep(150);
        }
    }

    /// <summary>Waits for a descendant of a window to load, because dialog content appears after the window.</summary>
    public static AutomationElement? WaitWithin(AutomationElement? window, string automationId, TimeSpan timeout)
    {
        if (window is null)
        {
            return null;
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                AutomationElement? found = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The window is still loading or has closed.
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>
    /// Saves a capture of a window's visible frame. The bounds are the DWM extended frame bounds, which
    /// leave out the invisible resize border that GetWindowRect includes. The pixels are a verified, settled
    /// screen copy (WindowCapture), so a window above it makes the capture not judged, and nothing is raised for the
    /// capture. Returns the path written.
    /// </summary>
    public static string CaptureWindow(IntPtr hwnd, string path)
    {
        Win32.NativeRect rect = VisibleFrame(hwnd);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"The captured window has no size ({width}x{height}).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using Bitmap bitmap = WindowCapture.CaptureRegion(hwnd, new Point(rect.Left, rect.Top), width, height);
            bitmap.Save(path, ImageFormat.Png);
        }
        catch (WindowCapture.NotJudgedException ex)
        {
            // An artifact, not a check: a covered window is a note and no file is claimed.
            E2EResults.Note("hub.capture.notjudged", $"{Path.GetFileName(path)}: {ex.Message}");
            return string.Empty;
        }

        return path;
    }

    /// <summary>The visible frame of a window in screen pixels, falling back to GetWindowRect when DWM has none.</summary>
    private static Win32.NativeRect VisibleFrame(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Win32.NativeRect frame, Marshal.SizeOf<Win32.NativeRect>()) == 0)
        {
            return frame;
        }

        if (!Win32.GetWindowRect(hwnd, out Win32.NativeRect rect))
        {
            throw new InvalidOperationException("GetWindowRect failed for the captured window.");
        }

        return rect;
    }

    /// <summary>The average brightness (0 to 255) of a sample of pixels inside a capture.</summary>
    public static double AverageBrightness(string pngPath, int sampleStep = 8)
    {
        using var bitmap = new Bitmap(pngPath);
        long total = 0;
        int count = 0;
        for (int y = bitmap.Height / 4; y < bitmap.Height * 3 / 4; y += sampleStep)
        {
            for (int x = bitmap.Width / 8; x < bitmap.Width * 7 / 8; x += sampleStep)
            {
                Color pixel = bitmap.GetPixel(x, y);
                total += (pixel.R + pixel.G + pixel.B) / 3;
                count++;
            }
        }

        return count == 0 ? 0 : (double)total / count;
    }

    public static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static bool IsProcess(AutomationElement element, int processId)
    {
        try
        {
            return element.Properties.ProcessId.ValueOrDefault == processId;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

#pragma warning disable SYSLIB1054
    private const uint DwmwaExtendedFrameBounds = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out Win32.NativeRect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

#pragma warning restore SYSLIB1054
}
