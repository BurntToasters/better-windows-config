using System.Runtime.Versioning;

namespace Bwc.Update;

/// <summary> Starts a program at the integrity level of the desktop shell from an elevated process: the documented "run
/// unelevated from an elevated process" pattern, as typed vtable calls (<see cref="ShellChain"/>). Nothing here falls
/// back to a plain start: any failure returns false and starts nothing (update-elevation.md LH3, HN10).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellLaunch
{
    /// <summary>True when the shell was asked to start the program. The call runs on its own STA thread.</summary>
    public static bool TryStartUnelevated(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var started = false;
        var thread = new Thread(() => started = StartOnThisThread(exePath, arguments, workingDirectory))
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(30)) && started;
    }

    private static bool StartOnThisThread(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // The thread is STA already (set above), so this returns S_FALSE and the matching CoUninitialize is balanced.
        using var apartment = ComApartment.EnterSta();
        if (!apartment.Usable)
        {
            return false;
        }

        int hr = ComNative.CoCreateInstance(ComIds.ShellWindowsClass, 0, ComNative.ClsctxAll, ComIds.ShellWindows, out nint pointer);
        using ComObject? windows = ComObject.Own(pointer);
        return hr == 0 && windows is not null && ShellChain.Run(windows, exePath, arguments, workingDirectory);
    }
}
