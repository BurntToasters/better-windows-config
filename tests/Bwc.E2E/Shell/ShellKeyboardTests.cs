using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Shell;

/// <summary>
/// Keyboard and layout checks of the classic shell: the Tab, Shift+Tab, and F6 pane cycle, the
/// splitter as a non-tab-stop with mouse drag and Escape cancel, the address bar accelerators and
/// its Enter and Escape behavior, and View > Address Bar with the layout reflow.
/// </summary>
/// <remarks>
/// The classic cycle was read from the reference regedit (read-only): tree, list, address bar, tree.
/// Every test starts its own Registry Editor process. The Registry Editor is only navigated, never
/// edited. Captures and a measurements file are written to artifacts/e2e/visual/shell/.
/// </remarks>
[TestClass]
public sealed class ShellKeyboardTests
{
    private const string ShellId = "ClassicWindowShell";
    private const string MenuBarId = "MenuBarRoot";
    private const string AddressBarId = "AddressBar";
    private const string SplitterId = "Splitter";
    private const string TreePaneId = "TreePane";
    private const int MenuViewCommand = 514;
    private const int MenuViewSplitCommand = 670;
    private const string ListPaneId = "ListPane";
    private const string MenuBarItemClass = "Microsoft.UI.Xaml.Controls.MenuBarItem";
    private const string SummaryFileName = "measurements.json";

    // The minimum tree pane width in DIP, as ClassicWindowShell clamps it. Scaled by the window DPI in the checks.
    private const double MinTreePaneDip = 120;

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeySettle = TimeSpan.FromMilliseconds(300);
    private static readonly List<KeyValuePair<string, string>> Summary = [];
    private static readonly object SummaryLock = new();

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        WriteSummary();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_TabCyclesTreeListAddressBar()
    {
        var checks = new Checks("shell.tab");
        using var session = OpenBwc(checks, "shell-tab");
        using var automation = new UIA3Automation();

        bool reached = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", reached, $"focused pane before the cycle={FocusedPane(automation)}.");

        // Classic order from the tree: list, address bar, tree, list.
        var seen = PressSequence(session, automation, "TAB", 4);
        checks.Record(
            "tabOrder",
            string.Join(",", seen) == "list,address,tree,list",
            $"Tab x4 from the tree visited [{string.Join(",", seen)}]; classic order is list,address,tree,list.");
        Remember("tab.order", string.Join(",", seen));
        CaptureWindow(session, "shell-tab-address.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_F6CyclesPanesLikeTab()
    {
        var checks = new Checks("shell.f6");
        using var session = OpenBwc(checks, "shell-f6");
        using var automation = new UIA3Automation();

        bool reached = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", reached, $"focused pane before F6={FocusedPane(automation)}.");

        var seen = PressSequence(session, automation, "F6", 3);
        checks.Record(
            "f6Order",
            string.Join(",", seen) == "list,address,tree",
            $"F6 x3 from the tree visited [{string.Join(",", seen)}]; classic F6 (command 640) is the same cycle as Tab.");
        Remember("f6.order", string.Join(",", seen));
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_ShiftTabReversesCycle()
    {
        var checks = new Checks("shell.shifttab");
        using var session = OpenBwc(checks, "shell-shifttab");
        using var automation = new UIA3Automation();

        bool reached = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", reached, $"focused pane before Shift+Tab={FocusedPane(automation)}.");

        var seen = PressSequence(session, automation, "SHIFT+TAB", 3);
        checks.Record(
            "shiftTabOrder",
            string.Join(",", seen) == "address,list,tree",
            $"Shift+Tab x3 from the tree visited [{string.Join(",", seen)}]; the reverse of tree,list,address is address,list,tree.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_SplitterIsNotATabStop()
    {
        var checks = new Checks("shell.splitterTab");
        using var session = OpenBwc(checks, "shell-splittertab");
        using var automation = new UIA3Automation();

        bool reached = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", reached, $"focused pane before the splitter check={FocusedPane(automation)}.");

        // Six Tab presses cover two full cycles. The splitter is never focused by Tab.
        var seen = PressSequence(session, automation, "TAB", 6);
        bool visitedSplitter = seen.Contains("splitter");
        checks.Record(
            "notTabStop",
            !visitedSplitter,
            $"Tab x6 from the tree visited [{string.Join(",", seen)}]; the splitter must not be in the Tab order.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_AddressBarHiddenIsSkippedByTab()
    {
        var checks = new Checks("shell.hiddenTab");
        using var session = OpenBwc(checks, "shell-hiddentab");
        using var automation = new UIA3Automation();

        ToggleAddressBarFromMenu(session);
        var hidden = WaitUntil(() => AddressBarHidden(session), MenuTimeout);
        checks.Record("hidden", hidden, "View > Address Bar hid the address bar before the Tab check.");

        bool reached = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", reached, $"focused pane before the hidden-bar Tab check={FocusedPane(automation)}.");

        var seen = PressSequence(session, automation, "TAB", 2);
        checks.Record(
            "skipsHidden",
            string.Join(",", seen) == "list,tree",
            $"With the address bar hidden, Tab x2 from the tree visited [{string.Join(",", seen)}]; expected list,tree.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_CtrlLAndAltDFocusAddressBar()
    {
        var checks = new Checks("shell.accelerators");
        using var session = OpenBwc(checks, "shell-accelerators");
        using var automation = new UIA3Automation();

        bool reached = EnsurePane(session, automation, "tree", "TAB", 6);
        checks.Record("startsOnTree", reached, $"focused pane before Ctrl+L={FocusedPane(automation)}.");

        session.SendKeys("CONTROL+KEY_L");
        var ctrlL = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        checks.Record("ctrlL", ctrlL, $"focused pane after Ctrl+L={FocusedPane(automation)}; ACC-014 is command 672, the address bar.");

        session.SendKeys("ESCAPE");
        var back = WaitUntil(() => FocusedPane(automation) == "tree", UiTimeout);
        checks.Record("escapeReturnsToTree", back, $"focused pane after Escape in the address bar={FocusedPane(automation)}.");

        session.SendKeys("ALT+KEY_D");
        var altD = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        checks.Record("altD", altD, $"focused pane after Alt+D={FocusedPane(automation)}; ACC-013 is command 672, the address bar.");
        CaptureWindow(session, "shell-address-focused.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_AddressBarFocusHasNoAccentLine()
    {
        var checks = new Checks("shell.addressFlat");
        using var session = OpenBwc(checks, "shell-addressflat");
        using var automation = new UIA3Automation();

        session.SendKeys("CONTROL+KEY_L");
        _ = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        Thread.Sleep(300);

        var address = WaitForId(session, AddressBarId);
        if (address is null)
        {
            checks.Record("present", false, "The AddressBar element was not found.");
            checks.AssertAllPassed();
            return;
        }

        // The Fluent focus line is the accent blue. The flat classic edit has a gray or white bottom edge.
        Rectangle rect = address.BoundingRectangle;
        using Bitmap shot = CaptureScreen(rect);
        int bottomY = shot.Height - 1;
        bool blue = false;
        for (int x = 2; x < shot.Width - 2; x += 4)
        {
            Color c = shot.GetPixel(x, bottomY);
            if (c.B > c.R + 60 && c.B > c.G + 30)
            {
                blue = true;
                break;
            }
        }

        checks.Record(
            "noAccentLine",
            !blue,
            $"The focused address bar has {(blue ? "an accent blue" : "no accent blue")} along its bottom edge (rect {rect.Width}x{rect.Height} px).");
        Remember("address.focusedBottom", blue ? "accent" : "neutral");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_EnterNavigatesAndEscapeRestoresPath()
    {
        var checks = new Checks("shell.addressNav");
        using var session = OpenBwc(checks, "shell-addressnav");
        using var automation = new UIA3Automation();

        _ = EnsurePane(session, automation, "tree", "TAB", 6);
        session.SendKeys("CONTROL+KEY_L");
        var focusedAddress = WaitUntil(() => FocusedPane(automation) == "address", UiTimeout);
        checks.Record("focusBeforeTyping", focusedAddress, $"Focused pane after Ctrl+L={FocusedPane(automation)}; typing needs the address bar.");
        var committed = ValueOf(WaitForId(session, AddressBarId));

        // Enter with a path navigates the tree. The row named Software is then selected.
        session.SendKeys("CONTROL+KEY_A");
        session.SendKeys("Computer\\HKEY_CURRENT_USER\\Software");
        session.SendKeys("ENTER");
        var navigated = WaitUntil(() => SoftwareRowSelected(session), UiTimeout);
        checks.Record(
            "enterNavigates",
            navigated,
            $"After Enter with 'Computer\\HKEY_CURRENT_USER\\Software', the Software row selected={navigated}.");

        var afterEnter = ValueOf(WaitForId(session, AddressBarId));
        session.SendKeys("CONTROL+KEY_A");
        session.SendKeys("junk-text-not-committed");
        session.SendKeys("ESCAPE");
        var restored = WaitUntil(() => ValueOf(WaitForId(session, AddressBarId)) == afterEnter, UiTimeout);
        checks.Record(
            "escapeRestores",
            restored,
            $"Escape restored the address bar to '{ValueOf(WaitForId(session, AddressBarId))}', expected the committed '{afterEnter}' (was '{committed}' before Enter).");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_ViewAddressBarReflowsLayout()
    {
        var checks = new Checks("shell.reflow");
        using var session = OpenBwc(checks, "shell-reflow");

        double shownGap = GapBelowMenuBar(session);
        ToggleAddressBarFromMenu(session);
        _ = WaitUntil(() => AddressBarHidden(session), MenuTimeout);
        Thread.Sleep(300);
        double hiddenGap = GapBelowMenuBar(session);

        // Shown, the tree starts one address row below the menu bar. Hidden, it starts directly below the bar.
        checks.Record(
            "hiddenGapClosed",
            hiddenGap <= 1 && shownGap > 1,
            $"Gap between the menu bar and the tree pane: shown={shownGap:0.#} px, hidden={hiddenGap:0.#} px; the hidden gap must close.");

        ToggleAddressBarFromMenu(session);
        var shown = WaitUntil(() => !AddressBarHidden(session), MenuTimeout);
        Thread.Sleep(300);
        double reshownGap = GapBelowMenuBar(session);
        checks.Record(
            "shownAgain",
            shown && Math.Abs(reshownGap - shownGap) <= 1,
            $"After toggling back on, the gap is {reshownGap:0.#} px (shown before: {shownGap:0.#} px).");
        CaptureWindow(session, "shell-address-restored.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_SplitterDragClampsAndEscapeRestores()
    {
        var checks = new Checks("shell.splitterDrag");
        using var session = OpenBwc(checks, "shell-splitterdrag");
        var dpi = ControlTestSupport.WindowDpi(session);
        double dipPx = dpi / 96.0;
        int tolerance = (int)Math.Ceiling(dipPx);

        var splitter = WaitForId(session, SplitterId);
        var tree = WaitForId(session, TreePaneId);
        if (splitter is null || tree is null)
        {
            checks.Record("present", false, "The Splitter or the TreePane was not found.");
            checks.AssertAllPassed();
            return;
        }

        int before = tree.BoundingRectangle.Width;
        Point grip = Center(splitter.BoundingRectangle);

        // Hovering the splitter shows the classic east-west resize cursor. The cursor is set on a move, so small
        // moves inside the splitter repeat until the window has set it.
        bool sizeWe = false;
        for (var attempt = 0; attempt < 10 && !sizeWe; attempt++)
        {
            MoveCursor(new Point(grip.X + (attempt % 2), grip.Y));
            Thread.Sleep(200);
            sizeWe = CursorIsSizeWestEast();
        }

        checks.Record("cursorSizeWE", sizeWe, $"Cursor over the splitter at {grip} is the system SizeWE cursor={sizeWe}.");

        // Drag to the right, then press Escape before the button is released. The split must go back.
        DragSplitter(grip, (int)(60 * dipPx));
        int during = WaitForId(session, TreePaneId)?.BoundingRectangle.Width ?? 0;
        session.SendKeys("ESCAPE");
        Thread.Sleep(300);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(400);
        int after = WaitForId(session, TreePaneId)?.BoundingRectangle.Width ?? 0;
        checks.Record(
            "dragMoves",
            during > before + tolerance,
            $"Tree width before the drag {before} px, during the drag {during} px; splitter rect {splitter.BoundingRectangle}, grip {grip}.");
        checks.Record(
            "escapeRestores",
            Math.Abs(after - before) <= tolerance,
            $"Tree width after Escape and release {after} px, expected {before} px (tolerance {tolerance} px).");
        CaptureWindow(session, "shell-splitter-after-escape.png");

        // Drag far to the left. The tree pane stops at its minimum width, and the list pane keeps its width.
        var splitterNow = WaitForId(session, SplitterId);
        Point grip2 = Center(splitterNow?.BoundingRectangle ?? splitter.BoundingRectangle);
        DragSplitter(grip2, -(int)(1000 * dipPx));
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(400);
        int minimum = WaitForId(session, TreePaneId)?.BoundingRectangle.Width ?? 0;
        int expectedMinimum = (int)Math.Round(MinTreePaneDip * dipPx);
        checks.Record(
            "minimumClamp",
            Math.Abs(minimum - expectedMinimum) <= tolerance,
            $"Tree width after a drag past the left limit {minimum} px, expected the {MinTreePaneDip} DIP minimum ({expectedMinimum} px).");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_ViewSplitReturnsFocusAndEscapeRestoresWidth()
    {
        var checks = new Checks("shell.split");
        using var session = OpenBwc(checks, "shell-split");
        using var automation = new UIA3Automation();

        // The list has focus before View > Split. Split mode must give focus back to the list afterwards.
        bool onList = EnsurePane(session, automation, "list", "TAB", 6);
        checks.Record("startsOnList", onList, $"focused pane before View > Split={FocusedPane(automation)}.");

        int startWidth = PaneWidth(session);
        SplitFromView(session);
        bool splitterFocused = WaitUntil(() => FocusedPane(automation) == "splitter", UiTimeout);
        checks.Record(
            "splitterFocused",
            splitterFocused,
            $"focused pane after View > Split={FocusedPane(automation)} (Tab skips the splitter; View > Split requests its focus).");

        for (int i = 0; i < 3; i++)
        {
            _ = ControlTestSupport.EnsureForeground(session);
            Keyboard.Type(VirtualKeyShort.RIGHT);
            Thread.Sleep(KeySettle);
        }

        int widened = PaneWidth(session);
        checks.Record("arrowWidens", widened > startWidth, $"tree width {startWidth} px -> {widened} px after three Right presses.");

        // Escape leaves split mode, puts the width back from before the split, and returns focus to the list.
        _ = ControlTestSupport.EnsureForeground(session);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool escapeFocus = WaitUntil(() => FocusedPane(automation) == "list", UiTimeout);

        // The restored column width is applied on the next layout pass, which can come after focus has moved. Reading at
        // once returned the widened 480 px while the width was already restored: the next split began at 432 px.
        _ = WaitUntil(() => Math.Abs(PaneWidth(session) - startWidth) <= 1, UiTimeout);
        int afterEscape = PaneWidth(session);
        checks.Record("escapeFocus", escapeFocus, $"focused pane after Escape={FocusedPane(automation)}.");
        checks.Record(
            "escapeWidth",
            Math.Abs(afterEscape - startWidth) <= 1,
            $"tree width after Escape={afterEscape} px, before the split={startWidth} px.");

        // Enter keeps the new width and also returns focus to the list.
        SplitFromView(session);
        _ = WaitUntil(() => FocusedPane(automation) == "splitter", UiTimeout);
        _ = ControlTestSupport.EnsureForeground(session);
        Keyboard.Type(VirtualKeyShort.RIGHT);
        Thread.Sleep(KeySettle);
        int beforeEnter = PaneWidth(session);
        Keyboard.Type(VirtualKeyShort.ENTER);
        bool enterFocus = WaitUntil(() => FocusedPane(automation) == "list", UiTimeout);
        int afterEnter = PaneWidth(session);
        checks.Record("enterFocus", enterFocus, $"focused pane after Enter={FocusedPane(automation)}.");
        checks.Record(
            "enterKeepsWidth",
            Math.Abs(afterEnter - beforeEnter) <= 1 && Math.Abs(afterEnter - startWidth) > 1,
            $"tree width after Enter={afterEnter} px (before Enter {beforeEnter} px, before the split {startWidth} px).");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Shell")]
    public void Shell_WindowEdgeAndSplitterAreNotDark()
    {
        var checks = new Checks("shell.edge");
        using var session = OpenBwc(checks, "shell-edge");
        Thread.Sleep(400);

        if (!GetClientRect(session.MainHwnd, out var client))
        {
            checks.Record("client", false, "GetClientRect failed.");
            checks.AssertAllPassed();
            return;
        }

        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(session.MainHwnd, ref origin);
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        using Bitmap capture = CaptureScreen(new Rectangle(origin.X, origin.Y, width, height));

        // Right and bottom client edges, clear of the anti-aliased corner, and the splitter column mid height.
        Color right = capture.GetPixel(width - 3, height / 2);
        Color bottom = capture.GetPixel(width / 2, height - 3);
        var splitter = WaitForId(session, SplitterId);
        Color splitterColor = Color.Black;
        if (splitter is not null)
        {
            Rectangle sr = splitter.BoundingRectangle;
            int sx = sr.Left + (sr.Width / 2) - origin.X;
            int sy = sr.Top + (sr.Height / 2) - origin.Y;
            splitterColor = capture.GetPixel(Math.Clamp(sx, 0, width - 1), Math.Clamp(sy, 0, height - 1));
        }

        int threshold = 30;
        checks.Record("rightEdge", Sum(right) > threshold, $"Right client edge #{Hex(right)}; a black strip is #000000.");
        checks.Record("bottomEdge", Sum(bottom) > threshold, $"Bottom client edge #{Hex(bottom)}; a black strip is #000000.");
        checks.Record("splitterColumn", Sum(splitterColor) > threshold, $"Splitter column mid-height #{Hex(splitterColor)}.");
        Remember("edge", $"client {width}x{height} px; right #{Hex(right)}, bottom #{Hex(bottom)}, splitter #{Hex(splitterColor)}.");
        checks.AssertAllPassed();
    }

    // ---- sessions and focus -------------------------------------------------------------------------

    /// <summary>The tree pane width in pixels, or -1 when the pane is not found.</summary>
    private static int PaneWidth(AppSession session) => WaitForId(session, TreePaneId)?.BoundingRectangle.Width ?? -1;

    /// <summary>Runs View > Split (command 670) from the menu bar with the real menu path.</summary>
    private static void SplitFromView(AppSession session)
    {
        using var driver = new Bwc.E2E.Editing.EditingDriver(session);
        driver.OpenMenu(MenuViewCommand, MenuViewSplitCommand);
    }

    private static AppSession OpenBwc(Checks checks, string scope)
    {
        var config = GuiGate.Config;
        config.EnsureExecutablesExist();
        var session = new AppSession(config, config.RegeditExePath, SessionTimeout, config.DataRootEnvironment(scope));
        _ = ControlTestSupport.EnsureForeground(session);
        checks.Note("session", $"pid={session.ProcessId}, scope={scope}.");
        return session;
    }

    /// <summary>The pane that holds keyboard focus: tree, list, address, splitter, or other.</summary>
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
            switch (SafeAutomationId(current))
            {
                case AddressBarId:
                    return "address";
                case SplitterId:
                    return "splitter";
                case TreePaneId:
                    return "tree";
                case ListPaneId:
                    return "list";
                case ShellId:
                    return "shell";
            }
        }

        return "other";
    }

    /// <summary>Presses a key until the target pane has focus. Returns false when it is not reached.</summary>
    private static bool EnsurePane(AppSession session, UIA3Automation automation, string target, string key, int maxPresses)
    {
        // A click puts the keyboard in the shell. Keys sent before that can miss the shell's accelerators and handlers.
        if (FocusedPane(automation) != target)
        {
            ClickPane(session, TreePaneId);
        }

        for (var i = 0; i <= maxPresses; i++)
        {
            if (FocusedPane(automation) == target)
            {
                return true;
            }

            if (i < maxPresses)
            {
                session.SendKeys(key);
                Thread.Sleep(KeySettle);
            }
        }

        return FocusedPane(automation) == target;
    }

    /// <summary>Presses the key count times and returns the pane focused after each press.</summary>
    private static string[] PressSequence(AppSession session, UIA3Automation automation, string key, int count)
    {
        var seen = new string[count];
        for (var i = 0; i < count; i++)
        {
            session.SendKeys(key);
            Thread.Sleep(KeySettle);
            seen[i] = FocusedPane(automation);
        }

        return seen;
    }

    private static void ClickPane(AppSession session, string automationId)
    {
        var pane = session.Find(automationId);
        if (pane is null)
        {
            return;
        }

        try
        {
            pane.Click();
        }
        catch (Exception)
        {
            // The focus check that follows reports whether the click took effect.
        }

        Thread.Sleep(200);
    }

    /// <summary>
    /// Presses the left button on the splitter and moves it in steps, leaving the button down. The steps use
    /// mouse_event on the virtual desktop. FlaUI's MoveTo does not send move messages while the button is held,
    /// so the splitter thumb saw one move at the start point and never raised DragDelta.
    /// </summary>
    private static void DragSplitter(Point start, int dx)
    {
        MoveCursor(start);
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(150);
        const int steps = 12;
        for (var i = 1; i <= steps; i++)
        {
            MoveCursor(new Point(start.X + (dx * i / steps), start.Y));
            Thread.Sleep(40);
        }

        Thread.Sleep(200);
    }

    /// <summary>Moves the cursor to a screen point with an absolute mouse_event, which generates move messages.</summary>
    private static void MoveCursor(Point target)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        int height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);
        double x = Math.Clamp((target.X - left) * 65535.0 / width, 0, 65535);
        double y = Math.Clamp((target.Y - top) * 65535.0 / height, 0, 65535);
        mouse_event(MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk, (uint)Math.Round(x), (uint)Math.Round(y), 0, UIntPtr.Zero);
    }

    /// <summary>True when the global cursor is the system east-west resize cursor (IDC_SIZEWE).</summary>
    private static bool CursorIsSizeWestEast()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info))
        {
            return false;
        }

        return info.hCursor != IntPtr.Zero && info.hCursor == LoadCursor(IntPtr.Zero, IdcSizeWestEast);
    }

    private static void ToggleAddressBarFromMenu(AppSession session)
    {
        var view = TopLevelItems(session).FirstOrDefault(e => string.Equals(SafeName(e), "View", StringComparison.Ordinal));
        if (view is null)
        {
            return;
        }

        try
        {
            if (view.Patterns.ExpandCollapse.IsSupported)
            {
                view.Patterns.ExpandCollapse.Pattern.Expand();
            }
        }
        catch (Exception)
        {
            // The item lookup below reports a missing flyout.
        }

        _ = WaitUntil(() => view.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == ExpandCollapseState.Expanded, MenuTimeout);

        // A ToggleMenuFlyoutItem may not expose the Invoke pattern, so a click is the fallback, as in ShellTests.InvokeMenuItem.
        var item = WaitForMenuItem(session, "Address Bar");
        try
        {
            if (item is not null && item.Patterns.Invoke.IsSupported)
            {
                item.Patterns.Invoke.Pattern.Invoke();
            }
            else if (item is not null)
            {
                item.Click();
            }
        }
        catch (Exception)
        {
            // The caller checks the visibility that results.
        }

        Thread.Sleep(300);
    }

    private static double GapBelowMenuBar(AppSession session)
    {
        var bar = WaitForId(session, MenuBarId);
        var tree = WaitForId(session, TreePaneId);
        if (bar is null || tree is null)
        {
            return -1;
        }

        return tree.BoundingRectangle.Top - bar.BoundingRectangle.Bottom;
    }

    private static bool SoftwareRowSelected(AppSession session)
    {
        try
        {
            var row = session.Window.FindFirstDescendant(cf => cf.ByName("Software"));
            return row is not null && row.Patterns.SelectionItem.IsSupported && row.Patterns.SelectionItem.Pattern.IsSelected.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AddressBarHidden(AppSession session)
    {
        var element = session.Find(AddressBarId);
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

    private static string ValueOf(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

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

    private static string SafeAutomationId(AutomationElement element)
    {
        try
        {
            return element.AutomationId ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeName(AutomationElement? element)
    {
        try
        {
            return element?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeClassName(AutomationElement? element)
    {
        try
        {
            return element?.ClassName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static Point Center(Rectangle rect) => new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
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
                // The next poll retries.
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    // ---- pixels and captures ----------------------------------------------------------------------

    /// <summary>The screen pixels of an area, verified to be shown by the current session app
    /// (see <see cref="WindowCapture"/>).</summary>
    private static Bitmap CaptureScreen(Rectangle area) => WindowCapture.CaptureAreaOfCurrentSession(area);

    /// <summary>Saves the client area of the window as a PNG in artifacts/e2e/visual/shell.</summary>
    private static void CaptureWindow(AppSession session, string fileName)
    {
        var config = GuiGate.Config;
        string dir = Path.Combine(config.ArtifactsDir, "visual", "shell");
        Directory.CreateDirectory(dir);
        if (!GetClientRect(session.MainHwnd, out var client))
        {
            return;
        }

        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(session.MainHwnd, ref origin);
        // An artifact, not a check: a capture that is not judged is a note, and the test goes on.
        try
        {
            using Bitmap shot = WindowCapture.CaptureRegion(
                session.MainHwnd,
                new System.Drawing.Point(origin.X, origin.Y),
                client.Right - client.Left,
                client.Bottom - client.Top);
            shot.Save(Path.Combine(dir, fileName), ImageFormat.Png);
        }
        catch (WindowCapture.NotJudgedException ex)
        {
            E2EResults.Note("shell.capture." + fileName, $"artifact not written: {ex.Message}");
        }
    }

    private static int Sum(Color c) => c.R + c.G + c.B;

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- measurements -----------------------------------------------------------------------------

    private static void Remember(string key, string text)
    {
        lock (SummaryLock)
        {
            Summary.Add(new KeyValuePair<string, string>(key, text));
        }
    }

    private static void WriteSummary()
    {
        try
        {
            var config = GuiGate.Config;
            string dir = Path.Combine(config.ArtifactsDir, "visual", "shell");
            Directory.CreateDirectory(dir);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            lock (SummaryLock)
            {
                foreach (var pair in Summary)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            File.WriteAllText(
                Path.Combine(dir, SummaryFileName),
                JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            E2EResults.Note("shell.summary", "The measurement summary could not be written: " + ex.Message);
        }
    }

    // ---- native -----------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // Small set of user32 calls; LibraryImport plumbing is not needed.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const uint MouseEventfMove = 0x0001;
    private const uint MouseEventfAbsolute = 0x8000;
    private const uint MouseEventfVirtualDesk = 0x4000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private static readonly IntPtr IdcSizeWestEast = new(32644);
#pragma warning restore SYSLIB1054

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
                $"Shell keyboard checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, _failures)}");
        }
    }
}
