using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Bwc.Spike.Interop.E2E;

[TestClass]
public sealed class MenuKeyboardTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    [TestMethod]
    public void AltAlone_And_F10_Focus_File_Escape_Restores_Previous_Focus()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "menu.alt-alone"), "main window must be foreground before keys");

        var startFocus = GuiSession.WaitUntil(() => TestSupport.FocusedAutomationIdIs(s, "OpenModalButton"), Settle);
        var ok = S4Results.Record("menu.start-focus-open-modal", startFocus, "focus at startup: " + TestSupport.FocusedDescription(s));

        // Alt pressed and released with nothing in between.
        Keyboard.Press(VirtualKeyShort.ALT);
        Keyboard.Release(VirtualKeyShort.ALT);
        var altState = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Focused:File", Settle);
        ok &= S4Results.Record("menu.alt-alone.state", altState, $"MenuState='{s.TextOf("MenuState")}'");
        var altFocus = TestSupport.FocusedIs(s, ControlType.MenuItem, "File");
        ok &= S4Results.Record("menu.alt-alone.focus-file", altFocus, "focused: " + TestSupport.FocusedDescription(s));

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        var escState = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Idle", Settle);
        var escFocus = GuiSession.WaitUntil(() => TestSupport.FocusedAutomationIdIs(s, "OpenModalButton"), Settle);
        ok &= S4Results.Record("menu.escape.restores-focus", escState && escFocus,
            $"MenuState='{s.TextOf("MenuState")}' focused: {TestSupport.FocusedDescription(s)}");

        // F10 gives the same result.
        Keyboard.Type(VirtualKeyShort.F10);
        var f10State = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Focused:File", Settle);
        var f10Focus = TestSupport.FocusedIs(s, ControlType.MenuItem, "File");
        ok &= S4Results.Record("menu.f10.focus-file", f10State && f10Focus,
            $"MenuState='{s.TextOf("MenuState")}' focused: {TestSupport.FocusedDescription(s)}");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        var f10Restored = GuiSession.WaitUntil(() => TestSupport.FocusedAutomationIdIs(s, "OpenModalButton"), Settle);
        ok &= S4Results.Record("menu.f10.escape-restores-focus", f10Restored, "focused: " + TestSupport.FocusedDescription(s));

        Assert.IsTrue(ok, "one or more menu keyboard checks failed; see out/s4-result.json");
    }

    [TestMethod]
    public void Alt_Then_Other_Key_Does_Not_Activate_Menu_Bar()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "menu.alt-chord"), "main window must be foreground before keys");
        GuiSession.WaitUntil(() => TestSupport.FocusedAutomationIdIs(s, "OpenModalButton"), Settle);

        // Alt held while another key is pressed is a chord, not a bare Alt. Use Alt+V, then release Alt.
        Keyboard.Press(VirtualKeyShort.ALT);
        Keyboard.Type(VirtualKeyShort.KEY_V);
        Keyboard.Release(VirtualKeyShort.ALT);
        GuiSession.WaitUntil(() => s.TextOf("MenuState") != "Idle", TimeSpan.FromSeconds(1));
        var state = s.TextOf("MenuState");
        var ok = S4Results.Record("menu.alt-chord.no-bare-alt-activation", state != "Focused:Edit" && state != "Focused:File",
            $"MenuState after Alt+V is '{state}' (chord opens View; a bare Alt would have focused File)");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Idle", Settle);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Idle", Settle);
        Assert.IsTrue(ok, "Alt+V chord must not be treated as a bare Alt press");
    }

    [TestMethod]
    public void Alt_F_Opens_File_Flyout()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "menu.alt-f"), "main window must be foreground before keys");
        GuiSession.WaitUntil(() => TestSupport.FocusedAutomationIdIs(s, "OpenModalButton"), Settle);

        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        var open = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Open:File", Settle);
        var ok = S4Results.Record("menu.alt-f.state-open-file", open, $"MenuState='{s.TextOf("MenuState")}'");

        var item = GuiSession.WaitUntil(() => TestSupport.IsVisible(TestSupport.FindVisibleOnDesktop(s, "New", ControlType.MenuItem)), Settle);
        ok &= S4Results.Record("menu.alt-f.flyout-visible", item, "File flyout item 'New' visible on desktop: " + item);

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        var closed = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Focused:File", Settle);
        ok &= S4Results.Record("menu.alt-f.escape-closes-flyout", closed, $"MenuState='{s.TextOf("MenuState")}'");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        var idle = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Idle", Settle);
        ok &= S4Results.Record("menu.alt-f.second-escape-idle", idle, $"MenuState='{s.TextOf("MenuState")}'");

        Assert.IsTrue(ok, "one or more Alt+F checks failed; see out/s4-result.json");
    }

    [TestMethod]
    public void Right_Arrow_Moves_From_File_To_Edit()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "menu.right"), "main window must be foreground before keys");
        GuiSession.WaitUntil(() => TestSupport.FocusedAutomationIdIs(s, "OpenModalButton"), Settle);

        Keyboard.Press(VirtualKeyShort.ALT);
        Keyboard.Release(VirtualKeyShort.ALT);
        var file = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Focused:File", Settle);
        var ok = S4Results.Record("menu.right.start-file", file, $"MenuState='{s.TextOf("MenuState")}'");

        Keyboard.Type(VirtualKeyShort.RIGHT);
        var edit = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Focused:Edit", Settle);
        var editFocus = TestSupport.FocusedIs(s, ControlType.MenuItem, "Edit");
        ok &= S4Results.Record("menu.right.moves-to-edit", edit && editFocus,
            $"MenuState='{s.TextOf("MenuState")}' focused: {TestSupport.FocusedDescription(s)}");

        // Down, with the bar focused on Edit, opens Edit's flyout.
        Keyboard.Type(VirtualKeyShort.DOWN);
        var editOpen = GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Open:Edit", Settle);
        var undo = GuiSession.WaitUntil(() => TestSupport.IsVisible(TestSupport.FindVisibleOnDesktop(s, "Undo", ControlType.MenuItem)), Settle);
        ok &= S4Results.Record("menu.down.opens-edit-flyout", editOpen && undo,
            $"MenuState='{s.TextOf("MenuState")}' Edit flyout item 'Undo' visible={undo}");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Focused:Edit", Settle);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        GuiSession.WaitUntil(() => s.TextOf("MenuState") == "Idle", Settle);
        Assert.IsTrue(ok, "Right arrow / Down check failed; see out/s4-result.json");
    }
}
