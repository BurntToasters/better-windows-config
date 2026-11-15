using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Remote;

/// <summary>
/// Drives File > Connect Network Registry on a non-elevated run. The test opens the computer picker (the system
/// object picker, caption "Select Computer"), checks that it is owned by the main window, and cancels it. A real
/// remote connection, remote browsing, and the disconnect list need a second computer or VM snapshot and are manual
/// checks. The connect error text and the Import flow are in RemoteConnectImportTests.
/// </summary>
[TestClass]
public sealed class RemoteRegistryTests
{
    private const int MenuFile = 512;
    private const int CommandConnectNetworkRegistry = 657;
    private const int CommandDisconnectNetworkRegistry = 768;
    private const string PickerTitle = "Select Computer";
    private const string LoopbackNode = @"\\127.0.0.1";
    private const string SeedValueName = "RemoteSeed";

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

            // No BWC_E2E_CONNECT_TARGET: File > Connect must open the picker.
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

            // The command opens a modal picker, so Invoke can report false while it is up. The window is awaited anyway.
            _ = menuOpened && driver.Invoke("MenuCommand_" + CommandConnectNetworkRegistry);
            nint picker = menuOpened ? RemoteWindows.WaitForOwnedWindow(session, PickerTitle, TimeSpan.FromSeconds(15)) : 0;
            bool owned = picker != 0 && Win32.GetWindow(picker, Win32.GvOwner) == session.MainHwnd;
            bool mainDisabled = picker != 0 && !Win32.IsWindowEnabled(session.MainHwnd);
            string title = picker != 0 ? RemoteWindows.TitleOf(picker) : string.Empty;
            E2EResults.Record(
                "remote.connect.dialog",
                picker != 0 && owned && mainDisabled && string.Equals(title, PickerTitle, StringComparison.Ordinal),
                $"picker={picker != 0}, hwnd=0x{picker:X}, owned={owned}, mainDisabled={mainDisabled}, caption='{title}'. " +
                $"windows titled '{PickerTitle}': {RemoteWindows.DescribeWindowsTitled(session, PickerTitle)}.");

            bool cancelled = picker != 0 &&
                RemoteWindows.Close(picker) &&
                ControlTestSupport.WaitUntil(() => !RemoteWindows.Exists(picker), TimeSpan.FromSeconds(10));
            bool enabledAgain = cancelled && ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(8));
            bool disconnectStillDisabled = enabledAgain && IsDisconnectDisabled(session, driver);
            E2EResults.Record(
                "remote.connect.cancel",
                cancelled && enabledAgain && disconnectStillDisabled,
                $"cancelled={cancelled}, enabledAgain={enabledAgain}, disconnectStillDisabled={disconnectStillDisabled}.");

            E2EGuards.RequireSandboxSelection(session, sandbox, "remote.sandbox.connect");
            AutomationElement? loopbackNode = enabledAgain
                ? ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    LoopbackNode)
                : null;
            bool noNode = enabledAgain && loopbackNode is null && IsDisconnectDisabled(session, driver);
            E2EResults.Record(
                "remote.connect.no-connection",
                noNode,
                $"enabledAgain={enabledAgain}, node '{LoopbackNode}' present={loopbackNode is not null}, disconnect disabled={noNode}.");

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
}
