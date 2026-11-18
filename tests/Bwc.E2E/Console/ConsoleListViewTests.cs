using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// Console list tests for the four view modes (layout and keyboard), selection kept across a view switch and a sort,
/// and multi-select. Failure modes 4, 5, 11 and 15 of docs/failure-modes/console-list.md.
/// </summary>
[TestClass]
public sealed class ConsoleListViewTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_ViewModes()
    {
        var checks = new ShellChecks("views", "list");
        using ListRun run = ListRun.Open("views", 60);
        const int rows = 60;
        run.FocusList();

        (string Mode, char Key)[] sequence = [("Detail", 'd'), ("LargeIcons", 'g'), ("SmallIcons", 'm'), ("List", 'l'), ("Detail", 'd')];
        for (int step = 0; step < sequence.Length; step++)
        {
            (string mode, char key) = sequence[step];
            string id = $"{mode}{step}";
            if (step > 0)
            {
                bool switched = run.SwitchView(key, mode);
                checks.Record(id + "Switch", switched, $"View menu access key '{key}' gives mode '{run.Mode}'; expected '{mode}'.");
                checks.Record(id + "FocusPane", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result"), $"focused pane after the switch={run.Shell.FocusedPane()}.");
            }

            List<ListItemView> items = run.Items();
            double[] viewport = run.Doubles("Viewport");
            double itemW = run.Doubles("ItemSize").ElementAtOrDefault(0);
            double itemH = run.Doubles("ItemSize").ElementAtOrDefault(1);
            int perLine = run.Int("PerLine");
            ListItemView? first = items.FirstOrDefault();
            checks.Record(id + "Realized", items.Count >= 3 && first is not null && first.Index == 0, $"realized items={items.Count}, first index={first?.Index}.");
            if (first is null || items.Count < 3 || viewport.Length < 2)
            {
                run.WriteArtifacts($"console-list-views-{step}.png");
                continue;
            }

            bool header = run.Rect(ListRun.HeaderId) is not null;
            checks.Record(id + "Header", header == (mode == "Detail"), $"header showing={header}; only Detail shows the column header.");
            double firstWidth = run.Logical(first.Rect.Width);
            double firstHeight = run.Logical(first.Rect.Height);

            switch (mode)
            {
                case "Detail":
                {
                    double step1 = run.Logical(items[1].Rect.Top - items[0].Rect.Top);
                    checks.Record(id + "Layout", Near(itemH, 19, 0.1) && Near(step1, 19, 1) && Near(firstHeight, 19, 1.5), $"row height={firstHeight:0.##}, step={step1:0.##}, ItemSize={itemW:0.##}x{itemH:0.##}; expected 19.");
                    checks.Record(id + "Wide", firstWidth >= 640, $"row width={firstWidth:0.##} DIP; at least the columns (650).");
                    break;
                }

                case "LargeIcons":
                case "SmallIcons":
                {
                    double cellW = mode == "LargeIcons" ? 75 : itemW;
                    double cellH = mode == "LargeIcons" ? 75 : 19;
                    int measured = items.Count(item => Math.Abs(item.Rect.Top - first.Rect.Top) <= 1);
                    ListItemView? below = items.FirstOrDefault(item => item.Index == measured);
                    checks.Record(id + "Size", Near(firstWidth, cellW, 1.5) && Near(firstHeight, cellH, 1.5), $"item {firstWidth:0.##}x{firstHeight:0.##} DIP; expected {cellW:0.##}x{cellH:0.##}.");
                    checks.Record(id + "PerLine", measured == perLine && perLine >= 2, $"items on the first line={measured}, readout PerLine={perLine}; the arithmetic must match the layout.");
                    checks.Record(
                        id + "Wraps",
                        below is not null && Near(run.Logical(below.Rect.Top - first.Rect.Top), cellH, 1.5) && Math.Abs(below.Rect.Left - first.Rect.Left) <= 1,
                        $"item {measured} is at ({below?.Rect.Left},{below?.Rect.Top}); the first is at ({first.Rect.Left},{first.Rect.Top}); the next line starts under it.");
                    break;
                }

                case "List":
                {
                    int measured = items.Count(item => Math.Abs(item.Rect.Left - first.Rect.Left) <= 1);
                    ListItemView? right = items.FirstOrDefault(item => item.Index == measured);
                    checks.Record(id + "Size", Near(firstHeight, 19, 1.5) && Near(firstWidth, itemW, 1.5), $"item {firstWidth:0.##}x{firstHeight:0.##} DIP; expected {itemW:0.##}x19.");
                    checks.Record(id + "PerColumn", measured == perLine && perLine >= 2, $"items in the first column={measured}, readout PerLine={perLine}.");
                    checks.Record(
                        id + "Flows",
                        right is not null && Near(run.Logical(right.Rect.Left - first.Rect.Left), itemW, 1.5) && Math.Abs(right.Rect.Top - first.Rect.Top) <= 1,
                        $"item {measured} is at ({right?.Rect.Left},{right?.Rect.Top}); the next column starts at the top.");
                    break;
                }
            }

            // Keys of the mode. Home focuses the first item.
            run.FocusList();
            run.Shell.Tap(ListKeys.Home);
            Expect(checks, run, id + "Home", 0);
            int page = mode switch
            {
                "Detail" => Math.Max(1, (int)(viewport[1] / 19)),
                "List" => Math.Max(1, (int)(viewport[0] / Math.Max(1, itemW))) * Math.Max(1, perLine),
                _ => Math.Max(1, (int)(viewport[1] / (mode == "LargeIcons" ? 75 : 19))) * Math.Max(1, perLine),
            };
            if (mode == "Detail")
            {
                run.Shell.Tap(ListKeys.Down);
                Expect(checks, run, id + "Down", 1);
                run.Shell.Tap(ListKeys.Right);
                Expect(checks, run, id + "RightDoesNotMove", 1);
                run.Shell.Tap(ListKeys.PageDown);
                Expect(checks, run, id + "PageDown", Math.Min(rows - 1, 1 + page));
                run.Shell.Tap(ListKeys.PageUp);
                Expect(checks, run, id + "PageUp", 1);
            }
            else if (mode == "List")
            {
                run.Shell.Tap(ListKeys.Down);
                Expect(checks, run, id + "Down", 1);
                run.Shell.Tap(ListKeys.Right);
                Expect(checks, run, id + "Right", 1 + perLine);
                run.Shell.Tap(ListKeys.Left);
                Expect(checks, run, id + "Left", 1);
                run.Shell.Tap(ListKeys.Up);
                Expect(checks, run, id + "Up", 0);
            }
            else
            {
                run.Shell.Tap(ListKeys.Right);
                Expect(checks, run, id + "Right", 1);
                run.Shell.Tap(ListKeys.Down);
                Expect(checks, run, id + "Down", 1 + perLine);
                run.Shell.Tap(ListKeys.Left);
                Expect(checks, run, id + "Left", perLine);
                run.Shell.Tap(ListKeys.Up);
                Expect(checks, run, id + "Up", 0);
                run.Shell.Tap(ListKeys.PageDown);
                Expect(checks, run, id + "PageDown", Math.Min(rows - 1, page));
                run.Shell.Tap(ListKeys.PageUp);
                Expect(checks, run, id + "PageUp", 0);
            }

            run.Shell.Tap(ListKeys.End);
            Expect(checks, run, id + "End", rows - 1);
            checks.Record(id + "EndSelected", run.SelectedCount == 1, $"selected rows after End={run.SelectedCount}; a plain move selects only the target.");
            run.Shell.Tap(ListKeys.Home);
            Expect(checks, run, id + "HomeAgain", 0);
            run.Measure(id, new Dictionary<string, object?>
            {
                ["itemSize"] = $"{itemW:0.##}x{itemH:0.##}",
                ["perLine"] = perLine,
                ["viewport"] = $"{viewport[0]:0.##}x{viewport[1]:0.##}",
                ["firstItem"] = first.Rect.ToString(),
            });
            run.WriteArtifacts($"console-list-views-{step}-{mode}.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_ViewModesAndSelection()
    {
        var checks = new ShellChecks("selection", "list");
        using ListRun run = ListRun.Open("selection", 300);
        run.FocusList();
        run.Shell.Tap(ListKeys.Home);
        for (int i = 0; i < 4; i++)
        {
            run.Shell.Tap(ListKeys.Down);
        }

        Expect(checks, run, "downFour", 4);
        run.Shell.Tap(ListKeys.End);
        Expect(checks, run, "end", 299);
        checks.Record("selectedTag", run.Str("SelectedTags") == "299" && run.Str("FocusTag") == "299", $"selected tags='{run.Str("SelectedTags")}', focus tag='{run.Str("FocusTag")}'.");

        // A sort while a row is selected keeps the row, selected and focused, wherever it lands.
        AutomationElement? stateHeader = run.Shell.Find(ListRun.HeaderItemId("state"));
        stateHeader?.Click();
        _ = ShellRun.WaitUntil(() => run.Str("Sort") == "state", TimeSpan.FromSeconds(8));
        Thread.Sleep(400);
        checks.Record(
            "sortKeepsSelection",
            run.SelectedCount == 1 && run.Str("SelectedTags") == "299" && run.Str("FocusTag") == "299",
            $"after sorting State: selected={run.SelectedCount}, selected tags='{run.Str("SelectedTags")}', focus tag='{run.Str("FocusTag")}'.");
        // Row 299 has state index 2 (Disabled), the first group: its position among 100 rows of that group is 99.
        checks.Record("sortFocusIndex", run.Focus == 99, $"focus index after sorting State ascending={run.Focus}; expected 99.");
        checks.Record("sortRevealed", IsVisible(run, run.Focus), $"the focused row (index {run.Focus}) is inside the list after the sort.");
        run.Measure("afterSort", run.RawReadout());

        // The focused row is off screen (the list is scrolled to its start); every view switch brings it back.
        (string Mode, char Key)[] modes = [("LargeIcons", 'g'), ("SmallIcons", 'm'), ("List", 'l'), ("Detail", 'd')];
        foreach ((string mode, char key) in modes)
        {
            ScrollToStart(run);
            int before = run.Focus;
            string tagBefore = run.Str("FocusTag");
            bool switched = run.SwitchView(key, mode);
            checks.Record(mode + "Switch", switched, $"mode after the View menu key '{key}'={run.Mode}.");
            checks.Record(
                mode + "KeptSelection",
                run.Focus == before && run.SelectedCount == 1 && run.Str("SelectedTags") == tagBefore && run.Str("FocusTag") == tagBefore,
                $"{mode}: focus={run.Focus} (was {before}), selected={run.SelectedCount}, tags='{run.Str("SelectedTags")}', focus tag='{run.Str("FocusTag")}'; expected tag '{tagBefore}'.");
            checks.Record(mode + "Revealed", ShellRun.WaitUntil(() => IsVisible(run, run.Focus), TimeSpan.FromSeconds(5)), $"{mode}: the focused row (index {run.Focus}) is inside the list after the switch.");
            checks.Record(mode + "FocusPane", run.Shell.FocusedPane() == "result", $"{mode}: focused pane={run.Shell.FocusedPane()}.");
            ushort previous = mode is "LargeIcons" or "SmallIcons" or "List" ? ListKeys.Left : ListKeys.Up;
            run.Shell.Tap(previous);
            checks.Record(mode + "ArrowWorks", run.Focus >= 0 && run.Focus < before, $"{mode}: focus after one back key={run.Focus}; it must be before {before}, so the keyboard still drives the list.");
            run.Shell.Tap(ListKeys.End);
            Expect(checks, run, mode + "EndAgain", 299);
            run.Shell.Tap(ListKeys.Home);
            run.Shell.Tap(ListKeys.End);
            run.WriteArtifacts($"console-list-selection-{mode}.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_MultiSelect()
    {
        var checks = new ShellChecks("multi", "list");
        using ListRun run = ListRun.Open("multi", 30);

        int[] Selected() => run.Ints("SelectedIdx");
        bool Is(params int[] expected) => Selected().SequenceEqual(expected);

        run.ItemElement(2)?.Click();
        _ = ShellRun.WaitUntil(() => Is(2));
        checks.Record("click", Is(2) && run.Focus == 2, $"selected=[{string.Join(",", Selected())}], focus={run.Focus} after clicking row 2.");

        run.Shell.Chord(RawKeyboard.Shift, ListKeys.Down);
        run.Shell.Chord(RawKeyboard.Shift, ListKeys.Down);
        checks.Record("shiftDown", Is(2, 3, 4) && run.Focus == 4, $"Shift+Down twice: selected=[{string.Join(",", Selected())}], focus={run.Focus}.");

        run.Shell.Chord(RawKeyboard.Control, ListKeys.Down);
        run.Shell.Chord(RawKeyboard.Control, ListKeys.Down);
        checks.Record("ctrlDownMovesFocusOnly", Is(2, 3, 4) && run.Focus == 6, $"Ctrl+Down twice: selected=[{string.Join(",", Selected())}], focus={run.Focus}.");
        run.Shell.Chord(RawKeyboard.Control, ListKeys.Space);
        checks.Record("ctrlSpace", Is(2, 3, 4, 6), $"Ctrl+Space: selected=[{string.Join(",", Selected())}].");

        AutomationElement? row10 = run.ItemElement(10);
        RawKeyboard.WithHeld(RawKeyboard.Control, () => row10?.Click());
        Thread.Sleep(ShellRun.KeySettle);
        checks.Record("ctrlClick", Is(2, 3, 4, 6, 10), $"Ctrl+click row 10: selected=[{string.Join(",", Selected())}].");

        AutomationElement? row14 = run.ItemElement(14);
        RawKeyboard.WithHeld(RawKeyboard.Shift, () => row14?.Click());
        Thread.Sleep(ShellRun.KeySettle);
        checks.Record("shiftClick", Is(10, 11, 12, 13, 14), $"Shift+click row 14 from the anchor 10: selected=[{string.Join(",", Selected())}].");

        run.Shell.Chord(RawKeyboard.Control, ListKeys.A);
        checks.Record("ctrlA", run.SelectedCount == 30, $"Ctrl+A: selected={run.SelectedCount}.");

        run.ItemElement(5)?.Click();
        run.Shell.Chord(RawKeyboard.Shift, ListKeys.End);
        checks.Record("shiftEnd", run.SelectedCount == 25 && Selected().FirstOrDefault() == 5 && Selected().LastOrDefault() == 29, $"Shift+End from row 5: selected={run.SelectedCount}, [{Selected().FirstOrDefault()}..{Selected().LastOrDefault()}].");
        run.Shell.Chord(RawKeyboard.Shift, ListKeys.Home);
        checks.Record("shiftHome", run.SelectedCount == 6 && Selected().FirstOrDefault() == 0 && Selected().LastOrDefault() == 5, $"Shift+Home with the anchor 5: selected={run.SelectedCount}, [{Selected().FirstOrDefault()}..{Selected().LastOrDefault()}].");

        run.Shell.Tap(ListKeys.Down);
        checks.Record("plainMoveCollapses", Is(1) || run.SelectedCount == 1, $"a plain Down after the extended selection: selected=[{string.Join(",", Selected())}].");

        // The selection pattern of the list reports the realized selected rows.
        var selection = run.Element?.Patterns.Selection.PatternOrDefault;
        checks.Record("selectionPattern", selection is not null && selection.CanSelectMultiple.ValueOrDefault, $"the list supports Selection with multiple selection={selection?.CanSelectMultiple.ValueOrDefault}.");

        run.WriteArtifacts("console-list-multi.png");
        checks.AssertAllPassed();
    }

    private static void Expect(ShellChecks checks, ListRun run, string id, int focus)
    {
        bool ok = ShellRun.WaitUntil(() => run.Focus == focus, TimeSpan.FromSeconds(4));
        checks.Record(id, ok, $"focus index={run.Focus}; expected {focus} (mode {run.Mode}, per line {run.Int("PerLine")}).");
    }

    /// <summary>True when the item with this index is realized and its rectangle lies inside the list.</summary>
    private static bool IsVisible(ListRun run, int index)
    {
        Rectangle? list = run.ListRect();
        AutomationElement? item = run.ItemElement(index);
        if (list is null || item is null)
        {
            return false;
        }

        Rectangle rect = item.BoundingRectangle;
        return rect.Left >= list.Value.Left - 2 && rect.Top >= list.Value.Top - 2 && rect.Right <= list.Value.Right + 2 && rect.Bottom <= list.Value.Bottom + 2;
    }

    private static void ScrollToStart(ListRun run)
    {
        var scroll = run.Element?.Patterns.Scroll.PatternOrDefault;
        if (scroll is null)
        {
            return;
        }

        try
        {
            double horizontal = scroll.HorizontallyScrollable.ValueOrDefault ? 0 : -1;
            double vertical = scroll.VerticallyScrollable.ValueOrDefault ? 0 : -1;
            scroll.SetScrollPercent(horizontal, vertical);
        }
        catch (Exception ex)
        {
            E2EResults.Note("console.list.scrollToStart", ex.Message);
        }

        Thread.Sleep(500);
    }

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;
}
