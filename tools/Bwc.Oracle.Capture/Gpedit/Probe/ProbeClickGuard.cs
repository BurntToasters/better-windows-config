using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// Every coordinate click goes through here. A click is refused unless the point is inside the native window of the intended control (or its
// nearest window ancestor) and the window under that point is that window or one of its children. This is the last fallback; patterns
// and keys are used first, and a refused click is recorded as the step's failure instead of landing somewhere else.
internal static class ProbeClickGuard
{
    // The mmc.exe process the probe started. A control of any other process is never clicked.
    internal static int TargetPid { get; set; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    // Returns the screen point to click, or throws InvalidOperationException saying why the click is refused.
    internal static NativePoint Check(AutomationElement element)
    {
        if (!GpeditDpi.IsPerMonitorV2)
        {
            throw new InvalidOperationException("click refused: this thread is not per-monitor v2 DPI aware, so coordinates are virtualized.");
        }

        string name = ProbeUia.NameOf(element);
        if (element.Current.ProcessId != TargetPid)
        {
            throw new InvalidOperationException($"click refused: '{name}' does not belong to the mmc.exe the probe started.");
        }

        System.Windows.Rect rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y) || rect.Width < 1 || rect.Height < 1)
        {
            throw new InvalidOperationException($"click refused: '{name}' has no usable bounding rectangle.");
        }

        var point = new NativePoint { X = (int)(rect.X + (rect.Width / 2)), Y = (int)(rect.Y + (rect.Height / 2)) };
        IntPtr hwnd = NearestWindow(element);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"click refused: '{name}' has no native window or ancestor window to check against.");
        }

        if (!Win32.GetWindowRect(hwnd, out NativeRect window)
            || point.X < window.Left || point.X >= window.Right || point.Y < window.Top || point.Y >= window.Bottom)
        {
            throw new InvalidOperationException(
                $"click refused: point ({point.X},{point.Y}) for '{name}' is outside its window 0x{hwnd:X} [{window.Left},{window.Top},{window.Right},{window.Bottom}].");
        }

        IntPtr under = WindowFromPoint(point);
        if (under != hwnd && !IsChild(hwnd, under))
        {
            throw new InvalidOperationException(
                $"click refused: the window under ({point.X},{point.Y}) is 0x{under:X} '{ProbeWindows.ClassOf(under)}', not 0x{hwnd:X} '{ProbeWindows.ClassOf(hwnd)}' or a child of it.");
        }

        return point;
    }

    private static IntPtr NearestWindow(AutomationElement element)
    {
        AutomationElement? current = element;
        for (int i = 0; i < 40 && current is not null; i++)
        {
            int handle = current.Current.NativeWindowHandle;
            if (handle != 0)
            {
                return new IntPtr(handle);
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return IntPtr.Zero;
    }
}
