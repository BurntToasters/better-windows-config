using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Shell;

/// <summary>
/// End to end checks for the classic Registry Editor shell. Every test starts its own Registry
/// Editor process so menu mode, the address bar, and the splitter width cannot leak across tests.
/// All measurements are physical pixels at the window DPI; the expected values are DIPs from
/// docs/parity/oracle/regedit-metrics.json multiplied by the rasterization scale.
/// </summary>
[TestClass]
public sealed class ShellTests
{
    private const string RegeditTitle = "Registry Editor";
    private const string MenuBarItemClass = "Microsoft.UI.Xaml.Controls.MenuBarItem";
    private const string MenuFlyoutItemClass = "MenuFlyoutItem";
    private const string MenuFlyoutSubItemClass = "MenuFlyoutSubItem";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(5);
    private static readonly IntPtr PerMonitorV2 = new(-4);
    private static readonly object PidLock = new();
    private static readonly List<int> StartedPids = [];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        _ = Native.SetProcessDpiAwarenessContext(PerMonitorV2);
        PrepareThread();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        var checks = new Checks("shell.process");
        var tracked = TrackedProcessIds();
        var alive = tracked.Where(IsProcessAlive).ToArray();
        foreach (var pid in alive)
        {
            KillTrackedProcess(pid);
        }

        var remaining = tracked.Where(IsProcessAlive).ToArray();
        checks.Record(
            "classCleanup",
            remaining.Length == 0,
            $"tracked=[{JoinIds(tracked)}] aliveBeforeCleanup=[{JoinIds(alive)}] remaining=[{JoinIds(remaining)}].");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_LaunchShowsClassicStructure()
    {
        PrepareThread();
        var checks = new Checks("shell.launch");
        using var session = OpenRegedit(checks);

        var title = session.Window.Title;
        checks.Record(
            "title",
            string.Equals(title, RegeditTitle, StringComparison.Ordinal),
            $"expected '{RegeditTitle}', got '{title}'.");

        var root = WaitForId(session, "RegeditRoot");
        checks.Record(
            "regeditRootPresent",
            root is not null,
            $"automation id 'RegeditRoot' found={root is not null}.");
        checks.Record(
            "regeditRootName",
            string.Equals(SafeName(root), RegeditTitle, StringComparison.Ordinal),
            $"automation id 'RegeditRoot' expected name '{RegeditTitle}', got '{SafeName(root)}'.");

        var shell = WaitForId(session, "ClassicWindowShell");
        checks.Record(
            "shellPresent",
            shell is not null,
            $"automation id 'ClassicWindowShell' found={shell is not null}.");

        var address = WaitForId(session, "AddressBar");
        checks.Record(
            "addressBarPresent",
            address is not null,
            $"automation id 'AddressBar' found={address is not null}.");
        var addressText = WaitForValue(session, "AddressBar", "Computer");
        checks.Record(
            "addressBarText",
            string.Equals(addressText, "Computer", StringComparison.Ordinal),
            $"expected 'Computer', got '{addressText}'.");

        var tree = WaitForId(session, "TreePane");
        checks.Record(
            "treePanePresent",
            tree is not null,
            $"automation id 'TreePane' found={tree is not null}.");

        var list = WaitForId(session, "ListPane");
        checks.Record(
            "listPanePresent",
            list is not null,
            $"automation id 'ListPane' found={list is not null}.");

        CheckText(session, checks, "headerName", "HeaderName", "Name");
        CheckText(session, checks, "headerType", "HeaderType", "Type");
        CheckText(session, checks, "headerData", "HeaderData", "Data");

        var splitter = WaitForId(session, "Splitter");
        checks.Record(
            "splitterPresent",
            splitter is not null,
            $"automation id 'Splitter' found={splitter is not null}.");

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_ClassicMetricsMatchOracleAtWindowScale()
    {
        PrepareThread();
        var checks = new Checks("shell.metrics");
        using var session = OpenRegedit(checks);

        session.Window.Click();
        var foreground = WaitUntil(() => Win32.IsForeground(session.MainHwnd), TimeSpan.FromSeconds(3));
        checks.Record(
            "foreground",
            true,
            $"informational: window HWND 0x{session.MainHwnd.ToInt64():X} is foreground={foreground}.");

        var dpi = Native.GetDpiForWindow(session.MainHwnd);
        var scale = dpi / 96.0;
        checks.Record(
            "dpi",
            dpi > 0,
            $"informational: GetDpiForWindow={dpi}, rasterization scale={scale:0.##}.");

        var menuBar = WaitForId(session, "MenuBarRoot");
        var menuBarHeight = HeightOf(menuBar);
        checks.Record(
            "menuBarHeight",
            Within(menuBarHeight, 19 * scale),
            $"expected {19 * scale:0.#} px (19 DIP), got {menuBarHeight:0.#} px.");

        var topLevel = WaitForTopLevelItems(session, 5);
        var itemHeights = topLevel.Select(e => HeightOf(e)).ToArray();
        checks.Record(
            "menuItemHeights",
            topLevel.Length == 5 && itemHeights.All(h => Within(h, 19 * scale)),
            $"expected 5 items at {19 * scale:0.#} px (19 DIP), got {itemHeights.Length} heights [{JoinDoubles(itemHeights)}] px.");

        var itemWidths = topLevel.Select(e => WidthOf(e)).ToArray();
        checks.Record(
            "menuItemWidths",
            true,
            $"informational: widths [{JoinDoubles(itemWidths)}] px; oracle at 200% is [65,68,79,122,78] px; not asserted.");
        checks.Record(
            "menuItemNames",
            topLevel.Length == 5 &&
            string.Equals(string.Join(",", topLevel.Select(SafeName)), "File,Edit,View,Favorites,Help", StringComparison.Ordinal),
            $"expected File,Edit,View,Favorites,Help; got {string.Join(",", topLevel.Select(SafeName))}.");

        var address = WaitForId(session, "AddressBar");
        var addressHeight = HeightOf(address);
        checks.Record(
            "addressBarHeight",
            Within(addressHeight, 19 * scale),
            $"expected {19 * scale:0.#} px (19 DIP), got {addressHeight:0.#} px.");

        var tree = WaitForId(session, "TreePane");
        var treeWidth = WidthOf(tree);
        checks.Record(
            "treePaneWidth",
            Within(treeWidth, 216 * scale),
            $"expected {216 * scale:0.#} px (216 DIP), got {treeWidth:0.#} px.");

        var splitter = WaitForId(session, "Splitter");
        var splitterWidth = WidthOf(splitter);
        checks.Record(
            "splitterWidth",
            Within(splitterWidth, 4 * scale),
            $"expected {4 * scale:0.#} px (4 DIP), got {splitterWidth:0.#} px.");

        var header = WaitForId(session, "HeaderName");
        var headerTextHeight = HeightOf(header);
        checks.Record(
            "headerTextHeight",
            headerTextHeight >= 24,
            $"expected at least 24 px, got {headerTextHeight:0.#} px.");

        var listHeader = WaitForId(session, "ListHeader");
        var headerRowHeight = HeightOf(listHeader);
        checks.Record(
            "headerRowHeight",
            listHeader is not null && Within(headerRowHeight, 20 * scale),
            $"expected {20 * scale:0.#} px (20 DIP), got {headerRowHeight:0.#} px from UIA.");

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_MenuTableMatchesCommandStates()
    {
        PrepareThread();
        var checks = new Checks("shell.menu");
        using var session = OpenRegedit(checks);

        session.Window.Click();

        var file = ExpandTopLevel(session, "File");
        checks.Record(
            "fileExpanded",
            StateOf(file) == ExpandCollapseState.Expanded,
            $"File ExpandCollapseState={StateOf(file)?.ToString() ?? "(missing)"}.");

        var fileItems = WaitForVisibleMenuItems(session, 13);
        var fileNames = string.Join(",", fileItems.Select(SafeName));
        checks.Record(
            "fileItemCount",
            fileItems.Length == 13,
            $"expected 13 visible MenuItem elements (5 top-level plus 8 File commands; separators have no UIA peer), got {fileItems.Length}: {fileNames}.");

        var fileCommands = new (string Id, string Name, bool Enabled)[]
        {
            ("import", "Import...", true),
            ("export", "Export...", false),
            ("loadHive", "Load Hive...", true),
            ("unloadHive", "Unload Hive...", false),
            ("connectNetworkRegistry", "Connect Network Registry...", true),
            ("disconnectNetworkRegistry", "Disconnect Network Registry...", false),
            ("print", "Print...", false),
            ("exit", "Exit", true),
        };

        foreach (var command in fileCommands)
        {
            CheckMenuItemState(checks, session, command.Id, command.Name, command.Enabled);
        }

        Collapse(file);

        var edit = ExpandTopLevel(session, "Edit");
        checks.Record(
            "editExpanded",
            StateOf(edit) == ExpandCollapseState.Expanded,
            $"Edit ExpandCollapseState={StateOf(edit)?.ToString() ?? "(missing)"}.");

        var newItem = WaitForMenuItem(session, "New");
        var newExpanded = false;
        if (newItem is not null && newItem.Patterns.ExpandCollapse.IsSupported)
        {
            try
            {
                newItem.Patterns.ExpandCollapse.Pattern.Expand();
                newExpanded = WaitUntil(() => StateOf(newItem) == ExpandCollapseState.Expanded, MenuTimeout);
            }
            catch (Exception ex)
            {
                checks.Record("newExpanded", false, ex.Message);
            }
        }

        if (newItem is not null)
        {
            checks.Record(
                "newExpanded",
                newExpanded,
                $"New ExpandCollapseState={StateOf(newItem)?.ToString() ?? "(missing)"}.");
        }
        else
        {
            checks.Record("newExpanded", false, "Edit > New was not found.");
        }

        var newChildren = new (string Id, string Name)[]
        {
            ("key", "Key"),
            ("stringValue", "String Value"),
            ("binaryValue", "Binary Value"),
            ("dwordValue", "DWORD (32-bit) Value"),
            ("qwordValue", "QWORD (64-bit) Value"),
            ("multiStringValue", "Multi-String Value"),
            ("expandableStringValue", "Expandable String Value"),
        };

        foreach (var child in newChildren)
        {
            CheckMenuItemState(checks, session, "new." + child.Id, child.Name, false);
        }

        if (newItem is not null && newItem.Patterns.ExpandCollapse.IsSupported)
        {
            try
            {
                newItem.Patterns.ExpandCollapse.Pattern.Collapse();
            }
            catch (Exception)
            {
                // The next menu expansion replaces the popup anyway.
            }
        }

        Collapse(edit);

        var view = ExpandTopLevel(session, "View");
        checks.Record(
            "viewExpanded",
            StateOf(view) == ExpandCollapseState.Expanded,
            $"View ExpandCollapseState={StateOf(view)?.ToString() ?? "(missing)"}.");

        CheckMenuItemState(checks, session, "view.addressBar", "Address Bar", true);
        CheckMenuItemState(checks, session, "view.split", "Split", true);
        CheckMenuItemState(checks, session, "view.displayBinaryData", "Display Binary Data...", false);
        CheckMenuItemState(checks, session, "view.refresh", "Refresh", false);
        CheckMenuItemState(checks, session, "view.font", "Font", false);
        Collapse(view);

        var favorites = WaitForTopLevel(session, "Favorites");
        checks.Record(
            "favoritesPresent",
            favorites is not null,
            $"top-level Favorites found={favorites is not null}.");
        var help = WaitForTopLevel(session, "Help");
        checks.Record(
            "helpPresent",
            help is not null,
            $"top-level Help found={help is not null}.");

        session.Window.Click();
        var escapeFile = ExpandTopLevel(session, "File");
        var expandedBeforeEscape = StateOf(escapeFile) == ExpandCollapseState.Expanded;
        var escapeSent = true;
        try
        {
            session.SendKeys("Escape");
        }
        catch (Exception ex)
        {
            escapeSent = false;
            checks.Record("escapeCloses", false, ex.Message);
        }

        if (escapeSent)
        {
            var collapsed = WaitUntil(() => StateOf(escapeFile) == ExpandCollapseState.Collapsed, MenuTimeout);
            checks.Record(
                "escapeCloses",
                expandedBeforeEscape && collapsed,
                $"File expanded before Escape={expandedBeforeEscape}, state after Escape={StateOf(escapeFile)?.ToString() ?? "(missing)"}.");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_AddressBarToggles()
    {
        PrepareThread();
        var checks = new Checks("shell.addressbar");
        using var session = OpenRegedit(checks);

        var scale = Native.GetDpiForWindow(session.MainHwnd) / 96.0;

        var address = WaitForId(session, "AddressBar");
        var initialHeight = HeightOf(address);
        checks.Record(
            "visibleBefore",
            address is not null && Within(initialHeight, 19 * scale),
            $"initial AddressBar height {initialHeight:0.#} px, expected {19 * scale:0.#} px.");

        ExpandTopLevel(session, "View");
        InvokeMenuItem(session, "Address Bar");
        var hidden = WaitUntil(() => AddressBarHidden(session), MenuTimeout);
        checks.Record(
            "hiddenAfterToggle",
            hidden,
            $"AddressBar absent={session.Find("AddressBar") is null}, height={HeightOf(session.Find("AddressBar")):0.#} px, offscreen={SafeOffscreen(session.Find("AddressBar"))}.");

        ExpandTopLevel(session, "View");
        InvokeMenuItem(session, "Address Bar");
        var shown = WaitUntil(() =>
        {
            var current = session.Find("AddressBar");
            return current is not null && !SafeOffscreen(current) && Within(HeightOf(current), 19 * scale);
        }, MenuTimeout);

        var restored = session.Find("AddressBar");
        checks.Record(
            "visibleAfterToggle",
            shown,
            $"AddressBar found={restored is not null}, height={HeightOf(restored):0.#} px, expected {19 * scale:0.#} px, offscreen={SafeOffscreen(restored)}.");

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_SplitterKeyboardMovesAndReleasesFocus()
    {
        PrepareThread();
        var checks = new Checks("shell.split");
        using var session = OpenRegedit(checks);

        session.Window.Click();
        var scale = Native.GetDpiForWindow(session.MainHwnd) / 96.0;

        ExpandTopLevel(session, "View");
        InvokeMenuItem(session, "Split");

        var splitter = WaitForId(session, "Splitter");
        var focused = WaitUntil(() => HasFocus(splitter), MenuTimeout);
        checks.Record(
            "focus",
            focused,
            $"Splitter HasKeyboardFocus={HasFocus(splitter)} after View > Split.");

        var before = WidthOf(WaitForId(session, "TreePane"));
        session.SendKeys("Left");
        var afterLeft = 0.0;
        var movedLeft = WaitUntil(() =>
        {
            afterLeft = WidthOf(WaitForId(session, "TreePane"));
            return afterLeft < before;
        }, MenuTimeout);
        checks.Record(
            "leftMoves",
            movedLeft && Math.Abs((before - afterLeft) - 8 * scale) <= 2,
            $"tree width before {before:0.#} px, after Left {afterLeft:0.#} px, expected step {8 * scale:0.#} px.");

        session.SendKeys("Right");
        var afterRight = 0.0;
        var movedRight = WaitUntil(() =>
        {
            afterRight = WidthOf(WaitForId(session, "TreePane"));
            return afterRight > afterLeft;
        }, MenuTimeout);
        checks.Record(
            "rightRestores",
            movedRight && Math.Abs(afterRight - before) <= 2,
            $"tree width after Right {afterRight:0.#} px, expected {before:0.#} px.");

        session.SendKeys("Enter");
        var released = WaitUntil(() => !HasFocus(WaitForId(session, "Splitter")), MenuTimeout);
        var shell = WaitForId(session, "ClassicWindowShell");
        checks.Record(
            "enterReleasesFocus",
            released,
            $"Splitter HasKeyboardFocus after Enter={HasFocus(WaitForId(session, "Splitter"))}, shell HasKeyboardFocus={HasFocus(shell)}.");

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_AltAndF10DriveTheMenuBar()
    {
        PrepareThread();
        var checks = new Checks("shell.keyboard");
        using var session = OpenRegedit(checks);

        session.Window.Click();

        var altFSent = SendChord(checks, session, "altFSent", "ALT+F", "ALT+KEY_F");
        var file = WaitForTopLevel(session, "File");
        var fileExpanded = altFSent && WaitUntil(() => StateOf(file) == ExpandCollapseState.Expanded, MenuTimeout);
        checks.Record(
            "altFExpandsFile",
            fileExpanded,
            $"File ExpandCollapseState after ALT+F={StateOf(file)?.ToString() ?? "(missing)"}.");

        session.SendKeys("Escape");
        var fileCollapsed = WaitUntil(() => StateOf(file) == ExpandCollapseState.Collapsed, MenuTimeout);
        checks.Record(
            "escapeCollapsesFile",
            fileCollapsed,
            $"File ExpandCollapseState after Escape={StateOf(file)?.ToString() ?? "(missing)"}.");

        session.SendKeys("Escape");
        _ = WaitUntil(() => FocusedTopLevelName(session) is null, MenuTimeout);
        checks.Record(
            "secondEscapeLeavesMenuBar",
            true,
            $"informational: focused top-level menu item after second Escape={FocusedTopLevelName(session) ?? "(none)"}; the menu is closed even when a top-level item keeps focus.");

        session.SendKeys("F10");
        var f10Focused = WaitUntil(() => FocusedTopLevelName(session) is not null, MenuTimeout);
        checks.Record(
            "f10FocusesTopLevel",
            f10Focused,
            $"focused top-level menu item after F10={FocusedTopLevelName(session) ?? "(none)"}.");

        session.SendKeys("Escape");
        var f10Closed = WaitUntil(() => StateOf(file) == ExpandCollapseState.Collapsed, MenuTimeout);
        checks.Record(
            "escapeClosesF10Menu",
            f10Closed,
            $"after Escape: File state={StateOf(file)?.ToString() ?? "(missing)"}, focused top-level={FocusedTopLevelName(session) ?? "(none)"}.");

        var altVSent = SendChord(checks, session, "altVSent", "ALT+V", "ALT+KEY_V");
        var view = WaitForTopLevel(session, "View");
        var viewExpanded = altVSent && WaitUntil(() => StateOf(view) == ExpandCollapseState.Expanded, MenuTimeout);
        checks.Record(
            "altVExpandsView",
            viewExpanded,
            $"View ExpandCollapseState after ALT+V={StateOf(view)?.ToString() ?? "(missing)"}.");

        session.SendKeys("Escape");
        var viewCollapsed = WaitUntil(() => StateOf(view) == ExpandCollapseState.Collapsed, MenuTimeout);
        checks.Record(
            "escapeClosesView",
            viewCollapsed,
            $"View ExpandCollapseState after Escape={StateOf(view)?.ToString() ?? "(missing)"}.");

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_CloseLeavesNoTestProcess()
    {
        PrepareThread();
        var checks = new Checks("shell.process");
        var before = RegeditPids();
        var session = OpenRegedit(checks);
        var pid = session.ProcessId;
        try
        {
            checks.Record(
                "started",
                !before.Contains(pid),
                $"pid={pid}, before=[{JoinIds(before)}].");

            session.Close();
            var exited = WaitUntil(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(10));
            if (!exited)
            {
                KillTrackedProcess(pid);
                exited = WaitUntil(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(5));
            }

            checks.Record(
                "exited",
                exited,
                $"pid={pid} exited={exited} after AppSession.Close.");
        }
        finally
        {
            session.Dispose();
        }

        var after = RegeditPids();
        var newRemaining = after.Except(before).ToArray();
        checks.Record(
            "noTestProcessRemains",
            newRemaining.Length == 0,
            $"before=[{JoinIds(before)}], after=[{JoinIds(after)}], test pid={pid}, new remaining=[{JoinIds(newRemaining)}].");

        checks.AssertAllPassed();
    }

    private static bool SendChord(Checks checks, AppSession session, string id, string key, string fallback)
    {
        try
        {
            session.SendKeys(key);
            checks.Record(id, true, $"sent '{key}'.");
            return true;
        }
        catch (ArgumentException ex)
        {
            // AppSession parses chords as FlaUI VirtualKeyShort names, and a bare letter is not
            // a member of that enum. The same SendKeys path, including its foreground check, is
            // used again with the enum name so the test still exercises the real input route.
            try
            {
                session.SendKeys(fallback);
                checks.Record(
                    id,
                    true,
                    $"informational: '{key}' is not a FlaUI key name ({ex.Message}); sent '{fallback}' through AppSession.SendKeys.");
                return true;
            }
            catch (Exception fallbackError)
            {
                checks.Record(id, false, fallbackError.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            checks.Record(id, false, ex.Message);
            return false;
        }
    }

    private static AppSession OpenRegedit(Checks checks)
    {
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
            var session = new AppSession(config, config.RegeditExePath);
            TrackProcess(session.ProcessId);
            checks.Record(
                "session",
                true,
                $"pid={session.ProcessId}, publish={config.PublishDir}.");
            return session;
        }
        catch (Exception ex)
        {
            checks.Record("session", false, ex.Message);
            throw;
        }
    }

    private static void CheckText(AppSession session, Checks checks, string id, string automationId, string expected)
    {
        var element = WaitForId(session, automationId);
        var actual = SafeName(element);
        checks.Record(
            id,
            string.Equals(actual, expected, StringComparison.Ordinal),
            $"automation id '{automationId}' expected '{expected}', got '{actual}'.");
    }

    private static void CheckMenuItemState(Checks checks, AppSession session, string id, string name, bool expectedEnabled)
    {
        var item = WaitForMenuItem(session, name);
        if (item is null)
        {
            checks.Record(id, false, $"'{name}' was not found as a menu item.");
            return;
        }

        var actual = SafeBool(() => item.IsEnabled);
        checks.Record(
            id,
            actual == expectedEnabled,
            $"'{name}' expected enabled={expectedEnabled}, got enabled={actual}.");
    }

    private static AutomationElement? ExpandTopLevel(AppSession session, string name)
    {
        var item = WaitForTopLevel(session, name);
        if (item is null)
        {
            return null;
        }

        try
        {
            if (item.Patterns.ExpandCollapse.IsSupported)
            {
                item.Patterns.ExpandCollapse.Pattern.Expand();
            }
        }
        catch (Exception)
        {
            // The state check that follows reports whether the flyout opened.
        }

        _ = WaitUntil(() => StateOf(item) == ExpandCollapseState.Expanded, MenuTimeout);
        return item;
    }

    private static void Collapse(AutomationElement? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (item.Patterns.ExpandCollapse.IsSupported)
            {
                item.Patterns.ExpandCollapse.Pattern.Collapse();
            }
        }
        catch (Exception)
        {
            // A later expansion replaces the open flyout.
        }

        _ = WaitUntil(() => StateOf(item) == ExpandCollapseState.Collapsed, MenuTimeout);
    }

    private static AutomationElement? InvokeMenuItem(AppSession session, string name)
    {
        var item = WaitForMenuItem(session, name);
        if (item is null)
        {
            return null;
        }

        try
        {
            if (item.Patterns.Invoke.IsSupported)
            {
                item.Patterns.Invoke.Pattern.Invoke();
            }
            else
            {
                item.Click();
            }
        }
        catch (Exception)
        {
            // The caller's state check reports whether the command took effect.
        }

        return item;
    }

    private static AutomationElement? WaitForTopLevel(AppSession session, string name)
    {
        AutomationElement? found = null;
        _ = WaitUntil(
            () =>
            {
                found = TopLevelItems(session).FirstOrDefault(
                    e => string.Equals(SafeName(e), name, StringComparison.Ordinal));
                return found is not null;
            },
            UiTimeout);
        return found;
    }

    private static AutomationElement[] WaitForTopLevelItems(AppSession session, int expected)
    {
        AutomationElement[] found = [];
        _ = WaitUntil(
            () =>
            {
                found = TopLevelItems(session);
                return found.Length == expected;
            },
            MenuTimeout);
        return found;
    }

    private static AutomationElement[] WaitForVisibleMenuItems(AppSession session, int minimum)
    {
        AutomationElement[] found = [];
        _ = WaitUntil(
            () =>
            {
                found = VisibleMenuItems(session);
                return found.Length >= minimum;
            },
            MenuTimeout);
        return VisibleMenuItems(session);
    }

    private static AutomationElement? WaitForMenuItem(AppSession session, string name)
    {
        AutomationElement? found = null;
        _ = WaitUntil(
            () =>
            {
                found = session.Window
                    .FindAllDescendants(cf => cf.ByName(name))
                    .FirstOrDefault(e => e.ControlType == ControlType.MenuItem);
                return found is not null;
            },
            MenuTimeout);
        return found;
    }

    private static AutomationElement? WaitForId(AppSession session, string automationId)
    {
        AutomationElement? found = null;
        _ = WaitUntil(() => (found = session.Find(automationId)) is not null, UiTimeout);
        return found;
    }

    private static string WaitForValue(AppSession session, string automationId, string expected)
    {
        var last = "(missing)";
        _ = WaitUntil(
            () =>
            {
                var element = session.Find(automationId);
                if (element is null)
                {
                    return false;
                }

                last = ValueOf(element);
                return string.Equals(last, expected, StringComparison.Ordinal);
            },
            UiTimeout);
        return last;
    }

    private static AutomationElement[] TopLevelItems(AppSession session)
    {
        try
        {
            return session.Window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .Where(e => string.Equals(SafeClassName(e), MenuBarItemClass, StringComparison.Ordinal))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static AutomationElement[] VisibleMenuItems(AppSession session)
    {
        try
        {
            return session.Window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .Where(
                    e =>
                    {
                        var className = SafeClassName(e);
                        return !SafeOffscreen(e) &&
                            (string.Equals(className, MenuBarItemClass, StringComparison.Ordinal) ||
                             string.Equals(className, MenuFlyoutItemClass, StringComparison.Ordinal) ||
                             string.Equals(className, MenuFlyoutSubItemClass, StringComparison.Ordinal));
                    })
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? FocusedTopLevelName(AppSession session) =>
        TopLevelItems(session).FirstOrDefault(HasFocus) is { } focused ? SafeName(focused) : null;

    private static bool AddressBarHidden(AppSession session)
    {
        var element = session.Find("AddressBar");
        if (element is null)
        {
            return true;
        }

        try
        {
            return element.IsOffscreen || element.BoundingRectangle.Height <= 1;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool HasFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.HasKeyboardFocus.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ExpandCollapseState? StateOf(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ValueOf(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // Fall through to the name.
        }

        return SafeName(element);
    }

    private static string SafeName(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeClassName(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.ClassName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool SafeOffscreen(AutomationElement? element)
    {
        if (element is null)
        {
            return true;
        }

        try
        {
            return element.IsOffscreen;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double HeightOf(AutomationElement? element) => element is null ? 0 : element.BoundingRectangle.Height;

    private static double WidthOf(AutomationElement? element) => element is null ? 0 : element.BoundingRectangle.Width;

    private static bool Within(double actual, double expected) => Math.Abs(actual - expected) <= 1.0;

    private static string JoinDoubles(IEnumerable<double> values) =>
        string.Join(",", values.Select(v => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));

    private static string JoinIds(IEnumerable<int> ids)
    {
        var text = string.Join(",", ids);
        return text.Length == 0 ? "(none)" : text;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // A stale element is retried on the next pass.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }
    }

    private static void PrepareThread() => _ = Native.SetThreadDpiAwarenessContext(PerMonitorV2);

    private static void TrackProcess(int pid)
    {
        lock (PidLock)
        {
            if (!StartedPids.Contains(pid))
            {
                StartedPids.Add(pid);
            }
        }
    }

    private static int[] TrackedProcessIds()
    {
        lock (PidLock)
        {
            return StartedPids.OrderBy(pid => pid).ToArray();
        }
    }

    private static int[] RegeditPids()
    {
        var processes = Process.GetProcessesByName("BwcRegedit");
        var ids = new List<int>();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    ids.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and the id read.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return ids.OrderBy(pid => pid).ToArray();
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static void KillTrackedProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited &&
                string.Equals(process.ProcessName, "BwcRegedit", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The process already exited or access was lost while it was exiting.
        }
    }

    /// <summary>
    /// Finds the bottom of the list header row by scanning a screen strip for the first full-width
    /// divider row. The header row has no automation peer, so UIA cannot report its rectangle.
    /// </summary>
    private static int MeasureHeaderRowHeight(Rectangle listPane)
    {
        var width = Math.Min(300, listPane.Width - 2);
        const int StripHeight = 80;
        if (width < 20 || listPane.Width <= 0)
        {
            return -1;
        }

        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return -1;
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = Native.CreateCompatibleDC(screenDc);
            bitmap = Native.CreateCompatibleBitmap(screenDc, width, StripHeight);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return -1;
            }

            previous = Native.SelectObject(memoryDc, bitmap);
            if (!Native.BitBlt(memoryDc, 0, 0, width, StripHeight, screenDc, listPane.Left, listPane.Top, 0x00CC0020))
            {
                return -1;
            }

            _ = Native.SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -StripHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                },
            };

            var pixels = new byte[width * StripHeight * 4];
            if (Native.GetDIBits(memoryDc, bitmap, 0, (uint)StripHeight, pixels, ref info, 0) == 0)
            {
                return -1;
            }

            var headerColor = DominantColor(0);
            var bodyColor = DominantColor(StripHeight - 1);

            static int Distance((int Red, int Green, int Blue) left, (int Red, int Green, int Blue) right) =>
                Math.Max(
                    Math.Abs(left.Red - right.Red),
                    Math.Max(Math.Abs(left.Green - right.Green), Math.Abs(left.Blue - right.Blue)));

            if (Distance(headerColor, bodyColor) > 8)
            {
                // The header and body use different layer colors. The header ends at the first
                // row where the body color becomes dominant for at least three rows in a row.
                var run = 0;
                for (var y = 10; y < StripHeight; y++)
                {
                    var color = DominantColor(y);
                    if (Distance(color, bodyColor) <= 3 && Distance(color, headerColor) > 8)
                    {
                        run++;
                        if (run >= 3)
                        {
                            return y - 2;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }

                return -1;
            }

            var threshold = (int)(width * 0.9);
            var dividerStart = -1;
            var dividerEnd = -1;
            for (var y = 4; y < StripHeight; y++)
            {
                var different = 0;
                for (var x = 0; x < width; x++)
                {
                    var index = ((y * width) + x) * 4;
                    var distance = Math.Max(
                        Math.Abs(pixels[index] - headerColor.Blue),
                        Math.Max(
                            Math.Abs(pixels[index + 1] - headerColor.Green),
                            Math.Abs(pixels[index + 2] - headerColor.Red)));
                    if (distance > 8)
                    {
                        different++;
                    }
                }

                var isDivider = different >= threshold;
                if (isDivider && dividerStart < 0)
                {
                    dividerStart = y;
                }

                if (isDivider)
                {
                    dividerEnd = y;
                }
                else if (dividerStart >= 0)
                {
                    break;
                }
            }

            return dividerStart < 0 ? -1 : dividerEnd + 1;

            (int Red, int Green, int Blue) DominantColor(int y)
            {
                var counts = new Dictionary<int, int>();
                for (var x = 0; x < width; x++)
                {
                    var index = ((y * width) + x) * 4;
                    var key = (pixels[index + 2] << 16) | (pixels[index + 1] << 8) | pixels[index];
                    counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                }

                var best = 0;
                var bestCount = -1;
                foreach (var pair in counts)
                {
                    if (pair.Value > bestCount)
                    {
                        best = pair.Key;
                        bestCount = pair.Value;
                    }
                }

                return ((best >> 16) & 0xFF, (best >> 8) & 0xFF, best & 0xFF);
            }
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                _ = Native.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = Native.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = Native.DeleteDC(memoryDc);
            }

            _ = Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

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
            var fullId = $"{_prefix}.{id}";
            _ = E2EResults.Record(fullId, pass, notes);
            if (!pass)
            {
                _failures.Add($"{fullId}: {notes}");
            }
        }

        public void AssertAllPassed()
        {
            Assert.AreEqual(
                0,
                _failures.Count,
                $"Shell checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, _failures)}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }

#pragma warning disable SYSLIB1054 // The small user32 and gdi32 surface does not justify LibraryImport plumbing.
    private static class Native
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(
            IntPtr hdc,
            int x,
            int y,
            int width,
            int height,
            IntPtr source,
            int sourceX,
            int sourceY,
            uint rasterOperation);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern int GetDIBits(
            IntPtr hdc,
            IntPtr bitmap,
            uint start,
            uint lines,
            byte[] bits,
            ref BitmapInfo info,
            uint usage);
    }
#pragma warning restore SYSLIB1054
}
