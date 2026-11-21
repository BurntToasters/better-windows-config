using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Bwc.E2E.Harness;

/// <summary>
/// Reads the top-level windows of one process without asking UI Automation for every window of the desktop.
/// </summary>
internal static class ProcessWindows
{
    /// <summary>
    /// The visible top-level windows of one process, read by window handle, in z order from the top.
    /// The UIA desktop children do not reliably include a window that is owned by another window, and the
    /// dialogs and flyouts of the app are owned by its main window, so the desktop list can miss them.
    /// Hidden windows are left out, as the desktop list leaves them out. An error on one window skips it.
    /// </summary>
    internal static AutomationElement[] Of(UIA3Automation automation, int processId) => Of(automation, processId, out _);

    /// <summary>
    /// The same list, with the number of windows that were listed but could not be read. A caller that falls back
    /// to a wider search does so only when this is not zero, because then a window of the process may be missing.
    /// </summary>
    internal static AutomationElement[] Of(UIA3Automation automation, int processId, out int skipped)
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

        skipped = 0;
        var windows = new List<AutomationElement>(handles.Count);
        foreach (IntPtr hwnd in handles)
        {
            try
            {
                windows.Add(automation.FromHandle(hwnd));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException
                or FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
                // The window closed between the enumeration and the lookup, or UIA could not reach it yet.
                skipped++;
            }
        }

        return windows.ToArray();
    }

    /// <summary>
    /// Drops the elements that an owned window shows a second time. A popup window can be reached from its own
    /// handle and again below the window that owns it, so a search over every window of the process finds its
    /// items twice, where the desktop list found them once. Two elements with the same id, class, name and
    /// rectangle are one element. An element that cannot be read is kept.
    /// </summary>
    internal static AutomationElement[] Distinct(IEnumerable<AutomationElement> elements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<AutomationElement>();
        foreach (AutomationElement element in elements)
        {
            string? key = null;
            try
            {
                key = string.Join('|', element.AutomationId, element.ClassName, element.Name, element.BoundingRectangle.ToString());
            }
            catch (Exception)
            {
                // A stale element has no key and goes through to the caller as it is.
            }

            if (key is null || seen.Add(key))
            {
                kept.Add(element);
            }
        }

        return kept.ToArray();
    }

#pragma warning disable SYSLIB1054
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
#pragma warning restore SYSLIB1054
}
