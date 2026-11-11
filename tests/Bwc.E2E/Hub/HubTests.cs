using System.ComponentModel;
using System.Diagnostics;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Hub;

/// <summary>
/// Drives the Hub from its published output: checks the launcher surface, starts the Registry
/// Editor through the tool list, verifies the child window by UIA, and closes only the child that
/// this test started.
/// </summary>
[TestClass]
public sealed class HubTests
{
    private const string HubTitle = "Better Windows Config";
    private const string RegeditTitle = "Registry Editor";
    private const string RegeditProcessName = "BwcRegedit";
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_ShowsToolListAndLaunchesRegedit()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
            E2EResults.Note("hub.publish.executables", config.PublishDir);
        }
        catch (FileNotFoundException ex)
        {
            E2EResults.Record("hub.publish.executables", false, ex.Message);
            Assert.Fail(ex.Message);
        }

        using var hub = new AppSession(config, config.HubExePath);
        E2EResults.CurrentSession = hub;

        var title = hub.Window.Title;
        E2EResults.Record(
            "hub.title",
            string.Equals(title, HubTitle, StringComparison.Ordinal),
            $"expected '{HubTitle}', got '{title}'.");

        var root = WaitFor(hub, "HubRoot");
        E2EResults.Record(
            "hub.root",
            root is not null,
            root is null ? "HubRoot was not found." : "HubRoot was found.");

        var version = WaitForText(hub, "HubVersion");
        E2EResults.Record(
            "hub.version",
            !string.IsNullOrWhiteSpace(version),
            $"HubVersion text was '{version ?? "(none)"}'.");

        var toolList = WaitFor(hub, "ToolList");
        E2EResults.Record(
            "hub.toolList",
            toolList is not null,
            toolList is null ? "ToolList was not found." : "ToolList was found.");

        var toolRow = WaitFor(hub, "ToolRow_regedit");
        E2EResults.Record(
            "hub.toolRow",
            toolRow is not null,
            toolRow is null ? "ToolRow_regedit was not found." : "ToolRow_regedit was found.");

        var status = WaitForText(hub, "HubStatus");
        E2EResults.Record(
            "hub.status",
            !string.IsNullOrWhiteSpace(status),
            $"HubStatus text was '{status ?? "(none)"}'.");

        Button? launch = null;
        var launchElement = WaitFor(hub, "Launch_regedit");
        E2EResults.Record(
            "hub.launchRegedit.present",
            launchElement is not null,
            launchElement is null ? "Launch_regedit was not found." : "Launch_regedit was found.");

        if (launchElement is not null)
        {
            try
            {
                launch = launchElement.AsButton();
            }
            catch (Exception ex)
            {
                E2EResults.Record(
                    "hub.launchRegedit.enabled",
                    false,
                    "Launch_regedit is not a button: " + ex.Message);
            }
        }

        if (launch is not null)
        {
            bool enabled = launch.IsEnabled;
            E2EResults.Record(
                "hub.launchRegedit.enabled",
                enabled,
                enabled ? "Launch_regedit is enabled." : "Launch_regedit is disabled.");
        }
        else if (launchElement is null)
        {
            E2EResults.Record("hub.launchRegedit.enabled", false, "Launch_regedit was not found.");
        }

        var about = WaitForText(hub, "HubAbout");
        var aboutHasLicense = about?.Contains("MPL-2.0", StringComparison.Ordinal) == true;
        E2EResults.Record(
            "hub.about",
            aboutHasLicense,
            $"HubAbout text was '{about ?? "(none)"}'.");

        if (launch is not null && launch.IsEnabled)
        {
            VerifyRegeditLaunch(launch);
        }

        hub.Close();
        E2EResults.Record(
            "hub.closed",
            !IsRunning(hub.ProcessId),
            $"Hub pid {hub.ProcessId} was closed by the test.");

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"E2E checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static int _checkStart;

    private static void VerifyRegeditLaunch(Button launch)
    {
        var before = SnapshotRegeditProcessIds();
        var startedUtc = DateTime.UtcNow;

        try
        {
            launch.Invoke();
            E2EResults.Note("hub.regedit.invoke", "Launch_regedit invoked.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("hub.regedit.invoke", false, "Launch_regedit invoke failed: " + ex.Message);
            return;
        }

        Process? child = null;
        try
        {
            child = WaitForNewRegedit(before, startedUtc, ProcessTimeout);
            E2EResults.Record(
                "hub.regedit.process",
                child is not null,
                child is null
                    ? "No new BwcRegedit process appeared within 15 s."
                    : $"Started BwcRegedit pid {child.Id}.");
            if (child is null)
            {
                return;
            }

            var handle = WaitForMainWindow(child, ProcessTimeout);
            E2EResults.Record(
                "hub.regedit.window",
                handle != IntPtr.Zero,
                handle == IntPtr.Zero
                    ? $"BwcRegedit pid {child.Id} showed no main window within 15 s."
                    : $"BwcRegedit pid {child.Id} main window is 0x{handle.ToInt64():X}.");
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var windowTitle = ReadWindowTitle(handle, TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "hub.regedit.title",
                string.Equals(windowTitle, RegeditTitle, StringComparison.Ordinal),
                $"expected '{RegeditTitle}', got '{windowTitle ?? "(none)"}'.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("hub.regedit.error", false, ex.Message);
        }
        finally
        {
            if (child is not null)
            {
                CloseChild(child);
            }
        }
    }

    private static HashSet<int> SnapshotRegeditProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(RegeditProcessName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static Process? WaitForNewRegedit(
        HashSet<int> before,
        DateTime notBeforeUtc,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process? match = null;
            foreach (var process in Process.GetProcessesByName(RegeditProcessName))
            {
                if (before.Contains(process.Id) || !StartedSince(process, notBeforeUtc))
                {
                    process.Dispose();
                    continue;
                }

                if (match is null)
                {
                    match = process;
                }
                else
                {
                    process.Dispose();
                }
            }

            if (match is not null)
            {
                return match;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static bool StartedSince(Process process, DateTime notBeforeUtc)
    {
        try
        {
            return process.StartTime.ToUniversalTime() >= notBeforeUtc.AddSeconds(-2);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    return IntPtr.Zero;
                }

                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return IntPtr.Zero;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    private static string? ReadWindowTitle(IntPtr handle, TimeSpan timeout)
    {
        using var automation = new UIA3Automation();
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = automation.FromHandle(handle).AsWindow().Title;
                if (string.Equals(last, RegeditTitle, StringComparison.Ordinal))
                {
                    return last;
                }
            }
            catch (Exception)
            {
                // The window may still be initializing or may have closed.
            }

            Thread.Sleep(100);
        }

        return last;
    }

    private static void CloseChild(Process child)
    {
        int processId = child.Id;
        try
        {
            if (!child.HasExited)
            {
                child.CloseMainWindow();
                if (!child.WaitForExit(TimeSpan.FromSeconds(10)) && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exited while it was being closed.
        }
        finally
        {
            child.Dispose();
        }

        bool running = IsRunning(processId);
        E2EResults.Record(
            "hub.regedit.closed",
            !running,
            running
                ? $"BwcRegedit pid {processId} is still running."
                : $"Closed BwcRegedit pid {processId}.");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static AutomationElement? WaitFor(AppSession session, string automationId)
    {
        try
        {
            return session.WaitForElement(automationId, ElementTimeout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? WaitForText(AppSession session, string automationId)
    {
        try
        {
            var element = session.WaitForElement(automationId, ElementTimeout);
            return session.TextOf(element);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
