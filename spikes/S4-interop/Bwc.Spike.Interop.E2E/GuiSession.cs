using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace Bwc.Spike.Interop.E2E;

/// <summary>
/// One launched copy of the spike app plus the shared GUI gate. Holds Local\BwcGuiTestLock for its lifetime,
/// kills only the process it started, and drives the window through UIA3 and Win32 owner/enabled queries.
/// </summary>
internal sealed class GuiSession : IDisposable
{
    public const string MainTitle = "BWC Interop Spike S4";
    private const string GateName = @"Local\BwcGuiTestLock";

    private readonly Mutex _gate;
    private readonly Process _process;

    private GuiSession(Mutex gate, Process process, UIA3Automation automation, nint mainHwnd)
    {
        _gate = gate;
        _process = process;
        Automation = automation;
        MainHwnd = mainHwnd;
        Main = automation.FromHandle(mainHwnd);
    }

    public UIA3Automation Automation { get; }
    public nint MainHwnd { get; }
    public AutomationElement Main { get; }
    public int ProcessId => _process.Id;

    /// <summary>Diagnostic: whether the app process is still running, and its exit code once it has exited.</summary>
    public string ProcessState()
    {
        try
        {
            return _process.HasExited ? "exited code=0x" + _process.ExitCode.ToString("X8") : "running";
        }
        catch (Exception ex)
        {
            return "unknown: " + ex.Message;
        }
    }

    public static string AppExePath => Path.Combine(AppContext.BaseDirectory, "app", "Bwc.Spike.Interop.exe");

    public static GuiSession Start()
    {
        var gate = new Mutex(false, GateName);
        AcquireGate(gate);
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(AppExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(AppExePath)!,
            };
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + AppExePath);
            var mainHwnd = nint.Zero;
            WaitUntil(() =>
            {
                mainHwnd = FindTopLevel(process.Id, title => title == MainTitle);
                return mainHwnd != 0;
            }, TimeSpan.FromSeconds(30));
            if (mainHwnd == 0)
            {
                throw new TimeoutException("Main window did not appear for pid " + process.Id);
            }
            Native.BringToFront(mainHwnd);
            var automation = new UIA3Automation();
            return new GuiSession(gate, process, automation, mainHwnd);
        }
        catch
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            gate.ReleaseMutex();
            gate.Dispose();
            throw;
        }
    }

    private static void AcquireGate(Mutex gate)
    {
        try
        {
            if (!gate.WaitOne(TimeSpan.FromMinutes(15)))
            {
                throw new TimeoutException("Timed out waiting for " + GateName);
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died; ownership has been transferred to us.
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        finally
        {
            Automation.Dispose();
            _gate.ReleaseMutex();
            _gate.Dispose();
        }
    }

    /// <summary>Finds a top-level window of the given process by title and/or class.</summary>
    public static nint FindTopLevel(int pid, Func<string, bool> titleMatches, Func<string, bool>? classMatches = null)
    {
        foreach (var hwnd in Native.TopLevelWindows())
        {
            if (Native.ProcessIdOf(hwnd) != (uint)pid) continue;
            if (!titleMatches(Native.TitleOf(hwnd))) continue;
            if (classMatches is not null && !classMatches(Native.ClassOf(hwnd))) continue;
            return hwnd;
        }
        return 0;
    }

    /// <summary>Top-level windows of this app process, optionally filtered.</summary>
    public nint FindOwnTopLevel(Func<string, bool> titleMatches, Func<string, bool>? classMatches = null)
    {
        return FindTopLevel(ProcessId, titleMatches, classMatches);
    }

    /// <summary>
    /// Finds a descendant by AutomationId. The default root is re-acquired from the window handle on every call:
    /// the element captured at launch can go stale after a Win32 modal (aclui) has run.
    /// </summary>
    public AutomationElement? Find(string automationId, AutomationElement? root = null)
    {
        return (root ?? Automation.FromHandle(MainHwnd)).FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    }

    public string TextOf(string automationId)
    {
        return Find(automationId)?.Name ?? "<missing " + automationId + ">";
    }

    /// <summary>Waits until the element with the given AutomationId shows the expected text.</summary>
    public bool WaitForText(string automationId, Func<string, bool> predicate, TimeSpan timeout)
    {
        return WaitUntil(() => predicate(TextOf(automationId)), timeout);
    }

    /// <summary>Makes the window the foreground window and reports whether that succeeded. Keys must only be sent after this is true.</summary>
    public static bool EnsureForeground(nint hwnd)
    {
        if (Native.GetForegroundWindow() == hwnd) return true;
        return Native.BringToFront(hwnd);
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (condition()) return true;
            }
            catch (Exception)
            {
                // Element not ready yet; keep polling until the deadline.
            }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
    }
}

/// <summary>The HKCU key the spike creates at startup and the test run deletes at the end.</summary>
internal static class SpikeRegistry
{
    public const string KeyPath = @"Software\BWC-Spike-S4";

    public static bool Exists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key is not null;
    }

    public static void DeleteIfPresent()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }
}
