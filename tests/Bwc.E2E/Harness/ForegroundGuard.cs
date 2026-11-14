using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bwc.E2E.Harness;

/// <summary>
/// The one routine that makes a window the foreground window before synthetic input is sent to it.
/// The decision uses only the owner process of the foreground window, never a topmost flag:
/// <list type="bullet">
/// <item>The target is foreground: nothing is recorded and nothing is sent.</item>
/// <item>Another window of the app is foreground: no re-assert. This is a product focus bug, so a failed
/// check is recorded and the caller does not retry.</item>
/// <item>Another process is foreground (a chat app, for example): the note <c>harness.foreign-foreground</c>
/// names that process, the target is re-asserted through <see cref="Win32.ForceForeground"/>, and a failed
/// check is recorded only if the target still is not foreground.</item>
/// </list>
/// Every failure is recorded here, so a caller that ignores the return value cannot hide it.
/// </summary>
internal static class ForegroundGuard
{
    internal const string ForeignNoteId = "harness.foreign-foreground";

    /// <summary>Notes the first input site taken for the target (see <see cref="IsOwnInputSite"/>).</summary>
    internal const string InputSiteNoteId = "harness.input-site";

    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    private static bool _inputSiteNoted;

    private static int _foreignEvents;

    /// <summary>Foreign-foreground events seen so far. Retry helpers compare it before and after an attempt.</summary>
    internal static int ForeignEvents => Volatile.Read(ref _foreignEvents);

    /// <summary>
    /// Makes <paramref name="target"/> the foreground window for input. Returns true when it is foreground when
    /// the call returns. A false result has already been recorded as a failed <paramref name="checkId"/> check.
    /// </summary>
    internal static bool Ensure(IntPtr target, string checkId, TimeSpan? timeout = null)
    {
        if (target == IntPtr.Zero || !Win32.IsWindow(target))
        {
            E2EResults.Record(checkId, false, "The input target window does not exist.");
            return false;
        }

        if (Win32.IsForeground(target))
        {
            return true;
        }

        IntPtr foreground = Win32.GetForegroundWindow();
        if (IsOwnInputSite(foreground, target))
        {
            return true;
        }

        // A child control of the target (a check box in a system dialog, for example) can be reported as the
        // foreground window. Its root window is the target, so input still goes to the target.
        if (foreground != IntPtr.Zero && GetAncestor(foreground, GaRoot) == target)
        {
            return true;
        }

        uint foregroundPid = foreground == IntPtr.Zero ? 0 : Win32.ProcessIdOf(foreground);
        uint targetPid = Win32.ProcessIdOf(target);
        if (foregroundPid != 0 && foregroundPid == targetPid)
        {
            E2EResults.Record(
                checkId,
                false,
                $"The app's own window 0x{foreground.ToInt64():X} (class '{Win32.ClassNameOf(foreground)}', " +
                $"title '{Win32.TitleOf(foreground)}', {DescribeSize(foreground)}) had the foreground, " +
                $"not the input target 0x{target.ToInt64():X} (class '{Win32.ClassNameOf(target)}', " +
                $"title '{Win32.TitleOf(target)}'). No re-assert was made.");
            return false;
        }

        string foreignName = ProcessNameOf(foregroundPid);
        _ = Interlocked.Increment(ref _foreignEvents);
        E2EResults.Note(
            ForeignNoteId,
            $"{checkId}: the foreground belonged to '{foreignName}' (pid {foregroundPid}), not the app (pid {targetPid}). " +
            $"Re-asserting target 0x{target.ToInt64():X}.");

        bool activated = Win32.ForceForeground(target, timeout ?? TimeSpan.FromSeconds(3));
        if (!activated)
        {
            IntPtr now = Win32.GetForegroundWindow();
            uint nowPid = now == IntPtr.Zero ? 0 : Win32.ProcessIdOf(now);
            E2EResults.Record(
                checkId,
                false,
                $"Target 0x{target.ToInt64():X} is not foreground after the re-assert. The foreground is now " +
                $"'{ProcessNameOf(nowPid)}' (pid {nowPid}).");
        }

        return activated;
    }

    /// <summary>
    /// True when <paramref name="foreground"/> is an XAML input site (InputSiteWindowClass) that belongs to the
    /// target: its owner, root owner, or root is the target. WinUI routes keyboard input from that window to the
    /// target's XAML island, so it is the target and not a focus loss. The first one seen is noted with its
    /// owner, root owner, root, and parent. A site owned by another window of the app is not the target.
    /// </summary>
    internal static bool IsOwnInputSite(IntPtr foreground, IntPtr target)
    {
        if (foreground == IntPtr.Zero || target == IntPtr.Zero || Win32.ClassNameOf(foreground) != "InputSiteWindowClass")
        {
            return false;
        }

        IntPtr owner = GetWindow(foreground, GwOwner);
        IntPtr rootOwner = GetAncestor(foreground, GaRootOwner);
        IntPtr root = GetAncestor(foreground, GaRoot);
        IntPtr parent = GetParent(foreground);
        bool own = owner == target || rootOwner == target || root == target;
        if (!_inputSiteNoted)
        {
            _inputSiteNoted = true;
            E2EResults.Note(
                InputSiteNoteId,
                $"foreground 0x{foreground.ToInt64():X} is an InputSiteWindowClass (owner 0x{owner.ToInt64():X}, root owner 0x{rootOwner.ToInt64():X}, " +
                $"root 0x{root.ToInt64():X}, parent 0x{parent.ToInt64():X}); target 0x{target.ToInt64():X}. " +
                (own
                    ? "It is the target's own input site; keyboard input goes to the target's XAML island from it, so it counts as the target."
                    : "It belongs to another window, so it is not the target."));
        }

        return own;
    }

    /// <summary>Who holds the foreground right now. Reads only; never activates a window.</summary>
    internal static ForegroundState Observe(IntPtr target, out string description)
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        uint foregroundPid = foreground == IntPtr.Zero ? 0 : Win32.ProcessIdOf(foreground);
        uint targetPid = Win32.ProcessIdOf(target);
        description = $"foreground 0x{foreground.ToInt64():X} ('{Win32.TitleOf(foreground)}') of '{ProcessNameOf(foregroundPid)}'";
        if (foreground == target && foreground != IntPtr.Zero)
        {
            return ForegroundState.Target;
        }

        if (IsOwnInputSite(foreground, target))
        {
            return ForegroundState.Target;
        }

        return foregroundPid != 0 && foregroundPid == targetPid ? ForegroundState.AppOther : ForegroundState.Foreign;
    }

    /// <summary>
    /// One re-assert of the app window after a capture found it covered by another process. It is a foreign-foreground
    /// event: the note names the covering process, and <see cref="Win32.ForceForeground"/> is the only activation.
    /// </summary>
    internal static void ReassertForCapture(IntPtr target, uint foreignPid, string foreignName, System.Drawing.Rectangle area)
    {
        if (target == IntPtr.Zero || !Win32.IsWindow(target))
        {
            E2EResults.Note(ForeignNoteId, $"capture of {area} covered by '{foreignName}'; no app window to re-assert.");
            return;
        }

        _ = Interlocked.Increment(ref _foreignEvents);
        E2EResults.Note(
            ForeignNoteId,
            $"capture of {area} covered by '{foreignName}' (pid {foreignPid}). Re-asserting target 0x{target.ToInt64():X} once.");
        _ = Win32.ForceForeground(target, TimeSpan.FromSeconds(2));
    }

    private static string DescribeSize(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out Win32.NativeRect rect))
        {
            return "rect unavailable";
        }

        return $"size {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
    }

    /// <summary>Process name for a pid, or a placeholder when the process is gone or the pid is zero.</summary>
    internal static string ProcessNameOf(uint processId)
    {
        if (processId == 0)
        {
            return "none";
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"pid {processId}";
        }
    }
}

/// <summary>The foreground owner relative to one input target.</summary>
internal enum ForegroundState
{
    /// <summary>The target window is the foreground window.</summary>
    Target,

    /// <summary>Another window of the same process is the foreground window.</summary>
    AppOther,

    /// <summary>A window of another process, or no window, is the foreground window.</summary>
    Foreign,
}
