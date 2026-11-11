using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Remote;

/// <summary>
/// Drives File > Connect Network Registry on a non-elevated run. The test only opens and cancels
/// the owned name prompt and rejects the local computer name before any native call. A real remote
/// connection, remote browsing, and the disconnect list need a second computer or VM snapshot and
/// are manual checks.
/// </summary>
[TestClass]
public sealed class RemoteRegistryTests
{
    private const int MenuFile = 512;
    private const int CommandConnectNetworkRegistry = 657;
    private const int CommandDisconnectNetworkRegistry = 768;
    private const string ConnectDialogTitle = "Connect Network Registry";
    private const string SeedValueName = "RemoteSeed";
    private const string LocalComputerMessage = "You cannot connect to your own computer.";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Remote_ConnectDialogIsOwnedAndCancelLeavesNoConnection()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("remote.publish", false, ex.Message);
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
                seed.SetValue(SeedValueName, "remote", RegistryValueKind.String);
            }

            dataRoot = CreateFreshDataRoot(config, "remote");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "remote.navigate",
                navigated && seeded,
                $"navigated={navigated}, seeded={seeded}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, "remote.sandbox.navigate");

            bool menuOpened = TryOpenMenu(driver, MenuFile);
            AutomationElement? connectItem = menuOpened
                ? driver.WaitForElement("MenuCommand_" + CommandConnectNetworkRegistry)
                : null;
            AutomationElement? disconnectItem = menuOpened
                ? driver.WaitForElement("MenuCommand_" + CommandDisconnectNetworkRegistry)
                : null;
            bool connectEnabled = IsEnabled(connectItem);
            bool disconnectDisabled = disconnectItem is not null && !IsEnabled(disconnectItem);
            E2EResults.Record(
                "remote.menu.selection-rules",
                menuOpened && connectEnabled && disconnectDisabled,
                $"menu={menuOpened}, connectPresent={connectItem is not null}, connectEnabled={connectEnabled}, disconnectPresent={disconnectItem is not null}, disconnectDisabled={disconnectDisabled}.");

            bool invoked = menuOpened && driver.Invoke("MenuCommand_" + CommandConnectNetworkRegistry);
            bool dialogShown = invoked && driver.WaitForDialog(TimeSpan.FromSeconds(10));
            nint dialog = dialogShown ? WaitForOwnedDialog(session, TimeSpan.FromSeconds(8)) : 0;
            bool owned = dialog != 0 &&
                Win32.GetWindow(dialog, Win32.GvOwner) == session.MainHwnd;
            bool mainDisabled = dialog != 0 && !Win32.IsWindowEnabled(session.MainHwnd);
            string heading = dialogShown ? driver.TextOf("DialogHeading") : string.Empty;
            AutomationElement? input = dialogShown ? driver.WaitForElement("DialogTextInput") : null;
            string label = ControlTestSupport.SafeName(input);
            E2EResults.Record(
                "remote.connect.dialog",
                dialogShown &&
                    owned &&
                    mainDisabled &&
                    heading.Length > 0 &&
                    label.Length > 0,
                $"dialog={dialogShown}, hwnd=0x{dialog:X}, owned={owned}, mainDisabled={mainDisabled}, heading='{heading}', label='{label}'.");

            bool cancelled = dialogShown &&
                driver.Invoke("DialogSecondary") &&
                driver.WaitForNoDialog();
            bool enabledAgain = cancelled && ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(8));
            bool disconnectStillDisabled = enabledAgain && IsDisconnectDisabled(session, driver);
            E2EResults.Record(
                "remote.connect.cancel",
                cancelled && enabledAgain && disconnectStillDisabled,
                $"cancelled={cancelled}, enabledAgain={enabledAgain}, disconnectStillDisabled={disconnectStillDisabled}.");

            E2EGuards.RequireSandboxSelection(session, sandbox, "remote.sandbox.connect");
            bool secondMenu = TryOpenMenu(driver, MenuFile);
            bool secondInvoked = secondMenu && driver.Invoke("MenuCommand_" + CommandConnectNetworkRegistry);
            bool secondDialog = secondInvoked && driver.WaitForDialog(TimeSpan.FromSeconds(10));
            bool typed = secondDialog && driver.SetText("DialogTextInput", Environment.MachineName);
            bool submitted = typed && driver.Invoke("DialogPrimary");
            bool localMessageShown = submitted && ControlTestSupport.WaitUntil(
                () => string.Equals(
                    driver.TextOf("DialogValidationText"),
                    LocalComputerMessage,
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            string validation = localMessageShown ? driver.TextOf("DialogValidationText") : string.Empty;
            bool dialogStayedOpen = localMessageShown && driver.WaitForDialog(TimeSpan.FromSeconds(2));
            bool localCancelled = dialogStayedOpen &&
                driver.Invoke("DialogSecondary") &&
                driver.WaitForNoDialog();
            E2EResults.Record(
                "remote.connect.local-rejected",
                secondDialog && typed && submitted && localMessageShown && dialogStayedOpen && localCancelled,
                $"dialog={secondDialog}, typed={typed}, submitted={submitted}, message='{validation}', stayedOpen={dialogStayedOpen}, cancelled={localCancelled}.");

            bool mainEnabledAgain = localCancelled && ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(8));
            string remoteNodeName = @"\\" + Environment.MachineName;
            AutomationElement? remoteNode = mainEnabledAgain
                ? ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    remoteNodeName)
                : null;
            bool disconnectStillOff = mainEnabledAgain && IsDisconnectDisabled(session, driver);
            E2EResults.Record(
                "remote.connect.no-connection",
                mainEnabledAgain && remoteNode is null && disconnectStillOff,
                $"mainEnabled={mainEnabledAgain}, nodePresent={remoteNode is not null}, nodeName='{remoteNodeName}', disconnectStillDisabled={disconnectStillOff}.");

            E2EResults.Note(
                "remote.manual-browse-note",
                "Browsing a connected computer, expanding its remote roots, and the disconnect list require a second machine or VM snapshot with the Remote Registry service and network permissions. Those checks are manual and must run only on an isolated network.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static bool TryOpenMenu(EditingDriver driver, int menuCommand)
    {
        try
        {
            driver.OpenMenu(menuCommand);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("remote.menu.open", false, ex.Message);
            return false;
        }
    }

    private static bool IsDisconnectDisabled(AppSession session, EditingDriver driver)
    {
        if (!TryOpenMenu(driver, MenuFile))
        {
            return false;
        }

        AutomationElement? item = driver.WaitForElement(
            "MenuCommand_" + CommandDisconnectNetworkRegistry);
        bool disabled = item is not null && !IsEnabled(item);

        try
        {
            ControlTestSupport.SendKeys(session, "ESCAPE");
        }
        catch (Exception)
        {
            // The menu closes when the next command opens or the window loses focus.
        }

        return disabled;
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

    private static nint WaitForOwnedDialog(AppSession session, TimeSpan timeout)
    {
        nint found = 0;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindOwnedDialog(session)) != 0,
            timeout);
        return found;
    }

    private static nint FindOwnedDialog(AppSession session)
    {
        nint found = 0;
        _ = EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out uint windowProcessId);
                if (windowProcessId != (uint)session.ProcessId ||
                    !IsWindowVisible(window) ||
                    Win32.GetWindow(window, Win32.GvOwner) != session.MainHwnd ||
                    !string.Equals(TitleOf(window), ConnectDialogTitle, StringComparison.Ordinal))
                {
                    return true;
                }

                found = window;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private static string TitleOf(nint window)
    {
        var buffer = new StringBuilder(256);
        return GetWindowText(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
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
            $"Remote registry checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
#pragma warning restore SYSLIB1054
}
