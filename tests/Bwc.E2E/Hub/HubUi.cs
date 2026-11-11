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
/// UI helpers for the Hub tests. Popups and dialogs are separate top-level windows, so they are
/// found from the desktop and filtered to the Hub's process id. Captures are PrintWindow bitmaps
/// saved under artifacts/e2e/visual/hub.
/// </summary>
internal static class HubUi
{
    private const uint PwRenderFullContent = 0x00000002;

    public static string VisualDir(E2EConfig config) => Path.Combine(config.ArtifactsDir, "visual", "hub");

    /// <summary>Finds an element by automation id in a top-level window or popup of the process.</summary>
    public static AutomationElement? FindInProcess(UIA3Automation automation, int processId, string automationId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            foreach (AutomationElement window in DesktopChildren(automation))
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
            foreach (AutomationElement window in DesktopChildren(automation))
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

    /// <summary>True when no top-level window of the process has the title.</summary>
    public static bool WaitForWindowGone(UIA3Automation automation, int processId, string title, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            bool present = false;
            foreach (AutomationElement window in DesktopChildren(automation))
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

    /// <summary>Brings a window to the foreground so keystrokes reach it. Returns false when it cannot.</summary>
    public static bool Foreground(IntPtr hwnd, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Win32.IsForeground(hwnd))
            {
                return true;
            }

            Win32.ShowWindow(hwnd, Win32.SwRestore);
            _ = Win32.SetForegroundWindow(hwnd);
            Thread.Sleep(100);
            if (DateTime.UtcNow >= deadline)
            {
                return Win32.IsForeground(hwnd);
            }
        }
    }

    /// <summary>
    /// Opens the Help menu by clicking its menu bar item. The mouse path does not depend on the
    /// keyboard state of the menu router, which a previous Alt+H and Escape can leave behind.
    /// </summary>
    public static void OpenHelpMenuByMouse(AppSession hub, UIA3Automation automation)
    {
        _ = Foreground(hub.MainHwnd, TimeSpan.FromSeconds(3));
        AutomationElement? help = FindInProcess(automation, hub.ProcessId, "MenuCommand_Help", TimeSpan.FromSeconds(5));
        help?.Click();
        Thread.Sleep(400);
    }

    /// <summary>The top level windows and popups of the desktop. A UIA error on one window yields none.</summary>
    private static AutomationElement[] DesktopChildren(UIA3Automation automation)
    {
        try
        {
            return automation.GetDesktop().FindAllChildren();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return [];
        }
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
        element?.Click();
        Thread.Sleep(200);
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

    /// <summary>Saves a PrintWindow capture of a window. Returns the path written.</summary>
    public static string CaptureWindow(IntPtr hwnd, string path)
    {
        if (!Win32.GetWindowRect(hwnd, out Win32.NativeRect rect))
        {
            throw new InvalidOperationException("GetWindowRect failed for the captured window.");
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"The captured window has no size ({width}x{height}).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PwRenderFullContent))
                {
                    throw new InvalidOperationException("PrintWindow failed for the captured window.");
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
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
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
#pragma warning restore SYSLIB1054
}
