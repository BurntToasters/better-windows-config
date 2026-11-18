using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit;

// Per-monitor v2 DPI awareness for every gpedit command, set before any UI Automation or window call. An unaware process gets virtualized
// rectangles and mouse coordinates at a scale other than 100%, so a coordinate click would land in the wrong place.
internal static class GpeditDpi
{
    private static readonly IntPtr PerMonitorV2 = new(-4);

    // What the awareness is now, for probe.json.
    internal static JsonObject Result { get; private set; } = new() { ["ensured"] = false };

    // True when this thread is per-monitor v2 aware, so rectangles are physical pixels and coordinate input is not virtualized.
    internal static bool IsPerMonitorV2 { get; private set; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    // Process first. If the process awareness was already fixed (the call fails), the thread is set instead; the probe runs on this thread.
    internal static void Ensure()
    {
        bool processSet = SetProcessDpiAwarenessContext(PerMonitorV2);
        int lastError = processSet ? 0 : Marshal.GetLastWin32Error();
        bool aware = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorV2);
        bool threadSet = false;
        if (!aware)
        {
            threadSet = SetThreadDpiAwarenessContext(PerMonitorV2) != IntPtr.Zero;
            aware = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorV2);
        }

        IsPerMonitorV2 = aware;
        Result = new JsonObject
        {
            ["ensured"] = true,
            ["processCallSucceeded"] = processSet,
            ["processCallLastError"] = lastError,
            ["threadCallUsed"] = threadSet,
            ["perMonitorV2"] = aware,
            ["awarenessValue"] = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()),
            ["awarenessMeaning"] = "0 unaware, 1 system, 2 per monitor",
        };
    }
}
