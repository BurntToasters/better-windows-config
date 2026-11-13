using System.Security.AccessControl;
using System.Security.Principal;
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
    private const string ChildName = "PermissionChild";
    private const string ReplaceCheckBoxFragment = "Replace all child object permission entries";
    private const string ThisKeyAndSubkeysText = "This key and subkeys";
    private const string ReplaceConfirmFragment = "Do you want to replace the permission on all existing subkeys within";
    private const string AcluiReplaceConfirmFragment = "This will replace explicitly defined permissions";

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

            bool settled = WaitForItemToSettle(permissionsItem);
            E2EResults.Record("permissions.editmenu.settled", settled, $"settled={settled}.");
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

    /// <summary>
    /// Drives the real aclui editor: Edit > Permissions, Advanced, the "Replace all child object permission
    /// entries" check box, OK, Yes to the MSG-178 confirmation, OK. The parent carries an inheritable entry
    /// for the current user (inherited from HKCU), and the child carries an explicit entry for Everyone.
    /// After the replace, the child must
    /// hold only inherited entries. The result is read back with the managed registry security API. Every
    /// change is inside HKCU\Software\BWC-E2E\&lt;run-id&gt;, and the run root is deleted in the finally block.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Mutating")]
    public void Permissions_AdvancedReplaceChildEntriesGivesChildrenOnlyInheritedEntries()
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
            E2EResults.Record("permissions.propagation.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            SeedReplaceScenario(sandbox);
            dataRoot = CreateFreshDataRoot(config, "permissions-propagation");
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
                "permissions.propagation.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.propagation.sandbox.navigate");

            _ = ControlTestSupport.EnsureForeground(session);
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.propagation.sandbox.menu");
            driver.OpenMenu(MenuEdit);
            AutomationElement? permissionsItem = driver.WaitForElement("MenuCommand_" + CommandPermissions);
            bool settled = WaitForItemToSettle(permissionsItem);
            E2EResults.Record("permissions.propagation.settled", settled, $"settled={settled}.");
            bool clicked = ClickElement(permissionsItem);

            nint sheet = clicked ? WaitForOwnedDialogOf(session.ProcessId, session.MainHwnd, TimeSpan.FromSeconds(10)) : 0;
            E2EResults.Record(
                "permissions.propagation.sheet",
                sheet != 0,
                $"clicked={clicked}, sheet=0x{sheet:X}.");

            if (sheet != 0)
            {
                bool advancedClicked = ClickButtonNamed(sheet, "Advanced");
                nint advanced = advancedClicked
                    ? WaitForOwnedDialogOf(session.ProcessId, sheet, TimeSpan.FromSeconds(10))
                    : 0;
                E2EResults.Record(
                    "permissions.propagation.advanced.open",
                    advanced != 0,
                    $"advancedClicked={advancedClicked}, advanced=0x{advanced:X}.");

                if (advanced != 0)
                {
                    bool inheritanceListed = ContainsText(advanced, ThisKeyAndSubkeysText);
                    E2EResults.Record(
                        "permissions.propagation.advanced.container-controls",
                        inheritanceListed,
                        $"'{ThisKeyAndSubkeysText}' shown in the Advanced entries={inheritanceListed}.");

                    bool replaceChecked = SetCheckBox(advanced, ReplaceCheckBoxFragment, true);
                    E2EResults.Record(
                        "permissions.propagation.advanced.replace-checked",
                        replaceChecked,
                        $"'{ReplaceCheckBoxFragment}' checked={replaceChecked}.");

                    bool advancedOk = replaceChecked && ClickButtonNamed(advanced, "OK");
                    E2EResults.Record("permissions.propagation.advanced.ok", advancedOk, $"clicked={advancedOk}.");

                    // aclui asks its own question when Advanced OK is pressed with the replace box checked. The
                    // question is a #32770 window titled "Windows Security", owned by the Advanced window.
                    nint aclConfirm = advancedOk ? WaitForOwnedDialogOf(session.ProcessId, advanced, TimeSpan.FromSeconds(10)) : 0;
                    bool aclConfirmText = aclConfirm != 0 && ContainsText(aclConfirm, AcluiReplaceConfirmFragment);
                    bool aclConfirmYes = aclConfirmText && ClickButtonNamed(aclConfirm, "Yes");
                    // The replace is confirmed with the classic MSG-178 question before any key changes. BWC writes
                    // inside the Advanced OK (SetSecurity runs there), so the question appears while the Advanced
                    // window is still open. It is a message box owned by the main window, so the sheet is excluded.
                    nint confirm = advancedOk
                        ? WaitForOwnedDialogExcept(session.ProcessId, session.MainHwnd, sheet, TimeSpan.FromSeconds(10))
                        : 0;
                    bool confirmText = confirm != 0 && ContainsText(confirm, ReplaceConfirmFragment);
                    bool confirmYes = confirmText && ClickButtonNamed(confirm, "Yes");
                    E2EResults.Record(
                        "permissions.propagation.replace-confirm",
                        confirmYes,
                        $"shown=0x{confirm:X}, textMatched={confirmText}, yesClicked={confirmYes}.");

                    bool advancedGone = ControlTestSupport.WaitUntil(
                        () => !PermissionsNative.IsWindow(advanced),
                        TimeSpan.FromSeconds(15));
                    E2EResults.Record("permissions.propagation.advanced.closed", advancedGone, $"gone={advancedGone}.");
                }

                bool sheetOk = ClickButtonNamed(sheet, "OK");
                bool sheetClosed = ControlTestSupport.WaitUntil(
                    () => !PermissionsNative.IsWindow(sheet),
                    TimeSpan.FromSeconds(15));
                E2EResults.Record(
                    "permissions.propagation.sheet.apply",
                    sheetOk && sheetClosed,
                    $"okClicked={sheetOk}, closed={sheetClosed}.");
            }

            VerifyChildInheritedOnly(sandbox);
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

    /// <summary>
    /// Waits until the menu item is enabled and its bounding rectangle holds still across two reads 150 ms
    /// apart. A flyout drops a press that arrives while it is still opening, so the real click waits for the
    /// settle. The wait is at least 300 ms and gives up after 5 s.
    /// </summary>
    private static bool WaitForItemToSettle(AutomationElement? item)
    {
        if (item is null)
        {
            return false;
        }

        long start = Environment.TickCount64;
        System.Drawing.Rectangle previous;
        try
        {
            previous = item.BoundingRectangle;
        }
        catch (Exception)
        {
            return false;
        }

        while (true)
        {
            Thread.Sleep(150);
            long elapsed = Environment.TickCount64 - start;
            System.Drawing.Rectangle current;
            try
            {
                current = item.BoundingRectangle;
            }
            catch (Exception)
            {
                return false;
            }

            if (current == previous && elapsed >= 300 && IsEnabled(item))
            {
                return true;
            }

            if (elapsed >= 5000)
            {
                return false;
            }

            previous = current;
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

    private static nint FindOwnedDialog(int processId, nint owner, nint exclude = 0)
    {
        nint found = 0;
        _ = PermissionsNative.EnumWindows(
            (hwnd, lParam) =>
            {
                _ = lParam;
                PermissionsNative.GetWindowThreadProcessId(hwnd, out uint windowProcessId);
                if (windowProcessId != (uint)processId ||
                    hwnd == exclude ||
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
            // The click goes to the dialog only when the guard has made it the foreground window.
            bool focused = ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2));
            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? cancel = focused
                ? root.FindFirstDescendant(cf => cf.ByName("Cancel").And(cf.ByControlType(ControlType.Button)))
                : null;
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
            if (!ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

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

    private static void SeedReplaceScenario(SandboxRegistry sandbox)
    {
        using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(SeedValueName, "permissions", RegistryValueKind.String);
        }

        using RegistryKey child = sandbox.CreateSubKey(ChildName);
        child.SetValue("Inside", "child", RegistryValueKind.String);

        // An explicit, non-inherited entry on the child that the replace must remove.
        RegistrySecurity childSecurity = child.GetAccessControl(AccessControlSections.Access);
        childSecurity.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            RegistryRights.ReadKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        child.SetAccessControl(childSecurity);
    }

    private static void VerifyChildInheritedOnly(SandboxRegistry sandbox)
    {
        SecurityIdentifier user = CurrentUserSid();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        using RegistryKey? child = OpenSandboxChild(sandbox);
        if (child is null)
        {
            E2EResults.Record("permissions.propagation.child.exists", false, "The sandbox child key is missing.");
            return;
        }

        AuthorizationRuleCollection rules = child.GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        int explicitCount = 0;
        bool everyoneExplicit = false;
        bool userInherited = false;
        foreach (RegistryAccessRule rule in rules.Cast<RegistryAccessRule>())
        {
            if (!rule.IsInherited)
            {
                explicitCount += 1;
                everyoneExplicit |= rule.IdentityReference.Equals(everyone);
            }
            else if (rule.IdentityReference.Equals(user) &&
                     (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0)
            {
                userInherited = true;
            }
        }

        E2EResults.Record(
            "permissions.propagation.child.no-explicit-entries",
            explicitCount == 0 && !everyoneExplicit,
            $"explicitCount={explicitCount}, everyoneExplicit={everyoneExplicit}.");
        E2EResults.Record(
            "permissions.propagation.child.inherits-user",
            userInherited,
            $"inherited container entry for the current user present={userInherited}.");
    }

    private static RegistryKey? OpenSandboxChild(SandboxRegistry sandbox)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return hkcu.OpenSubKey(sandbox.RunRootPath + @"\" + ChildName, writable: false);
    }

    private static SecurityIdentifier CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private static nint WaitForOwnedDialogOf(int processId, nint owner, TimeSpan timeout)
    {
        nint found = 0;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindOwnedDialog(processId, owner)) != 0,
            timeout);
        return found;
    }

    private static nint WaitForOwnedDialogExcept(int processId, nint owner, nint exclude, TimeSpan timeout)
    {
        nint found = 0;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindOwnedDialog(processId, owner, exclude)) != 0,
            timeout);
        return found;
    }

    private static bool ClickButtonNamed(nint dialog, string name)
    {
        try
        {
            if (!ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? button = root.FindFirstDescendant(
                cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)));
            if (button is null)
            {
                return false;
            }

            button.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ContainsText(nint dialog, string text)
    {
        try
        {
            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            return root
                .FindAllDescendants()
                .Any(element => element.Name?.Contains(text, StringComparison.Ordinal) == true);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SetCheckBox(nint dialog, string nameFragment, bool value)
    {
        try
        {
            if (!ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? box = root
                .FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(element => element.Name?.Contains(nameFragment, StringComparison.Ordinal) == true);
            if (box is null)
            {
                return false;
            }

            box.AsCheckBox().IsChecked = value;
            return box.AsCheckBox().IsChecked == value;
        }
        catch (Exception)
        {
            return false;
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
