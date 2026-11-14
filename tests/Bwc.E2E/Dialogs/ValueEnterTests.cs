using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// Presses Enter twice on a selected string value with no pause. The second press arrives while the first is
/// still reading the value, so only one value editor may open. After Cancel the owner is enabled again and
/// no editor is left behind.
/// </summary>
[TestClass]
public sealed class ValueEnterTests
{
    private const string ValueName = "DoubleEnter";
    private const string EditCaption = "Edit String";
    private const string SecondaryId = "DialogSecondary";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void ValueEnter_TwoFastPressesOpenOneEditor()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("valueenter.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        try
        {
            sandbox.Cleanup();
            sandbox.SetString(string.Empty, ValueName, "first");
            dataRoot = ClassicDialogTests.FreshDataRoot(config, "valueenter");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "valueenter.sandbox.navigate");
            AutomationElement? row = navigated
                ? ClassicDialogTests.OpenValueRow(session, sandbox, ValueName, "valueenter.row")
                : null;
            if (row is null)
            {
                FailIfNeeded();
                return;
            }

            int pid = session.ProcessId;
            int before = CountEditWindows(pid);
            Record("valueenter.before", before == 0, $"edit windows before Enter={before}.");

            _ = ControlTestSupport.EnsureForeground(session);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Keyboard.Press(VirtualKeyShort.RETURN);
            bool opened = ControlTestSupport.WaitUntil(() => CountEditWindows(pid) >= 1, UiTimeout);
            Thread.Sleep(1500);
            int after = CountEditWindows(pid);
            Record(
                "valueenter.oneEditor",
                opened && after == 1,
                $"edit windows after two fast Enter presses={after}, opened={opened}.");

            bool cancelled = opened && after == 1 && driver.WaitForDialog() && driver.Invoke(SecondaryId);
            bool gone = cancelled && ControlTestSupport.WaitUntil(() => CountEditWindows(pid) == 0, UiTimeout);
            bool enabled = gone && ControlTestSupport.WaitUntil(() => session.IsEnabled, UiTimeout);
            bool foreground = enabled && ControlTestSupport.WaitUntil(
                () => Win32.IsForeground(session.MainHwnd),
                TimeSpan.FromSeconds(5));
            Record(
                "valueenter.cancel.normal",
                cancelled && gone && enabled && foreground,
                $"Cancel sent={cancelled}, editor gone={gone}, owner enabled={enabled}, main foreground={foreground}.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            if (dataRoot is not null && Directory.Exists(dataRoot))
            {
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch (IOException)
                {
                    // The data root is scoped to this run and is removed best effort.
                }
            }
        }

        FailIfNeeded();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
#pragma warning restore SYSLIB1054

    /// <summary>Counts visible top-level windows of the app process whose caption is the string value editor's.</summary>
    private static int CountEditWindows(int processId)
    {
        int count = 0;
        _ = EnumWindows(
            (hwnd, unusedParam) =>
            {
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != processId || !IsWindowVisible(hwnd))
                {
                    return true;
                }

                var caption = new StringBuilder(256);
                _ = GetWindowText(hwnd, caption, caption.Capacity);
                if (string.Equals(caption.ToString(), EditCaption, StringComparison.Ordinal))
                {
                    count++;
                }

                return true;
            },
            IntPtr.Zero);
        return count;
    }

    private static void Record(string id, bool pass, string notes) =>
        _ = E2EResults.Record(id, pass, notes);

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Value Enter checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
