using System.Collections.Generic;
using Bwc.E2E.Controls;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.Hex;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Shell;

/// <summary>
/// Keyboard parity with classic regedit (K1a): F4 and the address bar auto-suggest list, Alt+1 to Alt+9 (no effect),
/// Shift+F6 (no effect), Alt+Up and Backspace to the parent key, Shift+F10 on the focused pane, the Computer root
/// and key context menus, the address bar edit menu, the F4 accelerator from the list, and the slow second click
/// that starts a rename (BEH-010).
/// </summary>
/// <remarks>
/// The measurements are docs/parity/oracle/regedit-keys.json and regedit-context.json. Each test starts its own
/// Registry Editor process and only navigates; the only write is the rename box, which is cancelled with Escape.
/// Results go to the E2E result artifact under the keyboard.* and contextmenu.keyboard.* ids.
/// </remarks>
[TestClass]
public sealed class KeyboardParityTests
{
    private const string AddressBarId = "AddressBar";
    private const string SuggestionsId = "AddressSuggestions";
    private const string TreePaneId = "TreePane";
    private const string ListPaneId = "ListPane";
    private const string TreeRenameBoxId = "TreeRenameBox";
    private const string ListRenameBoxId = "ListRenameBox";
    private const string ValueName = "KeyboardString";
    private const string HkcuPath = @"Computer\HKEY_CURRENT_USER";
    private const string SoftwarePath = @"Computer\HKEY_CURRENT_USER\Software";
    private const int CopyKeyNameCommand = 675;
    private const int ExportCommand = 659;
    private const int DeleteCommand = 913;
    private const int RenameCommand = 914;

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeySettle = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DoubleClickSettle = TimeSpan.FromMilliseconds(1200);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_F4FocusesAddressBarAndOpensSuggestions()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-f4");
        using var automation = new UIA3Automation();

        bool treeFocused = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", treeFocused, $"focused pane before F4={FocusedPane(automation)}.");

        Send(session, "F4");
        bool addressFocused = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        bool opened = WaitUntil(() => SuggestionNames(session).Count > 0, UiTimeout);
        IReadOnlyList<string> names = SuggestionNames(session);
        checks.Record(
            "f4.focusAddress",
            addressFocused,
            $"F4 from the tree: focused pane={FocusedPane(automation)} (classic: the address edit, command 642).");
        checks.Record(
            "f4.opensList",
            opened && names.All(name => name.StartsWith(@"Computer\HKEY_", StringComparison.OrdinalIgnoreCase)),
            $"The list opened={opened} with {names.Count} entries, the first {string.Join(",", names.Take(3))}.");
        Remember("f4.entries", string.Join(",", names.Take(8)));

        Send(session, "ESCAPE");
        bool closed = WaitUntil(() => SuggestionNames(session).Count == 0, TimeSpan.FromSeconds(5));
        bool stillInAddress = FocusedPane(automation) == "address";
        checks.Record(
            "escape.closesListFirst",
            closed && stillInAddress,
            $"Escape closed the list={closed}, focus still in the address bar={stillInAddress}.");

        Send(session, "ESCAPE");
        bool treeAgain = WaitUntil(() => FocusedPane(automation) == "tree", UiTimeout);
        checks.Record("escape.restoresTree", treeAgain, $"A second Escape returned focus to the tree={treeAgain}.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_TypingShowsSubkeySuggestionsAndEnterNavigates()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-typing");
        using var automation = new UIA3Automation();

        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        Send(session, "CONTROL+KEY_L");
        _ = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        Send(session, "CONTROL+KEY_A");
        Send(session, HkcuPath + @"\So");

        Remember("typing.focusAfterTyping", FocusedPane(automation));
        bool listed = WaitUntil(() => SuggestionNames(session).Contains(SoftwarePath), UiTimeout);
        checks.Record(
            "typing.listsSubkeys",
            listed,
            $"Typing '{HkcuPath}\\So' listed Software={listed}; entries=[{string.Join(",", SuggestionNames(session))}].");

        // The typed prefix names no key, so the full path is typed before Enter.
        Send(session, "CONTROL+KEY_A");
        Send(session, SoftwarePath);
        Send(session, "ENTER");
        bool navigated = WaitUntil(() => ValueOf(WaitForId(session, AddressBarId)) == SoftwarePath, UiTimeout);
        checks.Record(
            "enter.navigates",
            navigated && SuggestionNames(session).Count == 0,
            $"Enter committed the path={navigated}, list closed={SuggestionNames(session).Count == 0}.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_Alt1ToAlt9DoNothing()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-alt19");
        using var automation = new UIA3Automation();
        using var menus = new ContextMenuDriver(session);

        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        string before = ValueOf(WaitForId(session, AddressBarId));
        foreach (int digit in Enumerable.Range(1, 9))
        {
            Send(session, "ALT+" + digit);
            Thread.Sleep(KeySettle);
        }

        bool treeKept = FocusedPane(automation) == "tree";
        bool pathKept = ValueOf(WaitForId(session, AddressBarId)) == before;
        bool noMenu = menus.WaitForNoMenu(TimeSpan.FromSeconds(2));
        checks.Record(
            "tree.noEffect",
            treeKept && pathKept && noMenu,
            $"Alt+1 to Alt+9 from the tree: focus tree={treeKept}, path unchanged={pathKept}, no menu={noMenu}.");

        _ = EnsurePane(session, automation, "list", "TAB", 6);
        foreach (int digit in Enumerable.Range(1, 9))
        {
            Send(session, "ALT+" + digit);
            Thread.Sleep(KeySettle);
        }

        bool listKept = FocusedPane(automation) == "list";
        bool listPathKept = ValueOf(WaitForId(session, AddressBarId)) == before;
        bool listNoMenu = menus.WaitForNoMenu(TimeSpan.FromSeconds(2));
        checks.Record(
            "list.noEffect",
            listKept && listPathKept && listNoMenu,
            $"Alt+1 to Alt+9 from the list: focus list={listKept}, path unchanged={listPathKept}, no menu={listNoMenu}.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_ShiftF6DoesNothingAndF6StillCycles()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-shiftf6");
        using var automation = new UIA3Automation();

        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        Send(session, "SHIFT+F6");
        Thread.Sleep(KeySettle);
        Send(session, "SHIFT+F6");
        Thread.Sleep(KeySettle);
        string afterShift = FocusedPane(automation);
        checks.Record(
            "shiftF6.noEffect",
            afterShift == "tree",
            $"Shift+F6 x2 from the tree left the focus on [{afterShift}] (classic: no accelerator).");

        Send(session, "F6");
        Thread.Sleep(KeySettle);
        string afterF6 = FocusedPane(automation);
        checks.Record("f6.cycles", afterF6 == "list", $"F6 from the tree moved the focus to [{afterF6}].");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_AltUpSelectsParentFromTreeListAndAddress()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-altup");
        using var automation = new UIA3Automation();

        NavigateTo(session, automation, SoftwarePath);
        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        Send(session, "ALT+UP");
        bool fromTree = WaitUntil(() => ValueOf(WaitForId(session, AddressBarId)) == HkcuPath, UiTimeout);
        checks.Record("altUp.tree", fromTree, $"Alt+Up with tree focus: path=[{ValueOf(WaitForId(session, AddressBarId))}].");

        NavigateTo(session, automation, SoftwarePath);
        _ = EnsurePane(session, automation, "list", "TAB", 6);
        Send(session, "ALT+UP");
        bool fromList = WaitUntil(() => ValueOf(WaitForId(session, AddressBarId)) == HkcuPath, UiTimeout);
        checks.Record("altUp.list", fromList, $"Alt+Up with list focus: path=[{ValueOf(WaitForId(session, AddressBarId))}].");

        NavigateTo(session, automation, SoftwarePath);
        Send(session, "CONTROL+KEY_L");
        _ = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        Send(session, "ALT+UP");
        bool fromAddress = WaitUntil(() => ValueOf(WaitForId(session, AddressBarId)) == HkcuPath, UiTimeout);
        checks.Record("altUp.address", fromAddress, $"Alt+Up with address focus: path=[{ValueOf(WaitForId(session, AddressBarId))}].");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_BackspaceSelectsParentOnlyWithTreeFocus()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-backspace");
        using var automation = new UIA3Automation();

        NavigateTo(session, automation, SoftwarePath);
        _ = EnsurePane(session, automation, "list", "TAB", 6);
        Send(session, "BACK");
        Thread.Sleep(KeySettle);
        string listPath = ValueOf(WaitForId(session, AddressBarId));
        checks.Record(
            "backspace.listNoChange",
            listPath == SoftwarePath,
            $"Backspace with list focus left the path=[{listPath}] (classic: no change).");

        NavigateTo(session, automation, SoftwarePath);
        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        Send(session, "BACK");
        bool treeParent = WaitUntil(() => ValueOf(WaitForId(session, AddressBarId)) == HkcuPath, UiTimeout);
        checks.Record("backspace.treeParent", treeParent, $"Backspace with tree focus: path=[{ValueOf(WaitForId(session, AddressBarId))}].");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    [TestCategory("ContextMenus")]
    public void ContextMenus_ShiftF10OnListBackgroundAndComputerRoot()
    {
        var checks = new Checks("contextmenu");
        using var session = OpenBwc(checks, "contextmenu-keyboard");
        using var automation = new UIA3Automation();
        using var menus = new ContextMenuDriver(session);

        // Computer root: RT_MENU 107. Expand or Collapse, Export, and Disconnect (disabled on the local computer).
        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        Send(session, "SHIFT+F10");
        MenuSnapshot? root = menus.WaitForMenu(TimeSpan.FromSeconds(5));
        string[] rootNames = root?.Names.ToArray() ?? Array.Empty<string>();
        bool rootShape = rootNames.Length == 3 && (rootNames[0] == "Expand" || rootNames[0] == "Collapse") &&
            rootNames[1] == "Export" && rootNames[2] == "Disconnect";
        MenuEntry? disconnect = root?.Find(768);
        MenuEntry? export = root?.Find(ExportCommand);
        bool rootStates = disconnect is { Enabled: false } && export is { Enabled: true };
        checks.Record(
            "computerRoot.menu",
            rootShape && rootStates,
            $"Computer root menu=[{string.Join(",", rootNames)}], Disconnect enabled={disconnect?.Enabled}, Export enabled={export?.Enabled}.");
        Send(session, "ESCAPE");
        _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));

        // List background with nothing selected: the New submenu only.
        NavigateTo(session, automation, HkcuPath);
        _ = EnsurePane(session, automation, "list", "TAB", 6);
        Send(session, "SHIFT+F10");
        MenuSnapshot? background = menus.WaitForMenu(new[] { "New" }, UiTimeout);
        bool backgroundOk = background is not null && background.Names.Count == 1;
        checks.Record(
            "listBackground.menu",
            backgroundOk,
            $"Shift+F10 with list focus and nothing selected: menu=[{string.Join(",", background?.Names ?? Array.Empty<string>())}].");
        Send(session, "ESCAPE");
        _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    [TestCategory("ContextMenus")]
    public void ContextMenus_KeyMenuHasNoGoToAndDeleteRenameDisabledOnHive()
    {
        var checks = new Checks("contextmenu");
        using var session = OpenBwc(checks, "contextmenu-hive");
        using var automation = new UIA3Automation();
        using var menus = new ContextMenuDriver(session);

        NavigateTo(session, automation, HkcuPath);
        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        Send(session, "SHIFT+F10");
        MenuSnapshot? menu = menus.WaitForMenu(TimeSpan.FromSeconds(5));
        string[] names = menu?.Names.ToArray() ?? Array.Empty<string>();
        bool noGoTo = !names.Any(name => name.StartsWith("Go to", StringComparison.OrdinalIgnoreCase));
        bool hiveStates = menu?.Find(DeleteCommand) is { Enabled: false } && menu.Find(RenameCommand) is { Enabled: false } &&
            menu.Find(ExportCommand) is { Enabled: true };
        checks.Record(
            "hive.menu",
            noGoTo && hiveStates && names.Length == 8,
            $"HKCU menu=[{string.Join(",", names)}]; no Go to={noGoTo}; Delete and Rename disabled={hiveStates}.");
        Send(session, "ESCAPE");
        _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    [TestCategory("ContextMenus")]
    public void ContextMenus_AddressBarHasTheSystemEditMenu()
    {
        var checks = new Checks("contextmenu");
        using var session = OpenBwc(checks, "contextmenu-edit");
        using var automation = new UIA3Automation();
        using var menus = new ContextMenuDriver(session);

        string[] expected = ["Undo", "Cut", "Copy", "Paste", "Delete", "Select All"];
        MenuSnapshot? menu = menus.RightClickForMenu(() => session.Find(AddressBarId), expected, UiTimeout);
        string[] names = menu?.Names.ToArray() ?? Array.Empty<string>();
        bool namesOk = names.SequenceEqual(expected);
        bool cutOff = menu?.Entries.FirstOrDefault(entry => entry.Name == "Cut") is { Enabled: false };
        checks.Record(
            "addressBar.editMenu",
            namesOk && cutOff,
            $"Address bar menu=[{string.Join(",", names)}]; Cut disabled with no selection={cutOff} (classic: 768 to 771, 177).");
        Send(session, "ESCAPE");
        _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_SlowSecondClickStartsRename()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-slowclick");
        using var automation = new UIA3Automation();

        // Navigating to the key expands its parents, so the Software row is in the tree and has the focus.
        _ = NavigateTo(session, automation, SoftwarePath);
        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        AutomationElement? row = FindTreeRow(session, "Software");
        ControlTestSupport.Select(row);
        _ = WaitUntil(() => ControlTestSupport.IsSelected(FindTreeRow(session, "Software")), UiTimeout);

        // A second click after the double-click time, without moving, starts the rename (BEH-010).
        Thread.Sleep(DoubleClickSettle);
        FindTreeRow(session, "Software")?.Click();
        bool renaming = WaitUntil(() => session.Find(TreeRenameBoxId) is not null, TimeSpan.FromSeconds(3));
        checks.Record("slowClick.startsRename", renaming, $"The slow second click opened the rename box={renaming}.");
        Send(session, "ESCAPE");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Keyboard_DoubleClickOnSelectedKeyTogglesWithoutRename()
    {
        var checks = new Checks("keyboard");
        using var session = OpenBwc(checks, "keyboard-dblkey");
        using var automation = new UIA3Automation();

        _ = NavigateTo(session, automation, HkcuPath);
        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        string before = ExpandState(FindTreeRow(session, "HKEY_CURRENT_USER"));
        Thread.Sleep(DoubleClickSettle);

        // The key is selected and the tree has the focus, so the first click of the double-click is a slow click.
        FindTreeRow(session, "HKEY_CURRENT_USER")?.DoubleClick();
        Thread.Sleep(DoubleClickSettle);
        string after = ExpandState(FindTreeRow(session, "HKEY_CURRENT_USER"));
        bool renameOpened = session.Find(TreeRenameBoxId) is not null;
        checks.Record(
            "doubleClickKey.toggles",
            before != after,
            $"Double-click on the selected key: expand state {before} then {after}.");
        checks.Record(
            "doubleClickKey.noRename",
            !renameOpened,
            $"Double-click on the selected key opened the rename box={renameOpened}.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    [TestCategory("Mutating")]
    public void Keyboard_DoubleClickOnSelectedValueOpensEditorWithoutRename()
    {
        var checks = new Checks("keyboard");
        RunWithSandboxValue("keyboard-dblvalue", checks, (session, automation) =>
        {
            AutomationElement? row = FindListRow(session, ValueName);
            row?.Click();
            Thread.Sleep(DoubleClickSettle);

            // The value is selected and the list has the focus. The double-click opens the editor, not a rename.
            FindListRow(session, ValueName)?.DoubleClick();
            bool editorOpened = WaitUntil(() => IsValueEditorOpen(session), TimeSpan.FromSeconds(5));
            bool renameOpened = session.Find(ListRenameBoxId) is not null;
            checks.Record(
                "doubleClickValue.opensEditor",
                editorOpened && !renameOpened,
                $"Double-click on the selected value: editor opened={editorOpened}, rename box={renameOpened}.");
            CancelEditor();
        });

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    [TestCategory("Mutating")]
    public void Keyboard_ClickOnSelectedListRowFromTreeFocusDoesNotRename()
    {
        var checks = new Checks("keyboard");
        RunWithSandboxValue("keyboard-treefocus", checks, (session, automation) =>
        {
            AutomationElement? row = FindListRow(session, ValueName);
            row?.Click();
            Thread.Sleep(DoubleClickSettle);

            // The list row stays selected while the tree takes the focus with Shift+Tab.
            Send(session, "SHIFT+TAB");
            bool treeFocused = WaitUntil(() => FocusedPane(automation) == "tree", UiTimeout);
            Thread.Sleep(DoubleClickSettle);

            // One click moves the focus from the tree to the list, so it does not start the rename.
            FindListRow(session, ValueName)?.Click();
            Thread.Sleep(DoubleClickSettle);
            bool renameOpened = session.Find(ListRenameBoxId) is not null;
            checks.Record(
                "treeFocus.noRename",
                treeFocused && !renameOpened,
                $"Focus in the tree={treeFocused}; one click on the selected list row opened the rename box={renameOpened}.");
        });

        checks.AssertAllPassed();
    }

    /// <summary>Seeds one string value in the sandbox key, opens that key, runs the body, and removes the sandbox.</summary>
    private static void RunWithSandboxValue(string scope, Checks checks, Action<AppSession, UIA3Automation> body)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        try
        {
            sandbox.Cleanup();
            sandbox.SetString(string.Empty, ValueName, "before");
            dataRoot = HexEditingTests.CreateFreshDataRoot(config, scope);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var automation = new UIA3Automation();
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(ValueName, StringComparer.OrdinalIgnoreCase),
                UiTimeout);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, scope + ".sandbox.navigate");
            body(session, automation);

            string after = sandbox.ReadString(string.Empty, ValueName) ?? string.Empty;
            checks.Record("value.unchanged", after == "before", $"The value after the test is [{after}], expected [before].");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            HexEditingTests.DeleteDataRoot(dataRoot);
        }
    }

    /// <summary>The value editor is a separate window, so it is found on the desktop by its title.</summary>
    private static bool IsValueEditorOpen(AppSession session)
    {
        using var driver = new EditingDriver(session);
        return driver.WaitForDialog(TimeSpan.FromSeconds(1));
    }

    /// <summary>Cancels the open value editor with Escape. The editor is the foreground window.</summary>
    private static void CancelEditor()
    {
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(KeySettle);
    }

    private static string ExpandState(AutomationElement? element)
    {
        try
        {
            return element is not null && element.Patterns.ExpandCollapse.IsSupported
                ? element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString()
                : "none";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static AutomationElement? FindListRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, name);

    private static AppSession OpenBwc(Checks checks, string scope)
    {
        var config = GuiGate.Config;
        config.EnsureExecutablesExist();
        var session = new AppSession(config, config.RegeditExePath, SessionTimeout, config.DataRootEnvironment(scope));
        _ = ControlTestSupport.EnsureForeground(session);

        // Clicking the Computer row gives the tree the keyboard and keeps Computer selected.
        ControlTestSupport.Select(FindTreeRow(session, "Computer"));
        ClickTreePane(session);
        checks.Note("session", $"pid={session.ProcessId}, scope={scope}.");
        return session;
    }

    /// <summary>Types a path into the address bar. Returns true when the path's last key is selected in the tree.</summary>
    private static bool NavigateTo(AppSession session, UIA3Automation automation, string path)
    {
        if (FocusedPane(automation) != "address")
        {
            Send(session, "CONTROL+KEY_L");
            _ = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        }

        Send(session, "CONTROL+KEY_A");
        Send(session, path);
        Send(session, "ENTER");
        string leaf = path[(path.LastIndexOf('\\') + 1)..];
        bool selected = WaitUntil(() => ControlTestSupport.IsSelected(FindTreeRow(session, leaf)), UiTimeout);
        Thread.Sleep(KeySettle);
        return selected;
    }

    /// <summary>The names of the auto-suggest entries, or none when the list is closed.</summary>
    private static IReadOnlyList<string> SuggestionNames(AppSession session)
    {
        AutomationElement? list = session.Find(SuggestionsId);
        if (list is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return list.FindAllChildren()
                .Select(child => child.Name ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The pane that holds keyboard focus: tree, list, address, or other.</summary>
    private static string FocusedPane(UIA3Automation automation)
    {
        AutomationElement? focused;
        try
        {
            focused = automation.FocusedElement();
        }
        catch (Exception)
        {
            return "none";
        }

        for (AutomationElement? current = focused; current is not null; current = SafeParent(current))
        {
            string id = ControlTestSupport.SafeId(current);
            switch (id)
            {
                case AddressBarId:
                    return "address";
                case TreePaneId:
                    return "tree";
                case ListPaneId:
                    return "list";
            }
        }

        return "other";
    }

    /// <summary>Presses Tab until the target pane has focus. Returns false when it is not reached.</summary>
    private static bool EnsurePane(AppSession session, UIA3Automation automation, string target, string key, int maxPresses)
    {
        for (var i = 0; i <= maxPresses; i++)
        {
            if (FocusedPane(automation) == target)
            {
                return true;
            }

            if (i < maxPresses)
            {
                Send(session, key);
                Thread.Sleep(KeySettle);
            }
        }

        return FocusedPane(automation) == target;
    }

    private static AutomationElement? FindTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name);

    private static AutomationElement? WaitForId(AppSession session, string automationId)
    {
        _ = WaitUntil(() => session.Find(automationId) is not null, UiTimeout);
        return session.Find(automationId);
    }

    private static string ValueOf(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Sends keys to the window. A menu that closed can leave another window in front, so the window is brought
    /// forward first, and a click on the tree pane is tried when it does not come forward.
    /// </summary>
    private static void Send(AppSession session, params string[] keys)
    {
        for (int attempt = 0; attempt < 3 && !ControlTestSupport.EnsureForeground(session); attempt++)
        {
            ClickTreePane(session);
        }

        session.SendKeys(keys);
    }

    /// <summary>Clicks the tree pane so the shell has the keyboard. Failures are reported by the checks that follow.</summary>
    private static void ClickTreePane(AppSession session)
    {
        try
        {
            session.Find(TreePaneId)?.Click();
        }
        catch (Exception)
        {
            // The focus checks that follow report whether the click took effect.
        }

        Thread.Sleep(200);
    }

    private static AutomationElement? SafeParent(AutomationElement element)
    {
        try
        {
            return element.Parent;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }

    private static void Remember(string key, string text) => E2EResults.Note(key, text);

    private sealed class Checks
    {
        private readonly string _prefix;
        private readonly List<string> _failures = [];

        public Checks(string prefix)
        {
            _prefix = prefix;
        }

        public void Record(string id, bool pass, string notes)
        {
            string fullId = $"{_prefix}.{id}";
            _ = E2EResults.Record(fullId, pass, notes);
            if (!pass)
            {
                _failures.Add($"{fullId}: {notes}");
            }
        }

        public void Note(string id, string text) => E2EResults.Note($"{_prefix}.{id}", text);

        public void AssertAllPassed()
        {
            Assert.AreEqual(
                0,
                _failures.Count,
                $"Keyboard parity checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, _failures)}");
        }
    }
}
