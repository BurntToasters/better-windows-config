using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// Console list tests for the events (activate and context menu, through the host's status bar text) and for the
/// type-ahead search. Failure modes 12 and 15 of docs/failure-modes/console-list.md.
/// </summary>
[TestClass]
public sealed class ConsoleListInputTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_ActivateAndContext()
    {
        var checks = new ShellChecks("events", "list");
        using ListRun run = ListRun.Open("events", 12);
        string Status() => run.Shell.StatusText();
        bool IsStatus(string expected) => ShellRun.WaitUntil(() => Status() == expected, TimeSpan.FromSeconds(4));

        // The context key with nothing focused is one background request.
        run.FocusList();
        run.Shell.Chord(RawKeyboard.Shift, ShellRun.VkF10);
        checks.Record("shiftF10Background", IsStatus("Context menu: pane=Result source=key n=1"), $"status after Shift+F10 with no focused row='{Status()}'.");

        run.ItemElement(3)?.Click();
        _ = ShellRun.WaitUntil(() => run.Focus == 3);
        run.Shell.Tap(ListKeys.Return);
        checks.Record("enter", IsStatus($"Activated: {run.ExpectedName(3)} n=1"), $"status after Enter on row 3='{Status()}'.");

        run.ItemElement(5)?.DoubleClick();
        checks.Record("doubleClick", IsStatus($"Activated: {run.ExpectedName(5)} n=2"), $"status after a double click on row 5='{Status()}'.");
        checks.Record("doubleClickSelects", run.SelectedCount == 1 && run.Focus == 5, $"selected={run.SelectedCount}, focus={run.Focus} after the double click.");

        run.Shell.Chord(RawKeyboard.Shift, ShellRun.VkF10);
        checks.Record("shiftF10Row", IsStatus($"Context menu: pane=Result source=key n=2 row={run.ExpectedName(5)}"), $"status after Shift+F10 on row 5='{Status()}'.");
        run.Shell.Tap(ShellRun.VkApps);
        checks.Record("appsKeyRow", IsStatus($"Context menu: pane=Result source=key n=3 row={run.ExpectedName(5)}"), $"status after the context-menu key='{Status()}'.");

        // A right click on an unselected row selects it and reports it.
        run.ItemElement(7)?.RightClick();
        checks.Record("rightClickRow", IsStatus($"Context menu: pane=Result source=pointer n=4 row={run.ExpectedName(7)}"), $"status after a right click on row 7='{Status()}'.");
        checks.Record("rightClickSelects", run.Ints("SelectedIdx").SequenceEqual([7]), $"selected=[{string.Join(",", run.Ints("SelectedIdx"))}] after the right click on an unselected row.");

        // A right click on a row of a multi-selection keeps the selection.
        run.Shell.Chord(RawKeyboard.Shift, ListKeys.Down);
        run.Shell.Chord(RawKeyboard.Shift, ListKeys.Down);
        checks.Record("rangeBuilt", run.Ints("SelectedIdx").SequenceEqual([7, 8, 9]), $"selected=[{string.Join(",", run.Ints("SelectedIdx"))}] before the right click.");
        run.ItemElement(8)?.RightClick();
        checks.Record("rightClickKeeps", IsStatus($"Context menu: pane=Result source=pointer n=5 row={run.ExpectedName(8)}"), $"status after a right click on selected row 8='{Status()}'.");
        checks.Record("rightClickKeepsSelection", run.Ints("SelectedIdx").SequenceEqual([7, 8, 9]), $"selected=[{string.Join(",", run.Ints("SelectedIdx"))}]; the multi-selection is kept.");

        // Enter with several rows selected activates the focused one.
        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        run.FocusList();
        run.Shell.Tap(ListKeys.Return);
        string expectedFocused = $"Activated: {run.ExpectedName(run.Focus)} n=3";
        checks.Record("enterMulti", IsStatus(expectedFocused), $"status after Enter with 3 selected rows and focus {run.Focus}='{Status()}'.");

        // The empty area below the rows is a background request; the header is a header request.
        Rectangle? list = run.ListRect();
        if (list is not null)
        {
            var empty = new Point(list.Value.Left + (list.Value.Width / 2), list.Value.Bottom - (int)Math.Round(40 * run.Scale));
            FlaUI.Core.Input.Mouse.RightClick(empty);
            checks.Record("rightClickBackground", IsStatus("Context menu: pane=Result source=pointer n=6"), $"status after a right click on the empty area='{Status()}'.");
        }

        run.Shell.Find(ListRun.HeaderItemId("state"))?.RightClick();
        checks.Record("rightClickHeader", IsStatus("Context menu: pane=Result source=pointer n=7 header"), $"status after a right click on a header='{Status()}'.");

        run.WriteArtifacts("console-list-events.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_TypeAhead()
    {
        var checks = new ShellChecks("typeahead", "list");
        // Names: 0 Alpha, 1 Beta, 2 Bravo, 3 Charlie, 4 Delta, 5 Écho, 6 Foxtrot, 7 Zulu, 8 Ünder, 9 日本語.
        using ListRun run = ListRun.Open("typeahead", 10, new Dictionary<string, string> { ["BWC_E2E_CONSOLE_NAMES"] = "typeahead" });
        run.FocusList();
        string[] names = run.Items().Select(item => item.Name).ToArray();
        checks.Record("names", names.Length == 10 && names[5] == "Écho" && names[9] == "日本語", $"rows=[{string.Join(", ", names)}].");

        void Expect(string id, int focus, string what)
        {
            bool ok = ShellRun.WaitUntil(() => run.Focus == focus, TimeSpan.FromSeconds(3));
            checks.Record(id, ok && run.SelectedCount == 1, $"{what}: focus={run.Focus}, selected={run.SelectedCount}; expected row {focus}.");
        }

        run.Type("b");
        Expect("firstLetter", 1, "'b' selects the first row that starts with b");
        run.Type("r");
        Expect("prefix", 2, "'b' then 'r' within a second selects 'Bravo'");

        ListRun.Pause(1300);
        run.Type("b");
        Expect("repeatWraps", 1, "'b' after a pause, from 'Bravo', goes to the next row that starts with b (wrapping to 'Beta')");
        run.Type("b");
        Expect("repeat", 2, "a second 'b' within a second cycles on to 'Bravo'");

        ListRun.Pause(1300);
        run.Type("z");
        Expect("later", 7, "'z' selects 'Zulu'");
        ListRun.Pause(1300);
        run.Type("É");
        Expect("accentUpper", 5, "'É' selects 'Écho'");
        ListRun.Pause(1300);
        run.Type("ü");
        Expect("accentLower", 8, "'ü' (lower case) selects 'Ünder'");
        ListRun.Pause(1300);
        run.Type("日");
        Expect("cjk", 9, "'日' selects '日本語'");
        ListRun.Pause(1300);
        run.Type("q");
        Expect("noMatch", 9, "'q' matches nothing and leaves the selection");

        // Ctrl combinations are not search input.
        ListRun.Pause(1300);
        run.Shell.Chord(RawKeyboard.Control, 0x41);
        checks.Record("ctrlNotSearch", run.SelectedCount == 10, $"Ctrl+A selected {run.SelectedCount} rows; it is select all, not a search for 'a'.");

        run.WriteArtifacts("console-list-typeahead.png");
        checks.AssertAllPassed();
    }
}
