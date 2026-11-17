using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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
/// Opens the aclui security editor from Edit > Permissions and from the tree context menu, and the Advanced and Owner
/// sheets from it. Read-only checks cancel the editor or the sheet. Tests that change security press OK, Apply, or the
/// Advanced sheet's OK on sandbox keys under the run root, then read the DACL or the owner back: the Allow box (Apply,
/// then restore), owner recursion (OK), the replace of child entries (OK), and the remote key Apply. The Elevated tests
/// add audit entries and change the owner through the elevated runner. The registry engine tests cover the ACL logic.
/// </summary>
[TestClass]
public sealed class PermissionsTests
{
    private const int MenuEdit = 513;
    private const int CommandPermissions = 678;
    private const string SeedValueName = "PermissionValue";
    private const string ChildName = "PermissionChild";
    private const string ReplaceCheckBoxFragment = "Replace all child object permission entries";
    private const string EffectiveAccessTab = "Effective Access";
    private const string EffectiveAccessButton = "View effective access";
    private const string EffectiveUserLink = "Select a user";
    private const string ThisKeyAndSubkeysText = "This key and subkeys";
    private const string InheritedFromText = @"CURRENT_USER\Software";
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

                    bool replaceChecked = SetCheckBox(advanced, ReplaceCheckBoxFragment, true, out string replaceReason);
                    E2EResults.Record(
                        "permissions.propagation.advanced.replace-checked",
                        replaceChecked,
                        $"'{ReplaceCheckBoxFragment}' checked={replaceChecked} ({replaceReason}).");

                    bool advancedOk = replaceChecked && ClickButtonNamed(advanced, "OK");
                    E2EResults.Record("permissions.propagation.advanced.ok", advancedOk, $"clicked={advancedOk}.");

                    // aclui asks its own question when Advanced OK is pressed with the replace box checked. The
                    // question is a #32770 window titled "Windows Security", owned by the Advanced window.
                    nint aclConfirm = advancedOk ? WaitForOwnedDialogOf(session.ProcessId, advanced, TimeSpan.FromSeconds(10)) : 0;
                    bool aclConfirmText = aclConfirm != 0 && ContainsText(aclConfirm, AcluiReplaceConfirmFragment);
                    bool aclConfirmYes = aclConfirmText && ClickButtonNamed(aclConfirm, "Yes");
                    // The replace is confirmed with the classic MSG-178 question before any key changes. BWC writes
                    // inside the Advanced OK (SetSecurity runs there), so the question appears while the Advanced
                    // window is still open. It is a message box owned by the active aclui window, the Advanced one
                    // (permissions.md section 42, item I).
                    nint confirm = advancedOk
                        ? WaitForOwnedDialogOf(session.ProcessId, advanced, TimeSpan.FromSeconds(10))
                        : 0;
                    nint confirmOwner = confirm == 0 ? 0 : PermissionsNative.GetWindow(confirm, PermissionsNative.GvOwner);
                    E2EResults.Record(
                        "permissions.propagation.replace-confirm.owner",
                        confirm != 0 && confirmOwner == advanced,
                        $"owner=0x{confirmOwner:X}, advanced=0x{advanced:X}.");
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

    /// <summary>
    /// Opens Edit > Permissions on the sandbox key and Advanced, captures every window's control classes, ids, visibility
    /// and enabled states, and compares them with docs/parity/oracle/regedit-security.json. Both windows are cancelled and
    /// nothing is changed. See docs/failure-modes/permissions.md section 32.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    public void Permissions_SheetMatchesClassicOracle()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("permissions.oracle.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "permissions-oracle");
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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.oracle.sandbox.navigate");

            _ = ControlTestSupport.EnsureForeground(session);
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.oracle.sandbox.menu");
            driver.OpenMenu(MenuEdit);
            AutomationElement? permissionsItem = driver.WaitForElement("MenuCommand_" + CommandPermissions);
            _ = WaitForItemToSettle(permissionsItem);
            bool clicked = ClickElement(permissionsItem);

            nint sheet = clicked ? WaitForOwnedDialogOf(session.ProcessId, session.MainHwnd, TimeSpan.FromSeconds(10)) : 0;
            E2EResults.Record("permissions.oracle.sheet.open", sheet != 0, $"clicked={clicked}, sheet=0x{sheet:X}.");

            if (sheet != 0)
            {
                bool loaded = ControlTestSupport.WaitUntil(() => SheetHasLoaded(sheet), TimeSpan.FromSeconds(10));
                E2EResults.Note("permissions.oracle.sheet.loaded", $"sheet finished loading={loaded}.");
                CheckWindowAgainstOracle(config, "permissionsSheet", sheet, "permissions.oracle.sheet");

                bool advancedClicked = ClickButtonNamed(sheet, "Advanced");
                nint advanced = advancedClicked
                    ? WaitForOwnedDialogOf(session.ProcessId, sheet, TimeSpan.FromSeconds(10))
                    : 0;
                E2EResults.Record(
                    "permissions.oracle.advanced.open",
                    advanced != 0,
                    $"advancedClicked={advancedClicked}, advanced=0x{advanced:X}.");

                if (advanced != 0)
                {
                    // The dialog builds its list header after it appears. Wait for its controls to match the classic set,
                    // up to 10 s, so the comparison reads the loaded dialog. A set that never matches still fails.
                    _ = WaitForControlsToMatch(config, "advancedSecuritySettings", advanced, TimeSpan.FromSeconds(10));
                    CheckWindowAgainstOracle(config, "advancedSecuritySettings", advanced, "permissions.oracle.advanced");
                    bool inheritedFrom = ContainsText(advanced, InheritedFromText);
                    E2EResults.Record(
                        "permissions.inherit.source",
                        inheritedFrom,
                        $"'{InheritedFromText}' shown in the Inherited from column={inheritedFrom}.");
                    _ = TryCancel(session, advanced, out _);
                    _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(advanced), TimeSpan.FromSeconds(8));
                }

                _ = TryCancel(session, sheet, out _);
                _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(sheet), TimeSpan.FromSeconds(8));
            }

            E2EResults.Note("permissions.oracle.log", PermissionsLogLines(dataRoot));
            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    /// <summary>
    /// BEH-026: Advanced > "View effective access" on a sandbox key. The page must exist, name the current user, and mark
    /// at least one right for that user. Only Cancel is pressed, so nothing is changed. A missing control is named.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    public void Permissions_EffectiveAccessShowsResultsForTheCurrentUser()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("permissions.effective.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "permissions-effective");
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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.effective.sandbox.navigate");
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.effective.sandbox.menu");

            nint sheet = OpenPermissionsSheet(session, driver, "permissions.effective");
            if (sheet != 0)
            {
                nint advanced = OpenAdvanced(session, sheet, "permissions.effective");
                if (advanced != 0)
                {
                    CheckEffectiveAccess(session, advanced);
                    _ = TryCancel(session, advanced, out _);
                    _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(advanced), TimeSpan.FromSeconds(8));
                }

                _ = TryCancel(session, sheet, out _);
                _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(sheet), TimeSpan.FromSeconds(8));
            }

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    /// <summary>
    /// Selects the Effective Access tab of the Advanced window, clicks "View effective access" and reads the result. The
    /// result is the owned window that opens, or the Advanced window itself when the page is part of it. The page must
    /// name the current user and mark at least one right.
    /// </summary>
    private static void CheckEffectiveAccess(AppSession session, nint advanced)
    {
        bool tabSelected = SelectTab(advanced, EffectiveAccessTab);
        E2EResults.Record("permissions.effective.tab", tabSelected, $"the '{EffectiveAccessTab}' tab selected={tabSelected}.");
        bool picked = tabSelected && ChooseCurrentUser(session, advanced, EffectiveUserLink);
        E2EResults.Record("permissions.effective.user-picked", picked, $"the current user chosen with '{EffectiveUserLink}'={picked}.");
        bool clicked = picked && ClickButtonNamed(advanced, EffectiveAccessButton);
        E2EResults.Record("permissions.effective.button", clicked, $"'{EffectiveAccessButton}' clicked={clicked}.");
        if (!clicked)
        {
            return;
        }

        nint page = WaitForOwnedDialogOf(session.ProcessId, advanced, TimeSpan.FromSeconds(5));
        nint surface = page != 0 ? page : advanced;
        string account = CurrentAccountName();
        _ = ControlTestSupport.WaitUntil(
            () => TextOfWindow(surface).Contains(account, StringComparison.Ordinal) && AllowedRightCount(surface) > 0,
            TimeSpan.FromSeconds(10));
        E2EResults.Note(
            "permissions.effective.elements",
            $"page hwnd=0x{page:X} (0 means the Advanced window itself); elements: {DescribeElements(surface)}");

        bool named = TextOfWindow(surface).Contains(account, StringComparison.Ordinal);
        E2EResults.Record(
            "permissions.effective.user",
            named,
            $"the page names the current user '{account}'={named}.");

        int allowed = AllowedRightCount(surface);
        E2EResults.Record(
            "permissions.effective.rights",
            allowed > 0,
            $"rights the page reports as Allowed for the current user={allowed}; the sandbox key grants at least one.");
    }

    /// <summary>Selects a tab of the window by its name. The tab is clicked when it has no selection pattern.</summary>
    private static bool SelectTab(nint window, string name)
    {
        try
        {
            if (!ForegroundGuard.Ensure(window, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(window);
            AutomationElement? tab = root.FindFirstDescendant(
                cf => cf.ByName(name).And(cf.ByControlType(ControlType.TabItem)));
            if (tab is null)
            {
                return false;
            }

            if (tab.Patterns.SelectionItem.IsSupported)
            {
                tab.Patterns.SelectionItem.Pattern.Select();
            }
            else
            {
                tab.Click();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The account part of the current user's name, for example "jsmith" from "PC\jsmith".</summary>
    private static string CurrentAccountName()
    {
        string name = WindowsIdentity.GetCurrent().Name;
        int slash = name.LastIndexOf('\\');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    /// <summary>The names and the value patterns of every element in the window, one line per text.</summary>
    private static string TextOfWindow(nint window)
    {
        using var automation = new UIA3Automation();
        AutomationElement root = automation.FromHandle(window);
        var parts = new List<string>();
        foreach (AutomationElement element in root.FindAllDescendants())
        {
            parts.Add(SafeName(element));
            parts.Add(SafeValue(element));
        }

        return string.Join(" | ", parts);
    }

    /// <summary>Control type, name, value and toggle state of the first elements of the window, for the notes.</summary>
    private static string DescribeElements(nint window)
    {
        using var automation = new UIA3Automation();
        AutomationElement root = automation.FromHandle(window);
        return string.Join(
            " | ",
            root.FindAllDescendants()
                .Take(150)
                .Select(element => $"{SafeControlType(element)}:{SafeName(element)}={SafeValue(element)}[{SafeToggle(element)}]"));
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeToggle(AutomationElement element)
    {
        try
        {
            return element.Patterns.Toggle.IsSupported ? element.Patterns.Toggle.Pattern.ToggleState.Value.ToString() : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeControlType(AutomationElement element)
    {
        try
        {
            return element.ControlType.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>
    /// The Effective access list rows the page reports as Allowed, such as "Effective access Allowed, Permission Full Control".
    /// </summary>
    private static int AllowedRightCount(nint window)
    {
        try
        {
            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(window);
            return root
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Count(item => SafeName(item).StartsWith("Effective access Allowed", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Changes one Allow box on an explicit entry for Everyone, presses Apply, and reads the DACL back through the
    /// managed API. The original descriptor is restored in the finally block. The entry for the current user is
    /// checked before and after, so the change cannot remove the user's own access.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Mutating")]
    public void Permissions_AllowBoxChangeAppliesAndRestores()
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
            E2EResults.Record("permissions.allow.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        byte[]? originalDescriptor = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            originalDescriptor = AddEveryoneReadEntry(sandbox);
            SecurityIdentifier user = CurrentUserSid();
            int userRulesBefore = CountAllowRules(sandbox, user);
            bool everyoneBefore = HasEveryoneReadAllow(sandbox);
            E2EResults.Record(
                "permissions.allow.seeded",
                everyoneBefore,
                $"explicit Everyone Read Allow present before the change={everyoneBefore}, user allow rules={userRulesBefore}.");

            dataRoot = CreateFreshDataRoot(config, "permissions-allow");
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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.allow.sandbox.navigate");

            _ = ControlTestSupport.EnsureForeground(session);
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.allow.sandbox.menu");
            driver.OpenMenu(MenuEdit);
            AutomationElement? permissionsItem = driver.WaitForElement("MenuCommand_" + CommandPermissions);
            _ = WaitForItemToSettle(permissionsItem);
            bool clicked = ClickElement(permissionsItem);

            nint sheet = clicked ? WaitForOwnedDialogOf(session.ProcessId, session.MainHwnd, TimeSpan.FromSeconds(10)) : 0;
            E2EResults.Record("permissions.allow.sheet", sheet != 0, $"clicked={clicked}, sheet=0x{sheet:X}.");

            if (sheet != 0)
            {
                // The Allow Read box belongs to the selected entry. It is clicked only when Everyone is the one
                // selected, so the user's own entry can never be changed by this test.
                bool everyoneSelected = SelectOnlyNamedRow(sheet, "Everyone");
                bool allowToggled = everyoneSelected && ClickAnyNamed(sheet, "Allow Read");
                E2EResults.Record(
                    "permissions.allow.toggled",
                    allowToggled,
                    $"Everyone selected={everyoneSelected}, Allow Read clicked={allowToggled}.");

                string sddlBefore = SddlOfSandboxRoot(sandbox);
                bool applied = allowToggled && ClickAnyNamed(sheet, "Apply");
                string sddlAfter = SddlOfSandboxRoot(sandbox);
                E2EResults.Record("permissions.allow.sddl", true, $"before={sddlBefore}; after={sddlAfter}.");
                E2EResults.Record("permissions.allow.applied", applied, $"Apply clicked={applied}.");

                bool changed = applied && ControlTestSupport.WaitUntil(
                    () => !HasEveryoneReadAllow(sandbox),
                    TimeSpan.FromSeconds(10));
                E2EResults.Record(
                    "permissions.allow.dacl-changed",
                    changed,
                    $"explicit Everyone Read Allow removed={changed}.");

                int userRulesAfter = CountAllowRules(sandbox, user);
                E2EResults.Record(
                    "permissions.allow.user-entries-kept",
                    userRulesAfter == userRulesBefore,
                    $"user allow rules before={userRulesBefore}, after={userRulesAfter}.");

                _ = TryCancel(session, sheet, out _);
                _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(sheet), TimeSpan.FromSeconds(8));
            }

            FailIfNeeded();
        }
        finally
        {
            if (originalDescriptor is not null)
            {
                RestoreDescriptor(sandbox, originalDescriptor);
            }

            bool restored = originalDescriptor is null || HasEveryoneReadAllowAfterRestore(sandbox, originalDescriptor);
            E2EResults.Record("permissions.allow.restored", restored, $"original DACL restored={restored}.");
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    /// <summary>Adds an explicit Allow Read entry for Everyone on the run root and returns the original descriptor.</summary>
    private static byte[] AddEveryoneReadEntry(SandboxRegistry sandbox)
    {
        using RegistryKey root = sandbox.CreateSubKey(string.Empty);
        RegistrySecurity original = root.GetAccessControl(AccessControlSections.Access);
        byte[] originalBinary = original.GetSecurityDescriptorBinaryForm();

        RegistrySecurity seeded = root.GetAccessControl(AccessControlSections.Access);
        seeded.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            RegistryRights.ReadKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        root.SetAccessControl(seeded);
        return originalBinary;
    }

    private static void RestoreDescriptor(SandboxRegistry sandbox, byte[] originalBinary)
    {
        // Access only: the full descriptor holds the SACL, which an unelevated process cannot persist.
        var restore = new RegistrySecurity();
        restore.SetSecurityDescriptorBinaryForm(originalBinary, AccessControlSections.Access);
        using RegistryKey? root = OpenSandboxRoot(sandbox);
        root?.SetAccessControl(restore);
    }

    private static bool HasEveryoneReadAllowAfterRestore(SandboxRegistry sandbox, byte[] originalBinary)
    {
        var original = new RegistrySecurity();
        original.SetSecurityDescriptorBinaryForm(originalBinary, AccessControlSections.Access);
        bool originalHadEveryone = original.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        return originalHadEveryone == HasEveryoneReadAllow(sandbox);
    }

    private static string SddlOfSandboxRoot(SandboxRegistry sandbox)
    {
        using RegistryKey? root = OpenSandboxRoot(sandbox);
        return root is null
            ? "<none>"
            : root.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    private static RegistryKey? OpenSandboxRoot(SandboxRegistry sandbox, bool takeOwnership = false)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        // Only the rights the test needs. A DACL that denies KEY_WRITE must still be restorable.
        // ReadWriteSubTree only marks the managed key writable, so SetAccessControl is allowed. The access rights asked
        // of the system stay at ReadPermissions and ChangePermissions.
        return hkcu.OpenSubKey(
            sandbox.RunRootPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions |
                (takeOwnership ? RegistryRights.TakeOwnership : 0));
    }

    private static bool HasEveryoneReadAllow(SandboxRegistry sandbox)
    {
        using RegistryKey? root = OpenSandboxRoot(sandbox);
        if (root is null)
        {
            return false;
        }

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        return root.GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(everyone) &&
                (rule.RegistryRights & RegistryRights.ReadKey) == RegistryRights.ReadKey);
    }

    private static int CountAllowRules(SandboxRegistry sandbox, SecurityIdentifier user)
    {
        using RegistryKey? root = OpenSandboxRoot(sandbox);
        if (root is null)
        {
            return -1;
        }

        return root.GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Count(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(user));
    }

    /// <summary>
    /// Selects the list row with this name and confirms with UI Automation that it is the only selected row.
    /// Returns false when the row is missing or the selection is any other row.
    /// </summary>
    private static bool SelectOnlyNamedRow(nint dialog, string name)
    {
        try
        {
            if (!ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? row = root.FindFirstDescendant(
                cf => cf.ByName(name).And(cf.ByControlType(ControlType.ListItem)));
            if (row is null)
            {
                return false;
            }

            row.Click();
            AutomationElement[] selected = root
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(item => item.Patterns.SelectionItem.PatternOrDefault?.IsSelected.Value == true)
                .ToArray();
            return selected.Length == 1 && string.Equals(selected[0].Name, name, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Clicks the first visible control with this name, whatever its control type.</summary>
    private static bool ClickAnyNamed(nint dialog, string name)
    {
        try
        {
            if (!ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? element = root.FindFirstDescendant(cf => cf.ByName(name));

            // A hidden or off-screen control has no place to click, and a click at its bounds would hit another control.
            if (element is null || !IsEnabled(element) || element.Properties.IsOffscreen.Value)
            {
                return false;
            }

            element.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the control classes, ids, visibility and enabled state of a window and its descendants.</summary>
    private static List<string> ControlSignatures(nint window)
    {
        // The window itself is not a control of the sheet. Classic lists only its descendants.
        var signatures = new List<string>();
        _ = PermissionsNative.EnumChildWindows(
            window,
            (hwnd, lParam) =>
            {
                _ = lParam;
                signatures.Add(ControlSignature(hwnd));
                return true;
            },
            IntPtr.Zero);
        return signatures;
    }

    private static string ControlSignature(nint hwnd) =>
        string.Join(
            "|",
            PermissionsNative.ClassOf(hwnd),
            PermissionsNative.GetDlgCtrlID(hwnd).ToString(System.Globalization.CultureInfo.InvariantCulture),
            PermissionsNative.IsWindowVisible(hwnd) ? "true" : "false",
            PermissionsNative.IsWindowEnabled(hwnd) ? "true" : "false");

    private static int TabCountOf(nint window)
    {
        int count = 0;
        _ = PermissionsNative.EnumChildWindows(
            window,
            (hwnd, lParam) =>
            {
                _ = lParam;
                if (!string.Equals(PermissionsNative.ClassOf(hwnd), "SysTabControl32", StringComparison.Ordinal))
                {
                    return true;
                }

                count = (int)PermissionsNative.SendMessage(hwnd, PermissionsNative.TcmGetItemCount, 0, 0);
                return false;
            },
            IntPtr.Zero);
        return count;
    }

    private static bool WaitForControlsToMatch(E2EConfig config, string windowName, nint window, TimeSpan timeout)
    {
        (_, List<string> expected) = LoadOracleWindow(config, windowName);
        return ControlTestSupport.WaitUntil(
            () =>
            {
                (List<string> missing, List<string> extra) = DiffMultisets(expected, ControlSignatures(window));
                return missing.Count == 0 && extra.Count == 0;
            },
            timeout);
    }

    private static void CheckWindowAgainstOracle(E2EConfig config, string windowName, nint window, string prefix)
    {
        (int expectedTabs, List<string> expected) = LoadOracleWindow(config, windowName);
        List<string> actual = ControlSignatures(window);
        int tabs = TabCountOf(window);
        E2EResults.Record(
            prefix + ".tabs",
            tabs == expectedTabs,
            $"tabCount={tabs}, classic={expectedTabs}.");

        (List<string> missing, List<string> extra) = DiffMultisets(expected, actual);
        E2EResults.Record(
            prefix + ".controls",
            missing.Count == 0 && extra.Count == 0,
            $"controls={actual.Count}, classic={expected.Count}, missing=[{string.Join("; ", missing)}], extra=[{string.Join("; ", extra)}].");
    }

    private static (int TabCount, List<string> Signatures) LoadOracleWindow(E2EConfig config, string windowName)
    {
        string path = Path.Combine(config.RepoRoot, "docs", "parity", "oracle", "regedit-security.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement window = document.RootElement.GetProperty(windowName);

        var signatures = new List<string>();
        foreach (JsonElement control in window.GetProperty("controls").EnumerateArray())
        {
            signatures.Add(string.Join(
                "|",
                control.GetProperty("class").GetString(),
                control.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                control.GetProperty("visible").GetBoolean() ? "true" : "false",
                control.GetProperty("enabled").GetBoolean() ? "true" : "false"));
        }

        return (window.GetProperty("tabCount").GetInt32(), signatures);
    }

    private static (List<string> Missing, List<string> Extra) DiffMultisets(List<string> expected, List<string> actual)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string item in expected)
        {
            counts[item] = counts.GetValueOrDefault(item) + 1;
        }

        var extra = new List<string>();
        foreach (string item in actual)
        {
            if (counts.TryGetValue(item, out int remaining) && remaining > 0)
            {
                counts[item] = remaining - 1;
            }
            else
            {
                extra.Add(item);
            }
        }

        var missing = new List<string>();
        foreach ((string item, int remaining) in counts)
        {
            for (int index = 0; index < remaining; index += 1)
            {
                missing.Add(item);
            }
        }

        return (missing, extra);
    }

    // Owner recursion, auditing without SeSecurityPrivilege and a remote key (permissions.md sections 34, 35 and 37).

    private const string OwnerRecurseFragment = "Replace owner on subcontainers and objects";
    private const string AuditingTabName = "Auditing";
    private const int SheetAdvancedControlId = 1005;
    private const int SheetLoadingControlId = 6024;
    private const string AuditNoPermissionText = "You do not have permission to view or edit this object's audit settings.";
    private const string SecurityEditorLogFragment = "Registry permissions SetSecurity";

    /// <summary>Opens Edit > Permissions on the selected key and returns the sheet, or 0.</summary>
    private static nint OpenPermissionsSheet(AppSession session, EditingDriver driver, string prefix)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        driver.OpenMenu(MenuEdit);
        AutomationElement? permissionsItem = driver.WaitForElement("MenuCommand_" + CommandPermissions);
        _ = WaitForItemToSettle(permissionsItem);
        bool clicked = ClickElement(permissionsItem);
        nint sheet = clicked ? WaitForOwnedDialogOf(session.ProcessId, session.MainHwnd, TimeSpan.FromSeconds(10)) : 0;
        E2EResults.Record(prefix + ".sheet", sheet != 0, $"clicked={clicked}, sheet=0x{sheet:X}.");
        return sheet;
    }

    private static nint OpenAdvanced(AppSession session, nint sheet, string prefix)
    {
        bool advancedClicked = ClickButtonNamed(sheet, "Advanced");
        nint advanced = advancedClicked
            ? WaitForOwnedDialogOf(session.ProcessId, sheet, TimeSpan.FromSeconds(10))
            : 0;
        E2EResults.Record(prefix + ".advanced.open", advanced != 0, $"advancedClicked={advancedClicked}, advanced=0x{advanced:X}.");
        return advanced;
    }

    private static SecurityIdentifier? OwnerOf(SandboxRegistry sandbox, string relativePath)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        string path = relativePath.Length == 0 ? sandbox.RunRootPath : sandbox.RunRootPath + @"\" + relativePath;
        using RegistryKey? key = hkcu.OpenSubKey(path, RegistryRights.ReadPermissions);
        return key?.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
    }

    private static void RestoreOwner(SandboxRegistry sandbox, string relativePath, SecurityIdentifier owner)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        string path = relativePath.Length == 0 ? sandbox.RunRootPath : sandbox.RunRootPath + @"\" + relativePath;
        // ReadWriteSubTree marks the managed key writable, which SetAccessControl requires.
        using RegistryKey? key = hkcu.OpenSubKey(
            path,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.TakeOwnership | RegistryRights.ReadPermissions);
        if (key is null)
        {
            return;
        }

        var restore = new RegistrySecurity();
        restore.SetOwner(owner);
        key.SetAccessControl(restore);
    }

    /// <summary>
    /// Clicks "Change" next to the owner and picks the current user in the "Select User or Group" dialog. aclui shows
    /// "Replace owner on subcontainers and objects" only once an owner has been chosen. The owner stays the same user.
    /// </summary>
    private static bool ChooseCurrentUser(AppSession session, nint advanced, string linkName)
    {
        if (!ClickAnyNamed(advanced, linkName))
        {
            E2EResults.Note("permissions.picker", $"the {linkName} link was not found.");
            return false;
        }

        nint picker = WaitForOwnedDialogOf(session.ProcessId, advanced, TimeSpan.FromSeconds(10));
        if (picker == 0)
        {
            E2EResults.Note("permissions.picker", "the Select User or Group dialog did not open.");
            return false;
        }

        string stage = "focus";
        try
        {
            if (!ForegroundGuard.Ensure(picker, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                E2EResults.Note("permissions.picker", "the picker never got the foreground.");
                _ = ClickButtonNamed(picker, "Cancel");
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(picker);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            stage = "type";
            FlaUI.Core.Input.Keyboard.Type(identity.Name);
            stage = "ok";
            AutomationElement? okButton = root.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("OK")));
            E2EResults.Note(
                "permissions.picker",
                $"typed '{identity.Name}', OK enabled={okButton?.IsEnabled}.");
            if (okButton is null || !okButton.IsEnabled)
            {
                _ = ClickButtonNamed(picker, "Cancel");
                return false;
            }

            okButton.AsButton().Invoke();
        }
        catch (Exception ex)
        {
            E2EResults.Note("permissions.picker", $"failed at {stage}: {ex.GetType().Name}: {ex.Message}");
            _ = ClickButtonNamed(picker, "Cancel");
            return false;
        }

        bool closed = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(picker), TimeSpan.FromSeconds(15));
        if (!closed)
        {
            _ = ClickButtonNamed(picker, "Cancel");
        }

        E2EResults.Note("permissions.picker", $"picker closed after OK={closed}.");
        return closed;
    }

    /// <summary>The "Registry permissions" lines of the app log, for the notes of a test.</summary>
    private static string PermissionsLogLines(string? dataRoot)
    {
        if (dataRoot is null)
        {
            return string.Empty;
        }

        string[] files = Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories).ToArray();
        string[] lines = AppLogText(dataRoot).Split((char)10)
            .Where(line => line.Contains("Registry permissions", StringComparison.Ordinal))
            .ToArray();
        return $"files=[{string.Join(", ", files.Select(f => Path.GetRelativePath(dataRoot, f)))}] lines=[{string.Join(" || ", lines)}]";
    }

    /// <summary>
    /// The sheet is loaded once the Advanced button is enabled and the loading indicator (control 6024) is hidden.
    /// A capture before that sees the transient state, which differs from classic.
    /// </summary>
    private static bool SheetHasLoaded(nint sheet)
    {
        bool advancedEnabled = false;
        bool indicatorVisible = false;
        _ = PermissionsNative.EnumChildWindows(
            sheet,
            (child, _) =>
            {
                int id = PermissionsNative.GetDlgCtrlID(child);
                if (id == SheetAdvancedControlId)
                {
                    advancedEnabled = PermissionsNative.IsWindowEnabled(child);
                }
                else if (id == SheetLoadingControlId)
                {
                    indicatorVisible = PermissionsNative.IsWindowVisible(child);
                }

                return true;
            },
            0);
        return advancedEnabled && !indicatorVisible;
    }

    /// <summary>The text of the app log files in the data root, for the SetSecurity trace lines.</summary>
    private static string AppLogText(string dataRoot)
    {
        var text = new System.Text.StringBuilder();
        string logDirectory = Path.Combine(dataRoot, "regedit", "logs");
        if (!Directory.Exists(logDirectory))
        {
            return string.Empty;
        }

        foreach (string file in Directory.EnumerateFiles(logDirectory, "log-*.txt"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                _ = text.Append(reader.ReadToEnd());
            }
            catch (IOException)
            {
                // A log file that is still being written is read on the next call.
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Section 35: Advanced OK with "Replace owner on subcontainers and objects" on a three-key tree. An unelevated user
    /// can only set an owner it already holds, so no key changes hands. The test proves that the editor sent the
    /// owner with the recurse request (the app log line), that no error reached the user, and that every key still
    /// has the current user as owner. Each owner is restored in the finally block.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Mutating")]
    public void Permissions_OwnerRecursionSetsTheOwnerOfEverySubkey()
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
            E2EResults.Record("permissions.owner.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        string[] tree = ["OwnerA", @"OwnerA\OwnerB", "OwnerC"];
        var sandbox = new SandboxRegistry(config);
        SecurityIdentifier user = CurrentUserSid();
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
            {
                root.SetValue(SeedValueName, "permissions", RegistryValueKind.String);
            }

            foreach (string key in tree)
            {
                sandbox.CreateKey(key);
            }

            dataRoot = CreateFreshDataRoot(config, "permissions-owner");
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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.owner.sandbox.navigate");
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.owner.sandbox.menu");

            nint sheet = OpenPermissionsSheet(session, driver, "permissions.owner");
            if (sheet != 0)
            {
                nint advanced = OpenAdvanced(session, sheet, "permissions.owner");
                if (advanced != 0)
                {
                    bool changed = ChooseCurrentUser(session, advanced, "Change");
                    E2EResults.Record("permissions.owner.change", changed, $"the current user chosen as owner={changed}.");
                    bool recurse = false;
                    string recurseReason = "the owner was not chosen";
                    if (changed)
                    {
                        recurse = SetCheckBox(advanced, OwnerRecurseFragment, true, out recurseReason);
                    }

                    E2EResults.Record(
                        "permissions.owner.recurse-checked",
                        recurse,
                        $"'{OwnerRecurseFragment}' checked={recurse} ({recurseReason}).");

                    bool advancedOk = recurse && ClickButtonNamed(advanced, "OK");
                    E2EResults.Record("permissions.owner.advanced.ok", advancedOk, $"clicked={advancedOk}.");
                    bool advancedGone = advancedOk && ControlTestSupport.WaitUntil(
                        () => !PermissionsNative.IsWindow(advanced),
                        TimeSpan.FromSeconds(15));
                    E2EResults.Record("permissions.owner.advanced.closed", advancedGone, $"gone={advancedGone}.");
                }

                bool sheetOk = ClickButtonNamed(sheet, "OK");
                bool sheetClosed = ControlTestSupport.WaitUntil(
                    () => !PermissionsNative.IsWindow(sheet),
                    TimeSpan.FromSeconds(15));
                nint error = FindOwnedDialog(session.ProcessId, session.MainHwnd);
                E2EResults.Record(
                    "permissions.owner.sheet.ok",
                    sheetOk && sheetClosed && error == 0,
                    $"okClicked={sheetOk}, closed={sheetClosed}, error window=0x{error:X}.");

                string appLog = AppLogText(dataRoot);
                bool sent = appLog.Contains(SecurityEditorLogFragment, StringComparison.Ordinal) &&
                    appLog.Contains("ownerRecurse=True", StringComparison.Ordinal);
                E2EResults.Record(
                    "permissions.owner.recurse-sent",
                    sent,
                    $"the app log shows a SetSecurity with the owner and the recurse request={sent}.");
            }

            var owners = new List<string>();
            bool allUser = true;
            foreach (string key in new[] { string.Empty }.Concat(tree))
            {
                SecurityIdentifier? owner = OwnerOf(sandbox, key);
                allUser &= owner is not null && user.Equals(owner);
                owners.Add($"{(key.Length == 0 ? "<root>" : key)}={owner?.Value ?? "<missing>"}");
            }

            E2EResults.Record(
                "permissions.owner.children",
                allUser,
                $"owner of every key is the current user={allUser}: {string.Join("; ", owners)}.");

            FailIfNeeded();
        }
        finally
        {
            foreach (string key in new[] { string.Empty }.Concat(tree))
            {
                try
                {
                    if (OwnerOf(sandbox, key) is not { } current || !user.Equals(current))
                    {
                        RestoreOwner(sandbox, key, user);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    E2EResults.Record("permissions.owner.restored." + key, false, ex.Message);
                }
            }

            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    /// <summary>
    /// Section 37: without SeSecurityPrivilege the Auditing tab cannot read the SACL. The test opens the tab and checks
    /// that the editor stays alive, that the failed read reaches aclui (the app log names error 1314), and records
    /// what the system shows. It never presses a Continue button, because that would ask for elevation. The SACL write
    /// error is unreachable: aclui sends no SACL it could not read, so the write path has no unelevated test.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    public void Permissions_AuditingTabWithoutSecurityPrivilegeShowsTheSystemState()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("permissions.audit.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        if (IsElevated())
        {
            Assert.Inconclusive("Elevated: the unelevated no-privilege state cannot be observed.");
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "permissions-audit");
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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "permissions.audit.sandbox.navigate");
            E2EGuards.RequireSandboxSelection(session, sandbox, "permissions.audit.sandbox.menu");

            nint sheet = OpenPermissionsSheet(session, driver, "permissions.audit");
            if (sheet != 0)
            {
                nint advanced = OpenAdvanced(session, sheet, "permissions.audit");
                if (advanced != 0)
                {
                    bool tabClicked = ClickAnyNamed(advanced, AuditingTabName);
                    E2EResults.Record("permissions.audit.tab", tabClicked, $"'{AuditingTabName}' clicked={tabClicked}.");

                    // The system shows its own state in the tab or in a message box owned by the Advanced window.
                    Thread.Sleep(1500);
                    nint message = FindOwnedDialog(session.ProcessId, advanced);
                    string state = DescribeVisibleText(message != 0 ? message : advanced);
                    E2EResults.Note("permissions.audit.state", $"message window=0x{message:X}; visible text: {state}");
                    try
                    {
                        E2EResults.Note(
                            "permissions.audit.screenshot",
                            $"saved '{E2EResults.CaptureScreenshot("permissions.audit.state", session)}'.");
                    }
                    catch (Exception ex)
                    {
                        E2EResults.Note("permissions.audit.screenshot", $"not saved: {ex.GetType().Name}: {ex.Message}");
                    }

                    bool alive = PermissionsNative.IsWindow(advanced) && !RemoteWindowsHung(session);
                    E2EResults.Record("permissions.audit.alive", alive, $"the Advanced window is open and the app responds={alive}.");

                    string appLog = AppLogText(dataRoot);
                    bool noPermissionShown = state.Contains(AuditNoPermissionText, StringComparison.Ordinal);
                    E2EResults.Record(
                        "permissions.audit.no-permission-text",
                        noPermissionShown,
                        $"the system shows '{AuditNoPermissionText}'={noPermissionShown}.");

                    string[] logLines = appLog.Split('\n')
                        .Where(line => line.Contains("Registry permissions", StringComparison.Ordinal))
                        .ToArray();
                    E2EResults.Note("permissions.audit.log", string.Join(" || ", logLines));

                    if (message != 0)
                    {
                        _ = TryCancel(session, message, out _);
                    }

                    _ = TryCancel(session, advanced, out _);
                    _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(advanced), TimeSpan.FromSeconds(8));
                }

                _ = TryCancel(session, sheet, out _);
                _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(sheet), TimeSpan.FromSeconds(8));
            }

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static bool RemoteWindowsHung(AppSession session) => Bwc.E2E.Remote.RemoteWindows.IsHung(session.MainHwnd);

    /// <summary>The names of the visible UI Automation elements of a window, for a note.</summary>
    private static string DescribeVisibleText(nint window)
    {
        try
        {
            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(window);
            return string.Join(
                " | ",
                root.FindAllDescendants()
                    .Where(element => !element.Properties.IsOffscreen.ValueOrDefault)
                    .Select(element => element.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .Take(60));
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.GetType().Name}>";
        }
    }

    /// <summary>
    /// Section 34: Edit > Permissions on a key under the loopback connection, and an Apply that changes the DACL of
    /// HKEY_USERS\SID\Software\BWC-E2E\run-id through the connection. The Everyone Read entry is removed, the user's
    /// entries are kept, and the original DACL is restored. A connect that the system refuses (the RemoteRegistry
    /// service is stopped) is a note and the test is Inconclusive, but only while RemoteRegistry is not Running. Any
    /// other refusal is a failed check. The test never starts or configures the service.
    /// </summary>
    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Permissions_RemoteKeyApplyChangesTheDaclThroughTheConnection()
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
            E2EResults.Record("permissions.remote.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        byte[]? originalDescriptor = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            originalDescriptor = AddEveryoneReadEntry(sandbox);
            SecurityIdentifier user = CurrentUserSid();
            int userRulesBefore = CountAllowRules(sandbox, user);
            bool everyoneBefore = HasEveryoneReadAllow(sandbox);
            E2EResults.Record(
                "permissions.remote.seeded",
                everyoneBefore,
                $"explicit Everyone Read Allow present before the change={everyoneBefore}, user allow rules={userRulesBefore}.");

            dataRoot = CreateFreshDataRoot(config, "permissions-remote");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                [PermissionsRemoteSupport.ConnectTargetVariable] = PermissionsRemoteSupport.LoopbackComputer,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            PermissionsRemoteSupport.ConnectResult connect = PermissionsRemoteSupport.ConnectLoopback(session, driver);
            if (!connect.Connected)
            {
                if (connect.RemoteError is not null)
                {
                    // A note and Inconclusive only while RemoteRegistry is not Running. The test never starts it.
                    RemoteRegistryService.RefusedConnect(
                        "permissions.remote.connect",
                        "permissions.remote.not-run",
                        connect.RemoteError);
                }

                E2EResults.Record("permissions.remote.connect", false, connect.Detail);
                Assert.Fail(connect.Detail);
            }

            E2EResults.Record("permissions.remote.connect", true, "connected to 127.0.0.1.");

            string sid = user.Value;
            string remoteKey = $@"{PermissionsRemoteSupport.LoopbackNode}\HKEY_USERS\{sid}\Software\BWC-E2E\{config.RunId}";
            bool navigated = PermissionsRemoteSupport.NavigateAddress(session, remoteKey, config.RunId);
            string address = PermissionsRemoteSupport.AddressText(session);
            bool inside = E2EGuards.IsWithin(address, remoteKey);
            E2EResults.Record(
                "permissions.remote.navigate",
                navigated && inside,
                $"navigated={navigated}, address bar='{address}', required '{remoteKey}', inside={inside}.");
            if (!(navigated && inside))
            {
                Assert.Fail($"Refusing input: the address bar shows '{address}', not '{remoteKey}'.");
            }

            nint sheet = OpenPermissionsSheet(session, driver, "permissions.remote");
            if (sheet != 0)
            {
                E2EResults.Note("permissions.remote.sheet.title", $"title='{PermissionsNative.TitleOf(sheet)}'.");
                bool everyoneSelected = SelectOnlyNamedRow(sheet, "Everyone");
                bool allowToggled = everyoneSelected && ClickAnyNamed(sheet, "Allow Read");
                E2EResults.Record(
                    "permissions.remote.toggled",
                    allowToggled,
                    $"Everyone selected={everyoneSelected}, Allow Read clicked={allowToggled}.");

                bool applied = allowToggled && ClickAnyNamed(sheet, "Apply");
                bool changed = applied && ControlTestSupport.WaitUntil(
                    () => !HasEveryoneReadAllow(sandbox),
                    TimeSpan.FromSeconds(15));
                E2EResults.Record(
                    "permissions.remote.dacl-changed",
                    changed,
                    $"Apply clicked={applied}, explicit Everyone Read Allow removed through the connection={changed}.");

                int userRulesAfter = CountAllowRules(sandbox, user);
                E2EResults.Record(
                    "permissions.remote.user-entries-kept",
                    userRulesAfter == userRulesBefore,
                    $"user allow rules before={userRulesBefore}, after={userRulesAfter}.");

                _ = TryCancel(session, sheet, out _);
                _ = ControlTestSupport.WaitUntil(() => !PermissionsNative.IsWindow(sheet), TimeSpan.FromSeconds(8));
            }

            FailIfNeeded();
        }
        finally
        {
            if (originalDescriptor is not null)
            {
                RestoreDescriptor(sandbox, originalDescriptor);
            }

            bool restored = originalDescriptor is null || HasEveryoneReadAllowAfterRestore(sandbox, originalDescriptor);
            E2EResults.Record("permissions.remote.restored", restored, $"original DACL restored={restored}.");
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    // Elevated tests: they report Inconclusive unless the process is elevated (permissions.md section 39).

    private const string SecurityPrivilege = "SeSecurityPrivilege";
    private const string RestorePrivilege = "SeRestorePrivilege";
    private const string TakeOwnershipPrivilege = "SeTakeOwnershipPrivilege";
    private const string NotElevated = "Not elevated: the elevated security test runs only through the elevated runner.";

    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Elevated")]
    public void Permissions_ElevatedAddAndRemoveAudit()
    {
        if (!IsElevated())
        {
            Assert.Inconclusive(NotElevated);
        }

        E2EConfig config = GuiGate.Config;
        var sandbox = new SandboxRegistry(config);
        var rule = new RegistryAuditRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            RegistryRights.ReadKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AuditFlags.Success);

        try
        {
            sandbox.Cleanup();
            using TokenPrivileges privileges = TokenPrivileges.Enable(SecurityPrivilege);
            using RegistryKey root = sandbox.CreateSubKey(string.Empty);

            RegistrySecurity audited = root.GetAccessControl(AccessControlSections.Audit);
            audited.AddAuditRule(rule);
            root.SetAccessControl(audited);

            bool added = HasAudit(root, rule);
            E2EResults.Record("permissions.elevated.audit.added", added, $"Everyone Read success audit present={added}.");

            RegistrySecurity removing = root.GetAccessControl(AccessControlSections.Audit);
            removing.RemoveAuditRuleSpecific(rule);
            root.SetAccessControl(removing);

            bool removed = !HasAudit(root, rule);
            E2EResults.Record("permissions.elevated.audit.removed", removed, $"Everyone Read success audit absent={removed}.");
        }
        finally
        {
            RemoveAuditIfPresent(sandbox, rule);
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    [TestMethod]
    [TestCategory("Permissions")]
    [TestCategory("Elevated")]
    public void Permissions_ElevatedOwnerToAdministratorsAndBack()
    {
        if (!IsElevated())
        {
            Assert.Inconclusive(NotElevated);
        }

        E2EConfig config = GuiGate.Config;
        var sandbox = new SandboxRegistry(config);
        SecurityIdentifier? originalOwner = null;

        try
        {
            sandbox.Cleanup();
            using TokenPrivileges privileges = TokenPrivileges.Enable(RestorePrivilege, TakeOwnershipPrivilege);
            using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
            {
                originalOwner = root.GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

                RegistrySecurity toAdministrators = root.GetAccessControl(AccessControlSections.Owner);
                toAdministrators.SetOwner(administrators);
                root.SetAccessControl(toAdministrators);

                SecurityIdentifier? owner = root.GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                E2EResults.Record(
                    "permissions.elevated.owner.administrators",
                    owner is not null && administrators.Equals(owner),
                    $"owner after the change={owner?.Value}.");
            }

            using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
            {
                RegistrySecurity back = root.GetAccessControl(AccessControlSections.Owner);
                SecurityIdentifier restoreTo = originalOwner ?? throw new InvalidOperationException("The original owner was not read.");
                back.SetOwner(restoreTo);
                root.SetAccessControl(back);

                SecurityIdentifier? owner = root.GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                E2EResults.Record(
                    "permissions.elevated.owner.restored",
                    owner is not null && restoreTo.Equals(owner),
                    $"owner after the restore={owner?.Value}.");
            }
        }
        finally
        {
            RestoreOwnerIfChanged(sandbox, originalOwner);
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static bool HasAudit(RegistryKey root, RegistryAuditRule rule) =>
        root.GetAccessControl(AccessControlSections.Audit)
            .GetAuditRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<RegistryAuditRule>()
            .Any(existing => existing.IdentityReference.Equals(rule.IdentityReference) &&
                (existing.AuditFlags & AuditFlags.Success) != 0 &&
                (existing.RegistryRights & RegistryRights.ReadKey) == RegistryRights.ReadKey);

    private static void RemoveAuditIfPresent(SandboxRegistry sandbox, RegistryAuditRule rule)
    {
        try
        {
            using TokenPrivileges privileges = TokenPrivileges.Enable(SecurityPrivilege);
            using RegistryKey? root = OpenSandboxRoot(sandbox);
            if (root is null || !HasAudit(root, rule))
            {
                return;
            }

            RegistrySecurity cleanup = root.GetAccessControl(AccessControlSections.Audit);
            cleanup.RemoveAuditRuleSpecific(rule);
            root.SetAccessControl(cleanup);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            E2EResults.Record("permissions.elevated.audit.cleanup", false, ex.Message);
        }
    }

    private static void RestoreOwnerIfChanged(SandboxRegistry sandbox, SecurityIdentifier? originalOwner)
    {
        if (originalOwner is null)
        {
            return;
        }

        try
        {
            using TokenPrivileges privileges = TokenPrivileges.Enable(RestorePrivilege, TakeOwnershipPrivilege);
            using RegistryKey? root = OpenSandboxRoot(sandbox, takeOwnership: true);
            if (root is null)
            {
                return;
            }

            RegistrySecurity restore = root.GetAccessControl(AccessControlSections.Owner);
            if (originalOwner.Equals(restore.GetOwner(typeof(SecurityIdentifier))))
            {
                return;
            }

            restore.SetOwner(originalOwner);
            root.SetAccessControl(restore);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            E2EResults.Record("permissions.elevated.owner.cleanup", false, ex.Message);
        }
    }

    /// <summary>
    /// Enables named privileges on this process token and disables them again on dispose. A privilege the token does
    /// not hold fails the enable with ERROR_NOT_ALL_ASSIGNED, which throws.
    /// </summary>
    private sealed class TokenPrivileges : IDisposable
    {
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x00000002;
        private const int ErrorNotAllAssigned = 1300;

        private readonly nint _token;
        private readonly LuidAndAttributes[] _previous;
        private bool _disposed;

        private TokenPrivileges(nint token, LuidAndAttributes[] previous)
        {
            _token = token;
            _previous = previous;
        }

        public static TokenPrivileges Enable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
            {
                throw new InvalidOperationException($"OpenProcessToken failed with {Marshal.GetLastWin32Error()}.");
            }

            var previous = new LuidAndAttributes[names.Length];
            try
            {
                for (int index = 0; index < names.Length; index += 1)
                {
                    if (!LookupPrivilegeValue(null, names[index], out long luid))
                    {
                        throw new InvalidOperationException($"LookupPrivilegeValue({names[index]}) failed.");
                    }

                    var state = new TokenPrivilegeState { PrivilegeCount = 1, Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled } };
                    if (!AdjustTokenPrivileges(token, false, ref state, 0, nint.Zero, nint.Zero))
                    {
                        throw new InvalidOperationException($"AdjustTokenPrivileges({names[index]}) failed.");
                    }

                    if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                    {
                        throw new InvalidOperationException($"The token does not hold {names[index]}.");
                    }

                    previous[index] = new LuidAndAttributes { Luid = luid, Attributes = 0 };
                }

                return new TokenPrivileges(token, previous);
            }
            catch
            {
                // Privileges enabled before the failure stay on the process token, so they are disabled here.
                foreach (LuidAndAttributes privilege in previous.Where(entry => entry.Luid != 0))
                {
                    var state = new TokenPrivilegeState { PrivilegeCount = 1, Privilege = privilege };
                    _ = AdjustTokenPrivileges(token, false, ref state, 0, nint.Zero, nint.Zero);
                }

                _ = CloseHandle(token);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (LuidAndAttributes privilege in _previous)
            {
                var state = new TokenPrivilegeState { PrivilegeCount = 1, Privilege = privilege };
                _ = AdjustTokenPrivileges(_token, false, ref state, 0, nint.Zero, nint.Zero);
            }

            _ = CloseHandle(_token);
        }

        // LUID is two 32-bit fields in Windows, so these structs are packed at 4 bytes: the LUID sits at offset 4 of
        // TOKEN_PRIVILEGES. A plain long would be aligned to 8 and Windows would read a LUID that names no privilege.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct LuidAndAttributes
        {
            public long Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivilegeState
        {
            public uint PrivilegeCount;
            public LuidAndAttributes Privilege;
        }

#pragma warning disable SYSLIB1054 // The small P/Invoke surface does not justify LibraryImport plumbing.
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            nint token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAll,
            ref TokenPrivilegeState newState,
            int bufferLength,
            nint previousState,
            nint returnLength);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054
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

            Keyboard.Type(VirtualKeyShort.ESCAPE);
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

    /// <summary>
    /// Sets the check box whose name holds <paramref name="nameFragment"/>. <paramref name="reason"/> tells apart a
    /// foreground that could not be taken, a box that does not exist and a box that did not take the value.
    /// </summary>
    private static bool SetCheckBox(nint dialog, string nameFragment, bool value, out string reason)
    {
        try
        {
            if (!ForegroundGuard.Ensure(dialog, "dialog.permissions.foreground", TimeSpan.FromSeconds(2)))
            {
                reason = "no foreground";
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement[] boxes = root.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
            AutomationElement? box = boxes.FirstOrDefault(
                element => element.Name?.Contains(nameFragment, StringComparison.Ordinal) == true);
            if (box is null)
            {
                reason = $"not found among {boxes.Length} check boxes: {string.Join(" | ", boxes.Select(b => b.Name))}";
                return false;
            }

            box.AsCheckBox().IsChecked = value;
            bool took = box.AsCheckBox().IsChecked == value;
            reason = took ? "found and set" : "found but did not take the value";
            return took;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
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
