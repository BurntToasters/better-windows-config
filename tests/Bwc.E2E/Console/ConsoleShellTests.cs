using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// Drives the console shell inside BwcGpedit.exe (the scaffold host: two tree roots, an empty result pane, a status bar
/// that shows the last command or context request). The shell is the shared MMC-style frame, so these tests stand for
/// Local Security Policy as well. Failure modes: docs/failure-modes/console-shell.md. Every test writes a measurements
/// file and a window picture to artifacts/e2e/visual/console-shell, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class ConsoleShellTests
{
    private const int ViewMenuCommand = 59003;
    private const int ViewToolbarCommand = 1002;
    private const int ViewStatusBarCommand = 1003;
    private const string ComputerNode = "Computer Configuration";
    private const string UserNode = "User Configuration";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_Layout()
    {
        var checks = new ShellChecks("layout");
        using ShellRun run = ShellRun.Open("layout");
        run.Measure("expectedTreeLogical", 156);

        Rectangle? tree = run.Rect(ShellRun.TreePaneId);
        Rectangle? splitter = run.Rect(ShellRun.SplitterId);
        Rectangle? result = run.Rect(ShellRun.ResultPaneId);
        Rectangle? status = run.Rect(ShellRun.StatusBarId);
        Rectangle? toolbar = run.Rect(ShellRun.ToolbarId);
        Rectangle? menu = run.Rect(ShellRun.MenuBarId);
        checks.Record("parts", tree is not null && splitter is not null && result is not null && status is not null && toolbar is not null && menu is not null,
            $"tree={tree}, splitter={splitter}, result={result}, status={status}, toolbar={toolbar}, menu={menu}.");
        if (tree is null || splitter is null || result is null || status is null || toolbar is null || menu is null)
        {
            run.WriteArtifacts("console-shell-layout.png");
            checks.AssertAllPassed();
            return;
        }

        double treeLogical = run.Logical(tree.Value.Width);
        checks.Record(
            "treeWidth",
            Near(treeLogical, 156, 1.5),
            $"tree pane {tree.Value.Width} px = {treeLogical:0.##} DIP at {run.Dpi} DPI; classic is 156 DIP (312 px at 192 DPI).");
        run.Measure("treePhysical", tree.Value.Width);
        run.Measure("treeLogical", treeLogical);

        double splitterLogical = run.Logical(splitter.Value.Width);
        checks.Record("splitterWidth", Near(splitterLogical, 3, 1), $"splitter {splitter.Value.Width} px = {splitterLogical:0.##} DIP; classic is 3 DIP.");
        checks.Record(
            "splitterBetween",
            splitter.Value.Left >= tree.Value.Right - 1 && result.Value.Left >= splitter.Value.Right - 1,
            $"tree right={tree.Value.Right}, splitter={splitter.Value.Left}..{splitter.Value.Right}, result left={result.Value.Left}.");

        double statusLogical = run.Logical(status.Value.Height);
        checks.Record("statusHeight", Near(statusLogical, 23, 1.5), $"status bar {status.Value.Height} px = {statusLogical:0.##} DIP; classic is 23 DIP.");
        checks.Record(
            "statusBelowPanes",
            status.Value.Top >= tree.Value.Bottom - 1 && status.Value.Top >= result.Value.Bottom - 1,
            $"status top={status.Value.Top}, tree bottom={tree.Value.Bottom}, result bottom={result.Value.Bottom}.");

        AutomationElement? statusElement = run.Find(ShellRun.StatusBarId);
        checks.Record(
            "statusControlType",
            statusElement?.ControlType == ControlType.StatusBar,
            $"status bar control type={statusElement?.ControlType}; classic is a StatusBar with id StatusBar.");
        AutomationElement? toolbarElement = run.Find(ShellRun.ToolbarId);
        checks.Record("toolbarControlType", toolbarElement?.ControlType == ControlType.ToolBar, $"toolbar control type={toolbarElement?.ControlType}; classic is a ToolBar.");
        AutomationElement? treeElement = run.Find(ShellRun.TreeControlId);
        checks.Record(
            "treeControlType",
            treeElement?.ControlType == ControlType.Tree && ShellRun.SafeName(treeElement) == "Console Embedded Scope",
            $"tree control type={treeElement?.ControlType}, name='{ShellRun.SafeName(treeElement)}'; classic is a Tree named by an internal caption.");

        double topBand = run.Logical(tree.Value.Top - run.WindowRect().Top);
        run.Measure("topBandWithTitleLogical", topBand);
        double bands = run.Logical(tree.Value.Top - menu.Value.Top);
        checks.Record("menuAndToolbarBands", Near(bands, 52, 3), $"menu bar plus toolbar band = {bands:0.##} DIP; the probed classic rebar is 52 DIP.");

        string[] expectedMenus = ["File", "Action", "View", "Help"];
        int[] menuIds = [59001, 59002, 59003, 59004];
        var menuNames = new List<string>();
        foreach (int id in menuIds)
        {
            menuNames.Add(ShellRun.SafeName(run.Find("MenuCommand_" + id)));
        }

        checks.Record("menuNames", menuNames.SequenceEqual(expectedMenus), $"menu bar items=[{string.Join(", ", menuNames)}]; classic is File, Action, View, Help.");
        run.Measure("menuNames", menuNames);

        string[] expectedNames =
        [
            "Back", "Forward", "Up One Level", "Show/Hide Console Tree", "Properties", "Refresh", "Export List", "Help",
        ];
        var buttonNames = new List<string>();
        var buttonRects = new List<Rectangle>();
        foreach (string id in ShellRun.StandardButtonIds)
        {
            AutomationElement? button = run.Find(ShellRun.ButtonId(id));
            buttonNames.Add(ShellRun.SafeName(button));
            if (button is not null && button.ControlType == ControlType.Button)
            {
                buttonRects.Add(button.BoundingRectangle);
            }
        }

        checks.Record("buttonNames", buttonNames.SequenceEqual(expectedNames), $"toolbar buttons=[{string.Join(", ", buttonNames)}].");
        bool ordered = buttonRects.Count == expectedNames.Length;
        for (int i = 1; ordered && i < buttonRects.Count; i++)
        {
            ordered = buttonRects[i].Left > buttonRects[i - 1].Left;
        }

        checks.Record("buttonsAreButtons", ordered, "every standard toolbar item is a Button, left to right in the standard order.");
        if (buttonRects.Count > 0)
        {
            double w = run.Logical(buttonRects[0].Width);
            double h = run.Logical(buttonRects[0].Height);
            checks.Record("buttonSize", Near(w, 23, 1) && Near(h, 22, 1), $"toolbar button {w:0.##} by {h:0.##} DIP; classic is 23 by 22.");
        }

        string toolbarHelp = run.HelpTextOf(ShellRun.ToolbarId);
        int expectedPixels = (int)Math.Round(16 * run.Scale, MidpointRounding.AwayFromZero);
        checks.Record(
            "iconSource",
            toolbarHelp.Contains("IconSource=mmc.exe#202", StringComparison.Ordinal) && toolbarHelp.Contains($"IconPx={expectedPixels}", StringComparison.Ordinal),
            $"toolbar icon report '{toolbarHelp}'; expected the strip of mmc.exe at {expectedPixels} px.");
        bool allModule = ShellRun.StandardButtonIds.All(id => run.HelpTextOf(ShellRun.ButtonId(id)) == "Icon=mmc.exe#202");
        checks.Record("buttonIcons", allModule, "every standard button shows the image from mmc.exe, not the fallback glyph.");

        AutomationElement[] rows = run.Session.Window.FindAllDescendants(cf => cf.ByName(ComputerNode))
            .Concat(run.Session.Window.FindAllDescendants(cf => cf.ByName(UserNode))).ToArray();
        checks.Record("treeRoots", rows.Length >= 2, $"tree rows named '{ComputerNode}' or '{UserNode}': {rows.Length}.");

        checks.Record("statusEmpty", run.StatusText().Length == 0, $"status text at start='{run.StatusText()}'.");

        run.Measure("rects", new Dictionary<string, string>
        {
            ["tree"] = tree.Value.ToString(),
            ["splitter"] = splitter.Value.ToString(),
            ["result"] = result.Value.ToString(),
            ["status"] = status.Value.ToString(),
            ["toolbar"] = toolbar.Value.ToString(),
            ["menu"] = menu.Value.ToString(),
        });
        run.Measure("toolbarHelp", toolbarHelp);
        run.Measure("note", "Only the current system DPI can be exercised; the logical widths are the same at every scale.");
        run.WriteArtifacts("console-shell-layout.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_SplitterDragAndLimits()
    {
        var checks = new ShellChecks("splitter");
        using ShellRun run = ShellRun.Open("splitter");
        int width = Math.Min(1500, ShellRun.ScreenWidth - 300);
        run.PlaceWindow(40, 40, width, 900);

        double Tree() => run.Logical(run.Rect(ShellRun.TreePaneId)?.Width ?? -1);
        double Result() => run.Logical(run.Rect(ShellRun.ResultPaneId)?.Width ?? -1);
        Point SplitterCenter()
        {
            Rectangle r = run.Rect(ShellRun.SplitterId) ?? throw new InvalidOperationException("The splitter is not showing.");
            return new Point(r.Left + (r.Width / 2), r.Top + (r.Height / 2));
        }

        double start = Tree();
        double total = start + 3 + Result();
        checks.Record("startWidth", Near(start, 156, 1.5), $"tree at start={start:0.##} DIP.");
        run.Measure("startTree", start);
        run.Measure("totalMainWidth", total);

        // A drag to the right widens the tree by the distance moved.
        Point center = SplitterCenter();
        ShellRun.DragStart(center, (int)Math.Round(100 * run.Scale));
        ShellRun.DragEnd();
        double widened = Tree();
        checks.Record("dragRight", Near(widened, 256, 2), $"tree after a 100 DIP drag right={widened:0.##} DIP; expected 256.");
        run.Measure("afterDragRight", widened);

        // A drag far past the left edge stops at the minimum width, never at zero.
        center = SplitterCenter();
        ShellRun.DragStart(center, -(int)Math.Round((widened + 400) * run.Scale));
        ShellRun.DragEnd();
        double narrowest = Tree();
        checks.Record("dragLeftLimit", Near(narrowest, 48, 2), $"tree after a drag far left={narrowest:0.##} DIP; the minimum is 48.");
        run.Measure("afterDragFarLeft", narrowest);

        // A drag past the right edge of the window leaves the result pane its minimum.
        center = SplitterCenter();
        ShellRun.DragStart(center, ShellRun.ScreenWidth - 2 - center.X);
        ShellRun.DragEnd();
        double widest = Tree();
        double resultAtLimit = Result();
        checks.Record(
            "dragRightLimit",
            Near(widest, total - 51, 2) && resultAtLimit >= 46,
            $"tree after a drag past the window={widest:0.##} DIP, result pane={resultAtLimit:0.##} DIP; expected tree {total - 51:0.##} and result at least 48.");
        run.Measure("afterDragFarRight", widest);
        run.Measure("resultAtRightLimit", resultAtLimit);

        // Escape during a drag puts back the width from before the drag.
        double before = Tree();
        center = SplitterCenter();
        ShellRun.DragStart(center, -(int)Math.Round(120 * run.Scale));
        double during = Tree();
        run.Tap(ShellRun.VkEscape);
        ShellRun.DragEnd();
        double after = Tree();
        checks.Record(
            "escapeCancels",
            during < before - 50 && Near(after, before, 2),
            $"tree before={before:0.##}, during the drag={during:0.##}, after Escape={after:0.##} DIP.");
        run.Measure("escapeCancel", new[] { before, during, after });

        run.WriteArtifacts("console-shell-splitter.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_WindowNarrowerThanTree()
    {
        var checks = new ShellChecks("narrow");
        using ShellRun run = ShellRun.Open("narrow");
        run.PlaceWindow(40, 40, 1400, 900);
        double Tree() => run.Logical(run.Rect(ShellRun.TreePaneId)?.Width ?? -1);
        double Main() => Tree() + 3 + run.Logical(run.Rect(ShellRun.ResultPaneId)?.Width ?? -1);

        double wide = Tree();
        checks.Record("wideWidth", Near(wide, 156, 1.5), $"tree in the wide window={wide:0.##} DIP.");

        run.PlaceWindow(40, 40, (int)Math.Round(200 * run.Scale), 900);
        double total = Main();
        double narrow = Tree();
        run.Measure("narrowMain", total);
        run.Measure("narrowTree", narrow);
        checks.Record("precondition", total < 207, $"the narrow window gives {total:0.##} DIP of panes; it must be below 207 (156 + 3 + 48) for the check to mean anything.");
        checks.Record(
            "treeClamped",
            narrow < 154 && Near(narrow, Math.Max(48, total - 51), 2),
            $"tree in the narrow window={narrow:0.##} DIP of {total:0.##}; expected {Math.Max(48, total - 51):0.##}.");
        run.WriteArtifacts("console-shell-narrow.png");

        run.PlaceWindow(40, 40, 1400, 900);
        double restored = Tree();
        checks.Record("restored", Near(restored, 156, 1.5), $"tree after the window grew back={restored:0.##} DIP; the chosen width is 156.");
        run.Measure("restoredTree", restored);

        run.WriteArtifacts("console-shell-narrow-restored.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_FocusCycle()
    {
        var checks = new ShellChecks("focus");
        using ShellRun run = ShellRun.Open("focus");
        run.ClickTreeNode(ComputerNode);
        checks.Record("startsOnTree", ShellRun.WaitUntil(() => run.FocusedPane() == "tree"), $"focused pane after a click on the tree={run.FocusedPane()}.");

        string[] F6(int count, ushort modifier = 0) => Press(run, ShellRun.VkF6, count, modifier);
        string[] Tab(int count, ushort modifier = 0) => Press(run, ShellRun.VkTab, count, modifier);

        string[] forward = F6(4);
        checks.Record("f6Order", forward.SequenceEqual(["result", "tree", "result", "tree"]), $"F6 x4 from the tree visited [{string.Join(",", forward)}].");
        string[] tabs = Tab(4);
        checks.Record("tabOrder", tabs.SequenceEqual(["result", "tree", "result", "tree"]), $"Tab x4 from the tree visited [{string.Join(",", tabs)}].");
        string[] backwardF6 = F6(2, RawKeyboard.Shift);
        checks.Record("shiftF6Order", backwardF6.SequenceEqual(["result", "tree"]), $"Shift+F6 x2 from the tree visited [{string.Join(",", backwardF6)}].");
        string[] backwardTab = Tab(2, RawKeyboard.Shift);
        checks.Record("shiftTabOrder", backwardTab.SequenceEqual(["result", "tree"]), $"Shift+Tab x2 from the tree visited [{string.Join(",", backwardTab)}].");

        string[] many = Tab(6);
        bool only = many.All(p => p is "tree" or "result");
        checks.Record("onlyTwoPanes", only, $"Tab x6 visited [{string.Join(",", many)}]; the toolbar, splitter and menu bar are outside the cycle.");

        // F10 puts the focus on the menu bar; Tab then starts the cycle at the tree.
        run.Tap(ShellRun.VkF10);
        bool onMenu = ShellRun.WaitUntil(() => run.FocusedPane() == "menu");
        checks.Record("f10FocusesMenu", onMenu, $"focused pane after F10={run.FocusedPane()}, id={run.FocusedId()}.");
        run.Tap(ShellRun.VkTab);
        bool toTree = ShellRun.WaitUntil(() => run.FocusedPane() == "tree");
        checks.Record("tabFromMenuGoesToTree", toTree, $"focused pane after Tab from the menu bar={run.FocusedPane()}.");

        run.Measure("f6", forward);
        run.Measure("tab", tabs);
        run.Measure("shiftF6", backwardF6);
        run.Measure("shiftTab", backwardTab);
        run.Measure("tabSix", many);
        run.WriteArtifacts("console-shell-focus.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_MenuKeyboard()
    {
        var checks = new ShellChecks("menu");
        using ShellRun run = ShellRun.Open(
            "menu",
            new Dictionary<string, string> { ["BWC_E2E_CONSOLE_COLLIDE"] = "1" });
        run.ClickTreeNode(ComputerNode);
        checks.Record("startsOnTree", ShellRun.WaitUntil(() => run.FocusedPane() == "tree"), $"focused pane before the keys={run.FocusedPane()}.");

        // Alt pressed and released alone puts the focus on the first menu, and Escape gives it back to the tree.
        _ = ControlTestSupport.EnsureForeground(run.Session);
        RawKeyboard.Tap(RawKeyboard.Alt);
        bool altFile = ShellRun.WaitUntil(() => run.FocusedId() == "MenuCommand_59001");
        checks.Record("altFocusesFile", altFile, $"focused id after Alt={run.FocusedId()}; expected the File menu (MenuCommand_59001).");
        run.Tap(ShellRun.VkEscape);
        bool back = ShellRun.WaitUntil(() => run.FocusedPane() == "tree");
        checks.Record("escapeRestoresTree", back, $"focused pane after Escape={run.FocusedPane()}.");

        run.Tap(ShellRun.VkF10);
        bool f10File = ShellRun.WaitUntil(() => run.FocusedId() == "MenuCommand_59001");
        checks.Record("f10FocusesFile", f10File, $"focused id after F10={run.FocusedId()}.");
        run.Tap(ShellRun.VkEscape);
        _ = ShellRun.WaitUntil(() => run.FocusedPane() == "tree");

        // Alt+V opens the View menu with the host's entries and the frame's.
        run.Chord(RawKeyboard.Alt, ShellRun.VkV);
        AutomationElement? view = run.Find("MenuCommand_59003");
        bool viewOpen = ShellRun.WaitUntil(() => ExpandState(view) == ExpandCollapseState.Expanded);
        checks.Record("altVOpensView", viewOpen, $"View expand state after Alt+V={ExpandState(view)}.");
        string[] viewItems = ["MenuCommand_1001", "MenuCommand_1002", "MenuCommand_1003", "MenuCommand_13231"];
        bool itemsThere = ShellRun.WaitUntil(() => viewItems.All(id => run.Find(id) is not null));
        checks.Record("viewItems", itemsThere, $"View entries found: {string.Join(", ", viewItems.Select(id => id + "=" + (run.Find(id) is not null)))}.");
        run.WriteArtifacts("console-shell-menu-view.png");
        run.Tap(ShellRun.VkEscape);
        run.Tap(ShellRun.VkEscape);
        _ = ShellRun.WaitUntil(() => ExpandState(view) != ExpandCollapseState.Expanded);

        // A host entry with the letter of Exit wins it: Alt+F then X runs the host's entry and the window stays open.
        // The second Escape above already gave the focus back to the tree. A mouse click here would race the chord: the
        // window hook sees Alt+F at once, while the click reaches XAML late and dismisses the popup the chord just opened
        // (docs/failure-modes/menus.md, "A late pointer press closes a popup the keyboard opened"). So the test waits for
        // the tree focus and clicks only when it is not there.
        if (!ShellRun.WaitUntil(() => run.FocusedPane() == "tree", TimeSpan.FromSeconds(3)))
        {
            run.ClickTreeNode(ComputerNode);
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        run.Chord(RawKeyboard.Alt, ShellRun.VkF);
        AutomationElement? file = run.Find("MenuCommand_59001");
        bool fileOpen = ShellRun.WaitUntil(() => ExpandState(file) == ExpandCollapseState.Expanded);
        checks.Record("altFOpensFile", fileOpen, $"File expand state after Alt+F={ExpandState(file)}.");
        string exitKey = run.Find("MenuCommand_57665")?.Properties.AccessKey.ValueOrDefault ?? string.Empty;
        string extraKey = run.Find("MenuCommand_1100")?.Properties.AccessKey.ValueOrDefault ?? string.Empty;
        run.Measure("accessKeys", new Dictionary<string, string> { ["exit"] = exitKey, ["extra"] = extraKey });
        run.Tap(ShellRun.VkX);
        bool ran = ShellRun.WaitUntil(() => run.StatusText() == "Xtra");
        checks.Record("hostEntryWinsKey", ran, $"status text after Alt+F, X='{run.StatusText()}'; the host entry E&xtra must run, not Exit.");
        checks.Record("windowStillOpen", run.Find(ShellRun.ShellId) is not null, "the window is still open after Alt+F, X.");

        run.WriteArtifacts("console-shell-menu.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_ShowHide()
    {
        var checks = new ShellChecks("showhide");
        using ShellRun run = ShellRun.Open("showhide");
        run.ClickTreeNode(ComputerNode);
        Rectangle? treeBefore = run.Rect(ShellRun.TreePaneId);
        Rectangle? resultBefore = run.Rect(ShellRun.ResultPaneId);
        checks.Record("treeShowing", treeBefore is not null && resultBefore is not null, $"tree={treeBefore}, result={resultBefore}.");
        if (treeBefore is null || resultBefore is null)
        {
            checks.AssertAllPassed();
            return;
        }

        int shellWidth = treeBefore.Value.Width + (int)Math.Round(3 * run.Scale) + resultBefore.Value.Width;

        // Show/Hide Console Tree with the focus in the tree: the focus moves to the result pane.
        Invoke(run, ShellRun.ButtonId("ShowHideConsoleTree"));
        bool hidden = ShellRun.WaitUntil(() => run.IsHidden(ShellRun.TreePaneId) && run.IsHidden(ShellRun.SplitterId));
        checks.Record("treeHidden", hidden, "the tree pane and the splitter are gone after Show/Hide Console Tree.");
        Rectangle? resultHidden = run.Rect(ShellRun.ResultPaneId);
        checks.Record(
            "resultFills",
            resultHidden is not null && resultHidden.Value.Width >= shellWidth - 3,
            $"result pane width={resultHidden?.Width} px, the panes were {shellWidth} px wide.");
        checks.Record("focusMovedToResult", ShellRun.WaitUntil(() => run.FocusedPane() == "result"), $"focused pane after hiding the tree={run.FocusedPane()}.");

        // With one pane showing, F6, Tab and Shift+Tab keep the focus there and never reach the hidden tree.
        string[] f6 = Press(run, ShellRun.VkF6, 2);
        string[] tab = Press(run, ShellRun.VkTab, 2);
        string[] back = Press(run, ShellRun.VkTab, 2, RawKeyboard.Shift);
        bool stays = f6.Concat(tab).Concat(back).All(p => p == "result");
        checks.Record("cycleStaysOnResult", stays, $"F6, Tab and Shift+Tab with the tree hidden visited [{string.Join(",", f6.Concat(tab).Concat(back))}].");
        run.WriteArtifacts("console-shell-tree-hidden.png");

        Invoke(run, ShellRun.ButtonId("ShowHideConsoleTree"));
        Rectangle? treeBack = null;
        bool shown = ShellRun.WaitUntil(() => (treeBack = run.Rect(ShellRun.TreePaneId)) is not null);
        checks.Record(
            "treeShownAgain",
            shown && treeBack is not null && Near(run.Logical(treeBack.Value.Width), 156, 1.5),
            $"tree width after showing={run.Logical(treeBack?.Width ?? -1):0.##} DIP; the chosen width comes back.");
        string[] fromResult = Press(run, ShellRun.VkF6, 1);
        checks.Record("f6ReachesTreeAgain", fromResult[0] == "tree", $"F6 from the result pane with the tree back visited [{fromResult[0]}].");

        // View > Toolbar hides the toolbar band and the panes move up.
        Rectangle? treeWithToolbar = run.Rect(ShellRun.TreePaneId);
        MenuPath(run, ViewMenuCommand, ViewToolbarCommand);
        bool toolbarGone = ShellRun.WaitUntil(() => run.IsHidden(ShellRun.ToolbarId));
        checks.Record("toolbarHidden", toolbarGone, "the toolbar is gone after View > Toolbar.");
        Rectangle? treeWithoutToolbar = run.Rect(ShellRun.TreePaneId);
        double moved = treeWithToolbar is null || treeWithoutToolbar is null ? 0 : run.Logical(treeWithToolbar.Value.Top - treeWithoutToolbar.Value.Top);
        checks.Record("panesMovedUp", moved >= 25, $"the tree moved up by {moved:0.##} DIP when the toolbar went away; the band is 30 DIP.");

        // View > Status Bar hides the bar; text set while it is hidden shows when it comes back.
        Rectangle? treeWithStatus = run.Rect(ShellRun.TreePaneId);
        MenuPath(run, ViewMenuCommand, ViewStatusBarCommand);
        bool statusGone = ShellRun.WaitUntil(() => run.IsHidden(ShellRun.StatusBarId));
        checks.Record("statusHidden", statusGone, "the status bar is gone after View > Status Bar.");
        Rectangle? treeWithoutStatus = run.Rect(ShellRun.TreePaneId);
        double grew = treeWithStatus is null || treeWithoutStatus is null ? 0 : run.Logical(treeWithoutStatus.Value.Bottom - treeWithStatus.Value.Bottom);
        checks.Record("panesGrewDown", grew >= 20, $"the tree grew by {grew:0.##} DIP when the status bar went away; the bar is 23 DIP.");

        run.Chord(RawKeyboard.Shift, ShellRun.VkF10);
        MenuPath(run, ViewMenuCommand, ViewStatusBarCommand);
        bool statusBack = ShellRun.WaitUntil(() => !run.IsHidden(ShellRun.StatusBarId));
        string text = run.StatusText();
        checks.Record(
            "textSetWhileHidden",
            statusBack && text.StartsWith("Context menu:", StringComparison.Ordinal),
            $"status text after showing the bar='{text}'; it was set while the bar was hidden.");

        run.Measure("shellWidthPx", shellWidth);
        run.Measure("toolbarMovedDip", moved);
        run.Measure("statusGrewDip", grew);
        run.Measure("statusAfterShow", text);
        run.WriteArtifacts("console-shell-showhide.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_ToolbarStates()
    {
        var checks = new ShellChecks("toolbar");
        using ShellRun run = ShellRun.Open("toolbar");
        run.ClickTreeNode(ComputerNode);

        var initial = new Dictionary<string, bool>
        {
            ["Back"] = false,
            ["Forward"] = false,
            ["UpOneLevel"] = false,
            ["ShowHideConsoleTree"] = true,
            ["Properties"] = true,
            ["Refresh"] = true,
            ["ExportList"] = false,
            ["Help"] = false,
        };
        ReportStates(checks, run, "initial", initial);

        // Selecting the second root gives Back; Alt+Left runs it and the host moves the selection.
        run.Tap(ShellRun.VkDown);
        checks.Record("selectedUser", ShellRun.WaitUntil(() => run.SelectedTreeNode() == UserNode), $"selected node after Down='{run.SelectedTreeNode()}'.");
        ReportStates(checks, run, "afterSelect", new Dictionary<string, bool> { ["Back"] = true, ["Forward"] = false });

        run.Chord(RawKeyboard.Alt, ShellRun.VkLeft);
        checks.Record("altLeftIsBack", ShellRun.WaitUntil(() => run.StatusText() == "Command: Back" && run.SelectedTreeNode() == ComputerNode),
            $"after Alt+Left status='{run.StatusText()}', selected='{run.SelectedTreeNode()}'.");
        ReportStates(checks, run, "afterBack", new Dictionary<string, bool> { ["Back"] = false, ["Forward"] = true });

        run.Chord(RawKeyboard.Alt, ShellRun.VkRight);
        checks.Record("altRightIsForward", ShellRun.WaitUntil(() => run.StatusText() == "Command: Forward" && run.SelectedTreeNode() == UserNode),
            $"after Alt+Right status='{run.StatusText()}', selected='{run.SelectedTreeNode()}'.");
        ReportStates(checks, run, "afterForward", new Dictionary<string, bool> { ["Back"] = true, ["Forward"] = false });

        // A key whose command is disabled raises nothing: Forward is disabled here, so the status stays.
        run.Chord(RawKeyboard.Shift, ShellRun.VkF10);
        string marker = run.StatusText();
        run.Chord(RawKeyboard.Alt, ShellRun.VkRight);
        checks.Record(
            "disabledKeyRaisesNothing",
            run.StatusText() == marker && run.SelectedTreeNode() == UserNode,
            $"status before='{marker}' after Alt+Right with Forward disabled='{run.StatusText()}', selected='{run.SelectedTreeNode()}'.");

        // The same commands from the buttons.
        Invoke(run, ShellRun.ButtonId("Back"));
        checks.Record("backButton", ShellRun.WaitUntil(() => run.StatusText() == "Command: Back" && run.SelectedTreeNode() == ComputerNode),
            $"after the Back button status='{run.StatusText()}', selected='{run.SelectedTreeNode()}'.");
        Invoke(run, ShellRun.ButtonId("Properties"));
        checks.Record("propertiesButton", ShellRun.WaitUntil(() => run.StatusText() == "Command: Properties"), $"after Properties status='{run.StatusText()}'.");

        AutomationElement? help = run.Find(ShellRun.ButtonId("Help"));
        bool invokable = help is not null && help.Patterns.Invoke.IsSupported && help.IsEnabled;
        checks.Record("disabledButtonNotInvokable", !invokable, $"Help enabled={help?.IsEnabled}; a disabled button cannot be invoked.");

        run.WriteArtifacts("console-shell-toolbar.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_ContextMenuKey()
    {
        var checks = new ShellChecks("context");
        using ShellRun run = ShellRun.Open("context");
        run.ClickTreeNode(ComputerNode);

        run.Chord(RawKeyboard.Shift, ShellRun.VkF10);
        string first = run.StatusText();
        checks.Record(
            "shiftF10InTree",
            first == $"Context menu: pane=Tree source=key node={ComputerNode} n=1",
            $"status after Shift+F10 in the tree='{first}'.");

        run.Tap(ShellRun.VkApps);
        string second = run.StatusText();
        checks.Record(
            "appsKeyInTree",
            second == $"Context menu: pane=Tree source=key node={ComputerNode} n=2",
            $"status after the context-menu key in the tree='{second}'; each key raises exactly one event.");

        run.Tap(ShellRun.VkF6);
        checks.Record("onResult", ShellRun.WaitUntil(() => run.FocusedPane() == "result"), $"focused pane after F6={run.FocusedPane()}.");
        run.Tap(ShellRun.VkApps);
        string third = run.StatusText();
        checks.Record("appsKeyInResult", third == "Context menu: pane=Result source=key n=3", $"status after the key in the result pane='{third}'.");

        AutomationElement? row = run.Session.Window.FindFirstDescendant(cf => cf.ByName(ComputerNode));
        row?.RightClick();
        string fourth = ShellRun.WaitUntil(() => run.StatusText().Contains("source=pointer", StringComparison.Ordinal)) ? run.StatusText() : run.StatusText();
        checks.Record(
            "rightClickInTree",
            fourth == $"Context menu: pane=Tree source=pointer node={ComputerNode} n=4",
            $"status after a right click on the tree row='{fourth}'.");

        Rectangle? result = run.Rect(ShellRun.ResultPaneId);
        if (result is not null)
        {
            var point = new Point(result.Value.Left + (result.Value.Width / 2), result.Value.Top + (result.Value.Height / 2));
            FlaUI.Core.Input.Mouse.RightClick(point);
        }

        string fifth = ShellRun.WaitUntil(() => run.StatusText().EndsWith("n=5", StringComparison.Ordinal)) ? run.StatusText() : run.StatusText();
        checks.Record("rightClickInResult", fifth == "Context menu: pane=Result source=pointer n=5", $"status after a right click in the result pane='{fifth}'.");

        run.Measure("statuses", new[] { first, second, third, fourth, fifth });
        run.WriteArtifacts("console-shell-context.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_StatusBar()
    {
        var checks = new ShellChecks("status");
        string longText = string.Concat(Enumerable.Repeat("A very long status text. ", 200));
        using ShellRun run = ShellRun.Open(
            "status",
            new Dictionary<string, string> { ["BWC_E2E_CONSOLE_STATUS_TEXT"] = longText });
        Rectangle? bar = run.Rect(ShellRun.StatusBarId);
        Rectangle? pane = run.Rect(ShellRun.StatusPaneId);
        checks.Record("present", bar is not null && pane is not null, $"bar={bar}, pane={pane}.");
        if (bar is null || pane is null)
        {
            checks.AssertAllPassed();
            return;
        }

        double height = run.Logical(bar.Value.Height);
        checks.Record("heightFixed", Near(height, 23, 1.5), $"status bar height with {longText.Length} characters={height:0.##} DIP; it stays 23.");
        checks.Record(
            "paneInsideBar",
            pane.Value.Left >= bar.Value.Left - 1 && pane.Value.Right <= bar.Value.Right + 1 && pane.Value.Bottom <= bar.Value.Bottom + 1,
            $"pane={pane.Value}, bar={bar.Value}; the text is trimmed inside the bar.");
        string name = run.StatusText();
        checks.Record("fullTextKept", name == longText, $"accessible text length={name.Length}, expected {longText.Length}.");
        Rectangle window = run.WindowRect();
        checks.Record("windowNotWidened", bar.Value.Right <= window.Right, $"bar right={bar.Value.Right}, window right={window.Right}.");

        run.Measure("barHeightDip", height);
        run.Measure("paneRect", pane.Value.ToString());
        run.Measure("textLength", name.Length);
        run.WriteArtifacts("console-shell-status.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Shell_ToolbarIconFallback()
    {
        var checks = new ShellChecks("fallback");
        string missing = Path.Combine(Harness.GuiGate.Config.DataRootFor("console-fallback"), "no-such-module.exe");
        using ShellRun run = ShellRun.Open(
            "fallback",
            new Dictionary<string, string> { ["BWC_E2E_CONSOLE_ICON_MODULE"] = missing });

        string help = run.HelpTextOf(ShellRun.ToolbarId);
        checks.Record("toolbarReportsFallback", help.Contains("IconSource=fallback", StringComparison.Ordinal), $"toolbar icon report '{help}'.");
        bool allFallback = ShellRun.StandardButtonIds.All(id => run.HelpTextOf(ShellRun.ButtonId(id)) == "Icon=fallback");
        checks.Record("everyButtonFallback", allFallback, "every standard button shows a font glyph when the module is missing.");

        string[] expectedNames =
        [
            "Back", "Forward", "Up One Level", "Show/Hide Console Tree", "Properties", "Refresh", "Export List", "Help",
        ];
        var names = ShellRun.StandardButtonIds.Select(id => ShellRun.SafeName(run.Find(ShellRun.ButtonId(id)))).ToList();
        checks.Record("namesKept", names.SequenceEqual(expectedNames), $"button names=[{string.Join(", ", names)}].");
        checks.Record(
            "statesKept",
            run.ButtonEnabled("ShowHideConsoleTree") && run.ButtonEnabled("Properties") && !run.ButtonEnabled("Back"),
            $"ShowHide={run.ButtonEnabled("ShowHideConsoleTree")}, Properties={run.ButtonEnabled("Properties")}, Back={run.ButtonEnabled("Back")}.");

        run.Measure("help", help);
        run.WriteArtifacts("console-shell-fallback.png");
        checks.AssertAllPassed();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;

    /// <summary>Presses a key count times and returns the pane focused after each press.</summary>
    private static string[] Press(ShellRun run, ushort key, int count, ushort modifier = 0)
    {
        var seen = new string[count];
        for (int i = 0; i < count; i++)
        {
            if (modifier == 0)
            {
                run.Tap(key);
            }
            else
            {
                run.Chord(modifier, key);
            }

            seen[i] = run.FocusedPane();
        }

        return seen;
    }

    private static void Invoke(ShellRun run, string automationId)
    {
        _ = ControlTestSupport.EnsureForeground(run.Session);
        AutomationElement element = run.Session.WaitForElement(automationId, ShellRun.UiTimeout);
        element.Patterns.Invoke.Pattern.Invoke();
        Thread.Sleep(ShellRun.KeySettle.Milliseconds);
    }

    private static void MenuPath(ShellRun run, params int[] commandIds)
    {
        using var driver = new EditingDriver(run.Session);
        driver.OpenMenu(commandIds);
        Thread.Sleep(ShellRun.KeySettle.Milliseconds);
    }

    private static ExpandCollapseState? ExpandState(AutomationElement? element)
    {
        try
        {
            return element?.Patterns.ExpandCollapse.IsSupported == true
                ? element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReportStates(ShellChecks checks, ShellRun run, string stage, Dictionary<string, bool> expected)
    {
        foreach ((string button, bool enabled) in expected)
        {
            bool actual = ShellRun.WaitUntil(() => run.ButtonEnabled(button) == enabled, TimeSpan.FromSeconds(3));
            checks.Record($"{stage}.{button}", actual, $"{button} enabled={run.ButtonEnabled(button)}, expected {enabled}.");
        }
    }
}
