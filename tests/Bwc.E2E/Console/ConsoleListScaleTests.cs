using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Bwc.E2E.Controls;
using Bwc.E2E.List;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// Console list tests for a large list (3,000 rows: open, scroll, memory, rows replaced while scrolled to the end) and
/// for the column state the caller persists. Failure modes 2, 13 and 17 of docs/failure-modes/console-list.md.
/// </summary>
[TestClass]
public sealed class ConsoleListScaleTests
{
    // Bounds of Console_List_Large3000. They are generous on purpose: an unvirtualized list takes minutes and
    // gigabytes, so these catch that, not tuning. The measured values are written to the measurements file.
    private const long OpenBoundMs = 15_000;
    private const long EndKeyBoundMs = 3_000;
    private const long UiaReadBoundMs = 2_000;
    private const int RealizedBound = 150;
    private const long MemoryGrowthBoundBytes = 250L * 1024 * 1024;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_Large3000()
    {
        var checks = new ShellChecks("large", "list");
        const int rows = 3000;
        var openWatch = Stopwatch.StartNew();
        using ListRun run = ListRun.Open("large", rows);
        bool firstRow = ShellRun.WaitUntil(() => run.ItemElement(0) is not null, TimeSpan.FromSeconds(20));
        long openMs = openWatch.ElapsedMilliseconds;
        checks.Record("open", firstRow && openMs <= OpenBoundMs, $"start to 3,000 rows with row 0 on screen: {openMs} ms (bound {OpenBoundMs} ms).");
        run.Measure("openMs", openMs);
        checks.Record("realizedAtStart", run.Int("Realized") is > 0 and <= RealizedBound, $"realized elements at start={run.Int("Realized")} (bound {RealizedBound}); the list is virtualized.");

        using Process process = Process.GetProcessById(run.Shell.Session.ProcessId);
        process.Refresh();
        long memoryBefore = process.WorkingSet64;
        run.FocusList();
        run.Shell.Tap(ListKeys.Home);

        // End: the last row is focused, selected, realized and on screen.
        var endWatch = Stopwatch.StartNew();
        run.Shell.Tap(ListKeys.End);
        bool atEnd = ShellRun.WaitUntil(() => run.Focus == rows - 1 && run.ItemElement(rows - 1) is not null, TimeSpan.FromSeconds(10));
        long endMs = endWatch.ElapsedMilliseconds;
        checks.Record("endKey", atEnd && endMs <= EndKeyBoundMs + ShellRun.KeySettle.TotalMilliseconds, $"End: focus={run.Focus}, last row realized={run.ItemElement(rows - 1) is not null}, {endMs} ms (bound {EndKeyBoundMs} ms plus the settle time).");
        run.Measure("endMs", endMs);
        Rectangle lastRect = run.ItemElement(rows - 1)?.BoundingRectangle ?? Rectangle.Empty;
        Rectangle? listRect = run.ListRect();
        checks.Record(
            "lastRowVisible",
            listRect is not null && lastRect.Bottom <= listRect.Value.Bottom + 2 && lastRect.Top >= listRect.Value.Top,
            $"last row rectangle={lastRect}, list={listRect}.");
        checks.Record("lastRowName", run.Items().Any(item => item.Index == rows - 1 && item.Name == run.ExpectedName(rows - 1)), $"the last row is named '{run.ExpectedName(rows - 1)}'.");

        // PageUp from the end, then PageDown back: 20 keys each way, the focus moves by whole pages.
        run.Shell.Tap(ListKeys.PageUp);
        int onePage = rows - 1 - run.Focus;
        checks.Record("pageUp", onePage > 5, $"PageUp from the last row moved {onePage} rows.");
        for (int i = 0; i < 19; i++)
        {
            run.Shell.Tap(ListKeys.PageUp);
        }

        checks.Record("pageUpTwenty", run.Focus == rows - 1 - (20 * onePage), $"after 20 PageUp: focus={run.Focus}, expected {rows - 1 - (20 * onePage)}.");
        for (int i = 0; i < 20; i++)
        {
            run.Shell.Tap(ListKeys.PageDown);
        }

        checks.Record("pageDownTwenty", run.Focus == rows - 1, $"after 20 PageDown: focus={run.Focus}, expected {rows - 1}.");

        // One UIA read of the visible rows after scrolling.
        List<ListItemView> items = new();
        long uiaMs = ListRun.TimeMs(() => items = run.Items());
        checks.Record("uiaRead", items.Count > 5 && uiaMs <= UiaReadBoundMs, $"UI Automation read of {items.Count} realized rows: {uiaMs} ms (bound {UiaReadBoundMs} ms).");
        run.Measure("uiaReadMs", uiaMs);
        checks.Record("realizedAtEnd", run.Int("Realized") is > 0 and <= RealizedBound, $"realized elements at the end={run.Int("Realized")} (bound {RealizedBound}).");

        run.Shell.Tap(ListKeys.Home);
        checks.Record("home", ShellRun.WaitUntil(() => run.Focus == 0 && run.ItemElement(0) is not null), $"Home: focus={run.Focus}.");
        run.Shell.Tap(ListKeys.End);
        _ = ShellRun.WaitUntil(() => run.Focus == rows - 1);

        process.Refresh();
        long growth = process.WorkingSet64 - memoryBefore;
        checks.Record("memory", growth <= MemoryGrowthBoundBytes, $"working set growth over End, 40 pages and Home: {growth / (1024 * 1024)} MB (bound {MemoryGrowthBoundBytes / (1024 * 1024)} MB).");
        run.Measure("workingSetBeforeMb", memoryBefore / (1024 * 1024));
        run.Measure("workingSetGrowthMb", growth / (1024 * 1024));
        run.WriteArtifacts("console-list-large-end.png");

        // Rows replaced while the list is scrolled to its end: 100 rows, then none, then the 3,000 again.
        AutomationElement? refresh = run.Shell.Find(ShellRun.ButtonId("Refresh"));
        refresh?.Patterns.Invoke.PatternOrDefault?.Invoke();
        bool hundred = ShellRun.WaitUntil(() => run.Int("Rows") == 100, TimeSpan.FromSeconds(10));
        checks.Record("replaceBy100", hundred, $"rows after the first Refresh={run.Int("Rows")}; expected 100 rows of the new set.");
        List<ListItemView> afterReplace = run.Items();
        checks.Record(
            "replacedShowsNewRows",
            afterReplace.Count > 5 && afterReplace.All(i => i.Name == ListRun.RowName(i.Index, 100, 1)) && afterReplace[^1].Index == 99 && afterReplace[0].Index > 0,
            $"realized rows after the replacement start [{string.Join(", ", afterReplace.Take(3).Select(i => i.Index + ":" + i.Name))}] and end at index {(afterReplace.Count > 0 ? afterReplace[^1].Index : -1)}; every row must carry the new name and the old offset is clamped so the view shows the end (last index 99).");
        checks.Record("replacedClearsSelection", run.SelectedCount == 0 && run.Focus == -1, $"selected={run.SelectedCount}, focus={run.Focus}; the tag 2999 does not exist in the new rows.");

        refresh?.Patterns.Invoke.PatternOrDefault?.Invoke();
        bool none = ShellRun.WaitUntil(() => run.Int("Rows") == 0, TimeSpan.FromSeconds(10));
        checks.Record("replaceByNone", none && run.Items().Count == 0, $"rows after the second Refresh={run.Int("Rows")}, realized={run.Items().Count}.");
        run.FocusList();
        string statusBefore = run.Shell.StatusText();
        foreach (ushort key in new ushort[] { ListKeys.Down, ListKeys.Home, ListKeys.End, ListKeys.PageDown, ListKeys.Return })
        {
            run.Shell.Tap(key);
        }

        checks.Record("emptyKeysRaiseNothing", run.Shell.StatusText() == statusBefore && run.Focus == -1, $"status before='{statusBefore}', after='{run.Shell.StatusText()}', focus={run.Focus}.");
        run.Shell.Chord(RawKeyboard.Shift, ShellRun.VkF10);
        checks.Record("emptyContext", ShellRun.WaitUntil(() => run.Shell.StatusText().StartsWith("Context menu: pane=Result source=key", StringComparison.Ordinal)), $"status after the context key in the empty list='{run.Shell.StatusText()}'.");

        refresh?.Patterns.Invoke.PatternOrDefault?.Invoke();
        bool back = ShellRun.WaitUntil(() => run.Int("Rows") == rows && run.ItemElement(0) is not null, TimeSpan.FromSeconds(15));
        checks.Record("replaceBack", back, $"rows after the third Refresh={run.Int("Rows")}; the list answers UI Automation.");
        run.WriteArtifacts("console-list-large.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_ColumnState()
    {
        var checks = new ShellChecks("state", "list");
        using ListRun run = ListRun.Open("state", 12, new Dictionary<string, string> { ["BWC_E2E_CONSOLE_STATE_MENU"] = "1" });

        void Menu(char key)
        {
            _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
            run.Shell.Chord(RawKeyboard.Alt, ShellRun.VkV);
            _ = ShellRun.WaitUntil(() => run.Shell.Find("MenuCommand_1200") is not null, TimeSpan.FromSeconds(5));
            run.Shell.Tap((ushort)char.ToUpperInvariant(key));
        }

        // Widen the first column and sort State descending.
        Rectangle divider = run.Rect(ListRun.DividerId("name")) ?? Rectangle.Empty;
        ShellRun.DragStart(new Point(divider.Left + (divider.Width / 2), divider.Top + (divider.Height / 2)), (int)Math.Round(40 * run.Scale));
        ShellRun.DragEnd();
        run.Shell.Find(ListRun.HeaderItemId("state"))?.Click();
        _ = ShellRun.WaitUntil(() => run.Str("SortDir") == "Ascending");
        run.Shell.Find(ListRun.HeaderItemId("state"))?.Click();
        _ = ShellRun.WaitUntil(() => run.Str("SortDir") == "Descending");
        double nameWidth = run.Doubles("Widths")[0];

        Menu('v');
        string expectedSaved = $"State: name={nameWidth.ToString("0.##", CultureInfo.InvariantCulture)},state=100,comment=200;sort=state:Descending;mode=Detail";
        checks.Record("save", ShellRun.WaitUntil(() => run.Shell.StatusText() == expectedSaved), $"state saved by the host='{run.Shell.StatusText()}'; expected '{expectedSaved}'.");

        // The host restores a fixed state: comment first (150), name (222), state hidden, sorted by name, Detail.
        Menu('r');
        bool applied = ShellRun.WaitUntil(() => run.Str("Order") == "comment,name" && run.Str("Hidden") == "state", TimeSpan.FromSeconds(8));
        checks.Record("applied", applied, $"order='{run.Str("Order")}', hidden='{run.Str("Hidden")}', widths='{run.Str("Widths")}'.");
        checks.Record("appliedWidths", run.Doubles("Widths").SequenceEqual([150, 222]), $"widths after the restore=[{string.Join(", ", run.Doubles("Widths"))}]; expected 150, 222.");
        checks.Record("appliedSort", run.Str("Sort") == "name" && run.Str("SortDir") == "Ascending", $"sort after the restore={run.Str("Sort")} {run.Str("SortDir")}.");
        checks.Record("hiddenHeaderGone", run.Shell.Find(ListRun.HeaderItemId("state")) is null, "the header of the hidden column is not in the tree.");
        Rectangle? comment = run.Rect(ListRun.HeaderItemId("comment"));
        Rectangle? name = run.Rect(ListRun.HeaderItemId("name"));
        checks.Record("appliedOrder", comment is not null && name is not null && comment.Value.Left < name.Value.Left, $"comment header at {comment?.Left}, name header at {name?.Left}.");
        string[] shown = run.Items().Select(item => item.Name).ToArray();
        checks.Record("appliedSortOrder", shown.SequenceEqual(Enumerable.Range(0, 12).Select(run.ExpectedName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), $"rows after the restore=[{string.Join(", ", shown)}].");
        checks.Record("appliedArrow", run.ArrowInk("name") > 8, $"arrow ink on the name header={run.ArrowInk("name")}.");

        Menu('v');
        const string expectedAgain = "State: comment=150,name=222,state=100!;sort=name:Ascending;mode=Detail";
        checks.Record("saveAgain", ShellRun.WaitUntil(() => run.Shell.StatusText() == expectedAgain), $"state saved again='{run.Shell.StatusText()}'; expected '{expectedAgain}'.");

        run.WriteArtifacts("console-list-state.png");
        checks.AssertAllPassed();
    }
}
