using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Hives;

/// <summary>
/// Drives File > Load Hive on a non-elevated run. The test only opens and cancels the owned file
/// dialog and checks the menu selection rules, because mounting a hive needs SeRestorePrivilege.
/// The run sandbox and data root are removed in finally.
/// </summary>
[TestClass]
public sealed class HiveTests
{
    private const int MenuFile = 512;
    private const int CommandLoadHive = 680;
    private const int CommandUnloadHive = 681;
    private const string CommonDialogClass = "#32770";
    private const string SeedValueName = "HiveSeed";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Hives")]
    public void Hives_LoadDialogIsOwnedAndCancelLeavesNoMountedKey()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("hives.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            using (RegistryKey seed = sandbox.CreateSubKey(string.Empty))
            {
                seed.SetValue(SeedValueName, "hive", RegistryValueKind.String);
            }

            dataRoot = CreateFreshDataRoot(config, "hives");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.Ordinal),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "hives.navigate",
                navigated && seeded,
                $"navigated={navigated}, seeded={seeded}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            if (!navigated || !seeded)
            {
                FailIfNeeded();
                return;
            }

            string[] hklmBefore = HklmChildren();
            _ = ControlTestSupport.EnsureForeground(session);
            driver.OpenMenu(MenuFile);

            AutomationElement? loadItem = driver.WaitForElement("MenuCommand_" + CommandLoadHive);
            AutomationElement? unloadItem = driver.WaitForElement("MenuCommand_" + CommandUnloadHive);
            bool loadEnabled = IsEnabled(loadItem);
            bool unloadDisabled = unloadItem is not null && !IsEnabled(unloadItem);
            E2EResults.Record(
                "hives.menu.selection-rules",
                loadEnabled && unloadDisabled,
                $"loadPresent={loadItem is not null}, loadEnabled={loadEnabled}, unloadPresent={unloadItem is not null}, unloadDisabled={unloadDisabled}.");

            bool invoked = Invoke(loadItem);
            E2EResults.Record("hives.load.invoked", invoked, $"clicked={invoked}.");

            nint dialog = invoked ? WaitForFileDialog(session, TimeSpan.FromSeconds(10)) : 0;
            E2EResults.Record(
                "hives.load.dialog",
                dialog != 0,
                $"class='{(dialog == 0 ? string.Empty : ClassOf(dialog))}', hwnd=0x{dialog:X}.");

            if (dialog != 0)
            {
                nint owner = Win32.GetWindow(dialog, Win32.GvOwner);
                E2EResults.Record(
                    "hives.load.dialog-owned",
                    owner == session.MainHwnd,
                    $"owner=0x{owner:X}, main=0x{session.MainHwnd:X}.");

                bool mainDisabled = !Win32.IsWindowEnabled(session.MainHwnd);
                E2EResults.Record(
                    "hives.load.main-disabled",
                    mainDisabled,
                    $"IsWindowEnabled(main) while open={!mainDisabled}.");

                bool cancelled = TryCancel(dialog);
                bool gone = cancelled && ControlTestSupport.WaitUntil(
                    () => !Win32.IsWindow(dialog),
                    TimeSpan.FromSeconds(8));
                E2EResults.Record(
                    "hives.load.cancel-closed",
                    gone,
                    $"cancelled={cancelled}, gone={gone}.");

                bool enabledAgain = ControlTestSupport.WaitUntil(
                    () => Win32.IsWindowEnabled(session.MainHwnd),
                    TimeSpan.FromSeconds(8));
                E2EResults.Record(
                    "hives.load.main-enabled-after",
                    enabledAgain,
                    $"IsWindowEnabled(main) after close={enabledAgain}.");
            }

            string[] hklmAfter = HklmChildren();
            bool noNewKey = hklmAfter.Length == hklmBefore.Length &&
                hklmAfter.All(name => hklmBefore.Contains(name, StringComparer.OrdinalIgnoreCase));
            E2EResults.Record(
                "hives.load.no-key",
                noNewKey,
                $"before={hklmBefore.Length}, after={hklmAfter.Length}.");

            bool selectionKept = ControlTestSupport.WaitForTreeSelection(
                session,
                config.RunId,
                TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "hives.load.selection-kept",
                selectionKept,
                $"tree selection '{config.RunId}' after cancel={selectionKept}.");

            E2EResults.Record(
                "hives.elevation-note",
                true,
                "A load/unload round-trip needs SeRestorePrivilege. This non-elevated run only opens and cancels the owned file dialog, so no hive was mounted.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static string[] HklmChildren()
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return hklm.GetSubKeyNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEnabled(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.IsEnabled.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Invoke(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            element.Focus();
            Thread.Sleep(150);
            Keyboard.Type(VirtualKeyShort.RETURN);
            return true;
        }
        catch (Exception)
        {
            try
            {
                element.Click();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static nint WaitForFileDialog(AppSession session, TimeSpan timeout)
    {
        nint found = 0;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindFileDialog(session.ProcessId)) != 0,
            timeout);
        return found;
    }

    private static nint FindFileDialog(int processId)
    {
        nint found = 0;
        _ = EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out uint windowProcessId);
                if (windowProcessId != (uint)processId ||
                    !IsWindowVisible(window) ||
                    !string.Equals(ClassOf(window), CommonDialogClass, StringComparison.Ordinal))
                {
                    return true;
                }

                found = window;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private static bool TryCancel(nint dialog)
    {
        try
        {
            _ = SetForegroundWindow(dialog);
            Thread.Sleep(300);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            return ControlTestSupport.WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            return !IsWindow(dialog);
        }
    }

    private static string ClassOf(nint window)
    {
        var buffer = new StringBuilder(64);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDataRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit path may still hold a settings file for a moment.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Hive checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private delegate bool EnumWindowsProc(nint window, nint lParam);

#pragma warning disable SYSLIB1054 // The small window discovery surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);
#pragma warning restore SYSLIB1054
}
