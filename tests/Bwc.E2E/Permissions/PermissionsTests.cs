using Bwc.E2E.Controls;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Permissions;

/// <summary>
/// Opens the aclui security editor from Edit > Permissions and from the tree context menu. The
/// tests only cancel the dialog: ACL mutation is covered by the registry engine tests.
/// </summary>
[TestClass]
public sealed class PermissionsTests
{
    private const int MenuEdit = 513;
    private const int CommandPermissions = 678;
    private const string SeedValueName = "PermissionValue";

    private static readonly string[] TreeMenuNames =
    [
        "Expand",
        "New",
        "Find...",
        "Delete",
        "Rename",
        "Export",
        "Permissions...",
        "Copy Key Name",
        "Go to HKEY_LOCAL_MACHINE",
    ];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Mutating")]
    public void Permissions_EditMenuOpensOwnedAcluiDialogAndCancelRestoresMainWindow()
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
            E2EResults.Record("permissions.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "permissions");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seededRows = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "permissions.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.sandbox.navigate");

            _ = ControlTestSupport.EnsureForeground(session);
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.sandbox.menu");
            driver.OpenMenu(MenuEdit);
            AutomationElement? permissionsItem = driver.WaitForElement("MenuCommand_" + CommandPermissions);
            bool menuEnabled = IsEnabled(permissionsItem);
            E2EResults.Record(
                "permissions.editmenu.enabled",
                menuEnabled,
                $"itemPresent={permissionsItem is not null}, enabled={menuEnabled}.");

            bool clicked = ClickElement(permissionsItem);
            E2EResults.Record("permissions.editmenu.invoked", clicked, $"clicked={clicked}.");

            nint dialog = clicked ? WaitForOwnedDialog(session, TimeSpan.FromSeconds(10)) : 0;
            E2EResults.Record(
                "permissions.dialog.exists",
                dialog != 0,
                $"dialog=0x{dialog:X}, class='{(dialog == 0 ? string.Empty : PermissionsNative.ClassOf(dialog))}', title='{(dialog == 0 ? string.Empty : PermissionsNative.TitleOf(dialog))}'.");

            if (dialog != 0)
            {
                nint owner = PermissionsNative.GetWindow(dialog, PermissionsNative.GvOwner);
                E2EResults.Record(
                    "permissions.dialog.owned-by-main",
                    owner == session.MainHwnd,
                    $"owner=0x{owner:X}, main=0x{session.MainHwnd:X}.");

                bool mainEnabledWhileOpen = PermissionsNative.IsWindowEnabled(session.MainHwnd);
                E2EResults.Record(
                    "permissions.dialog.main-disabled",
                    !mainEnabledWhileOpen,
                    $"IsWindowEnabled(main) while open={mainEnabledWhileOpen}.");

                bool cancelSent = TryCancel(session, dialog, out string cancelMethod);
                bool gone = ControlTestSupport.WaitUntil(
                    () => !PermissionsNative.IsWindow(dialog),
                    TimeSpan.FromSeconds(8));
                E2EResults.Record(
                    "permissions.dialog.cancel-closed",
                    cancelSent && gone,
                    $"method={cancelMethod}, sent={cancelSent}, gone={gone}.");

                bool enabledAgain = ControlTestSupport.WaitUntil(
                    () => PermissionsNative.IsWindowEnabled(session.MainHwnd),
                    TimeSpan.FromSeconds(8));
                E2EResults.Record(
                    "permissions.dialog.main-enabled-after",
                    enabledAgain,
                    $"IsWindowEnabled(main) after close={enabledAgain}.");

                bool selectionKept = ControlTestSupport.WaitForTreeSelection(
                    session,
                    config.RunId,
                    TimeSpan.FromSeconds(5));
                E2EResults.Record(
                    "permissions.dialog.selection-kept",
                    selectionKept,
                    $"tree selection '{config.RunId}' after close={selectionKept}.");
            }

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    [TestMethod]
    [TestCategory("Permissions")]
    public void Permissions_TreeContextMenuShowsEnabledPermissions()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("permissions.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "permissions-menu");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var menus = new ContextMenuDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seededRows = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "permissions.contextmenu.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            if (!navigated || !seededRows)
            {
                FailIfNeeded();
                return;
            }

            AutomationElement? row = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                config.RunId);
            ControlTestSupport.Select(row);
            bool selected = ControlTestSupport.WaitForTreeSelection(session, config.RunId);

            bool clicked = menus.RightClick(row);
            MenuSnapshot? snapshot = menus.WaitForMenu(TreeMenuNames);
            MenuEntry? permissions = snapshot?.Find(CommandPermissions);
            string actual = snapshot is null ? "<none>" : string.Join(",", snapshot.Names);
            E2EResults.Record(
                "permissions.contextmenu.open",
                selected && clicked && snapshot is not null,
                $"selected={selected}, rightClick={clicked}, names=[{actual}].");
            E2EResults.Record(
                "permissions.contextmenu.enabled",
                permissions is { Enabled: true },
                $"permissionsPresent={permissions is not null}, enabled={permissions?.Enabled}.");

            ControlTestSupport.SendKeys(session, "ESCAPE");
            _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(5));
            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(SeedValueName, "permissions", RegistryValueKind.String);
        }

        using var child = sandbox.CreateSubKey("PermissionChild");
        child.SetValue("Inside", "child", RegistryValueKind.String);
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

    private static bool ClickElement(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            System.Drawing.Rectangle bounds = element.BoundingRectangle;
            int x = bounds.Left + (bounds.Width / 2);
            int y = bounds.Top + (bounds.Height / 2);
            Mouse.Click(new System.Drawing.Point(x, y));
            return true;
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
            () => (found = FindOwnedDialog(session.ProcessId, session.MainHwnd)) != 0,
            timeout);
        return found;
    }

    private static nint FindOwnedDialog(int processId, nint owner)
    {
        nint found = 0;
        _ = PermissionsNative.EnumWindows(
            (hwnd, lParam) =>
            {
                _ = lParam;
                PermissionsNative.GetWindowThreadProcessId(hwnd, out uint windowProcessId);
                if (windowProcessId != (uint)processId ||
                    !PermissionsNative.IsWindowVisible(hwnd) ||
                    !string.Equals(PermissionsNative.ClassOf(hwnd), "#32770", StringComparison.Ordinal) ||
                    PermissionsNative.GetWindow(hwnd, PermissionsNative.GvOwner) != owner)
                {
                    return true;
                }

                found = hwnd;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private static bool TryCancel(AppSession session, nint dialog, out string method)
    {
        method = "none";

        try
        {
            _ = PermissionsNative.SetForegroundWindow(dialog);
            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? cancel = root.FindFirstDescendant(
                cf => cf.ByName("Cancel").And(cf.ByControlType(ControlType.Button)));
            if (cancel is not null)
            {
                cancel.Click();
                method = "cancel-button";
                if (ControlTestSupport.WaitUntil(
                    () => !PermissionsNative.IsWindow(dialog),
                    TimeSpan.FromSeconds(2)))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to Escape below.
        }

        try
        {
            _ = PermissionsNative.SetForegroundWindow(dialog);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            method = method == "cancel-button" ? "cancel-button-then-escape" : "escape";
            return true;
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
            $"Permissions checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
