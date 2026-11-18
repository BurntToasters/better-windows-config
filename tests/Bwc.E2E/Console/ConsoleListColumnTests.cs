using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>Virtual keys the console list tests press.</summary>
internal static class ListKeys
{
    internal const ushort Return = 0x0D;
    internal const ushort Space = 0x20;
    internal const ushort PageUp = 0x21;
    internal const ushort PageDown = 0x22;
    internal const ushort End = 0x23;
    internal const ushort Home = 0x24;
    internal const ushort Left = 0x25;
    internal const ushort Up = 0x26;
    internal const ushort Right = 0x27;
    internal const ushort Down = 0x28;
    internal const ushort A = 0x41;
}

/// <summary>
/// Console list tests for the columns, the header, sorting and resizing. The list is the shared console result list
/// (src/Bwc.UI/Console/List) inside the Local Group Policy Editor host; the failure modes are in
/// docs/failure-modes/console-list.md. Every test writes artifacts/e2e/visual/console-list/console-list-&lt;name&gt;.json
/// and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class ConsoleListColumnTests
{
    private static readonly string[] ColumnIds = ["name", "state", "comment"];
    private static readonly string[] ColumnNames = ["Name", "State", "Comment"];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_ColumnsAndHeaders()
    {
        var checks = new ShellChecks("columns", "list");
        using (ListRun run = ListRun.Open("columns", 8))
        {
            AutomationElement? list = run.Element;
            checks.Record(
                "listType",
                list?.ControlType == ControlType.List && ShellRun.SafeName(list) == "Console Embedded Window Results",
                $"list control type={list?.ControlType}, name='{ShellRun.SafeName(list)}'; classic is a List named by an internal caption.");
            var table = list?.Patterns.Table.PatternOrDefault;
            var grid = list?.Patterns.Grid.PatternOrDefault;
            checks.Record(
                "tableAndGrid",
                table is not null && grid is not null && grid.RowCount.ValueOrDefault == 8 && grid.ColumnCount.ValueOrDefault == 3,
                $"table={table is not null}, grid={grid is not null}, rows={grid?.RowCount.ValueOrDefault}, columns={grid?.ColumnCount.ValueOrDefault}; classic has Scroll, Grid and Table.");
            checks.Record("scrollPattern", list?.Patterns.Scroll.IsSupported == true, "the list supports the Scroll pattern, as classic does.");

            Rectangle? header = run.Rect(ListRun.HeaderId);
            double headerHeight = run.Logical(header?.Height ?? -1);
            checks.Record("headerHeight", Near(headerHeight, 24, 1.5), $"header {header?.Height} px = {headerHeight:0.##} DIP; classic is 24 (48 px at 192 DPI).");

            var left = new List<int>();
            var names = new List<string>();
            var widths = new List<double>();
            for (int i = 0; i < ColumnIds.Length; i++)
            {
                AutomationElement? item = run.Shell.Find(ListRun.HeaderItemId(ColumnIds[i]));
                names.Add(ShellRun.SafeName(item));
                Rectangle rect = item?.BoundingRectangle ?? Rectangle.Empty;
                left.Add(rect.Left);
                widths.Add(run.Logical(rect.Width));
                checks.Record($"headerType{i}", item?.ControlType == ControlType.HeaderItem, $"header '{ColumnIds[i]}' control type={item?.ControlType}; classic uses HeaderItem.");
            }

            checks.Record("headerNames", names.SequenceEqual(ColumnNames), $"header names=[{string.Join(", ", names)}].");
            checks.Record("headerOrder", left[0] < left[1] && left[1] < left[2], $"header lefts={string.Join(", ", left)}.");
            double[] expectedWidths = [350, 100, 200];
            checks.Record(
                "headerWidths",
                Enumerable.Range(0, 3).All(i => Near(widths[i], expectedWidths[i], 1.5)),
                $"header widths={string.Join(", ", widths.Select(w => w.ToString("0.#")))} DIP; expected 350, 100, 200 (the first is the probed classic Setting width).");
            run.Measure("headerWidths", widths);

            List<ListItemView> items = run.Items();
            checks.Record("rowsRealized", items.Count == 8, $"realized rows={items.Count}; the list has 8 and all fit.");
            checks.Record(
                "rowNames",
                items.Select(item => item.Name).SequenceEqual(Enumerable.Range(0, 8).Select(run.ExpectedName)),
                $"row names=[{string.Join(", ", items.Select(item => item.Name))}]; the row name is the first column text plus the word the host adds.");
            AutomationElement? row0 = run.ItemElement(0);
            checks.Record(
                "rowType",
                row0?.ControlType == ControlType.ListItem
                    && row0.Patterns.Invoke.IsSupported
                    && row0.Patterns.SelectionItem.IsSupported
                    && row0.Patterns.ScrollItem.IsSupported,
                $"row type={row0?.ControlType}; classic rows are ListItem with Invoke, SelectionItem and ScrollItem.");
            if (items.Count >= 2)
            {
                double step = run.Logical(items[1].Rect.Top - items[0].Rect.Top);
                double height = run.Logical(items[0].Rect.Height);
                checks.Record("rowHeight", Near(step, 19, 1) && Near(height, 19, 1.5), $"row step={step:0.##} DIP, row height={height:0.##} DIP; classic is 19 (38 px at 192 DPI).");
            }

            // The cell texts through the Grid pattern: the first column, State and Comment of row 2.
            string cells = string.Empty;
            try
            {
                string[] texts = Enumerable.Range(0, 3).Select(c => ShellRun.SafeName(grid?.GetItem(2, c))).ToArray();
                cells = string.Join("|", texts);
                checks.Record(
                    "cellNames",
                    texts.SequenceEqual([run.ExpectedName(2), ListRun.ExpectedState(2), ListRun.ExpectedComment(2)]),
                    $"cells of row 2 through the Grid pattern=[{cells}].");
            }
            catch (Exception ex)
            {
                checks.Record("cellNames", false, $"reading the cells through the Grid pattern failed: {ex.Message}");
            }

            // Row 3 has a module icon that does not exist: the glyph shows, the row keeps its height.
            using (Bitmap? picture = run.Shell.CaptureClient(out Point origin))
            {
                ListItemView? row3 = items.FirstOrDefault(item => item.Index == 3);
                if (picture is not null && row3 is not null)
                {
                    int size = (int)Math.Round(16 * run.Scale);
                    var iconRect = new Rectangle(row3.Rect.Left + (int)Math.Round(2 * run.Scale), row3.Rect.Top + ((row3.Rect.Height - size) / 2), size, size);
                    int ink = run.InkInScreenRect(picture, origin, iconRect);
                    checks.Record("iconFallback", ink > 8, $"row 3 uses a module icon that does not exist; ink in its icon cell={ink} pixels (the fallback glyph).");
                    run.Measure("iconFallbackInk", ink);
                    ListItemView? row2 = items.FirstOrDefault(item => item.Index == 2);
                    if (row2 is not null)
                    {
                        var kindRect = new Rectangle(row2.Rect.Left + (int)Math.Round(2 * run.Scale), row2.Rect.Top + ((row2.Rect.Height - size) / 2), size, size);
                        int kindInk = run.InkInScreenRect(picture, origin, kindRect);
                        checks.Record("iconKind", kindInk > 8, $"row 2 uses a classic icon; ink in its icon cell={kindInk} pixels.");
                    }
                }
                else
                {
                    checks.Note("iconFallback", "picture not judged; the icon pixels were not checked.");
                }
            }

            run.Measure("cells", cells);
            run.Measure("readout", run.RawReadout());
            run.WriteArtifacts("console-list-columns.png");
        }

        // An empty list keeps its header, and the keys and the context request do nothing wrong.
        using (ListRun empty = ListRun.Open("columns-empty", 0))
        {
            checks.Record("emptyHeader", empty.Rect(ListRun.HeaderItemId("name")) is not null && empty.Items().Count == 0, "an empty list shows the header and no rows.");
            checks.Record("emptyFocus", empty.TryFocusList(), $"the empty list takes the keyboard focus (focused id={empty.Shell.FocusedId()}).");
            foreach (ushort key in new ushort[] { ListKeys.Down, ListKeys.Up, ListKeys.Home, ListKeys.End, ListKeys.PageDown, ListKeys.PageUp, ListKeys.Right, ListKeys.Return })
            {
                empty.Shell.Tap(key);
            }

            checks.Record(
                "emptyKeys",
                empty.Focus == -1 && empty.SelectedCount == 0 && empty.Shell.StatusText().Length == 0,
                $"after Down, Up, Home, End, PageDown, PageUp, Right and Enter: focus={empty.Focus}, selected={empty.SelectedCount}, status='{empty.Shell.StatusText()}'; nothing is raised.");
            empty.Shell.Chord(RawKeyboard.Shift, ShellRun.VkF10);
            bool context = ShellRun.WaitUntil(() => empty.Shell.StatusText() == "Context menu: pane=Result source=key n=1");
            checks.Record("emptyContext", context, $"status after Shift+F10 in the empty list='{empty.Shell.StatusText()}'; one background request.");
            empty.WriteArtifacts("console-list-empty.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_SortBothWays()
    {
        var checks = new ShellChecks("sort", "list");
        using ListRun run = ListRun.Open("sort", 12);
        int[] source = Enumerable.Range(0, 12).ToArray();

        string[] Names(IEnumerable<int> order) => order.Select(run.ExpectedName).ToArray();
        string[] Shown() => run.Items().Select(item => item.Name).ToArray();

        checks.Record("unsorted", Shown().SequenceEqual(Names(source)), $"order before any sort=[{string.Join(", ", Shown())}]; the caller's order.");
        checks.Record("noArrowAtStart", ColumnIds.All(id => run.HeaderStatus(id).Length == 0 && run.ArrowInk(id) <= 2), "no header shows an arrow before the first sort.");

        void ClickHeader(string column, string expectedDirection)
        {
            AutomationElement? item = run.Shell.Find(ListRun.HeaderItemId(column));
            item?.Click();
            _ = ShellRun.WaitUntil(() => run.Str("Sort") == column && run.Str("SortDir") == expectedDirection, TimeSpan.FromSeconds(8));
            Thread.Sleep(400);
        }

        void CheckSorted(string id, string column, string direction, IEnumerable<int> order)
        {
            string[] expected = Names(order);
            string[] shown = Shown();
            checks.Record(id + "Order", shown.SequenceEqual(expected), $"{column} {direction}: shown=[{string.Join(", ", shown)}], expected=[{string.Join(", ", expected)}].");
            checks.Record(id + "State", run.Str("Sort") == column && run.Str("SortDir") == direction, $"readout sort={run.Str("Sort")} {run.Str("SortDir")}; expected {column} {direction}.");
            string status = run.HeaderStatus(column);
            checks.Record(
                id + "Status",
                status.Contains(direction == "Ascending" ? "ascending" : "descending", StringComparison.OrdinalIgnoreCase),
                $"header item status of {column}='{status}'.");
            int arrow = run.ArrowInk(column);
            int others = ColumnIds.Where(c => c != column).Sum(c => Math.Max(0, run.ArrowInk(c)));
            checks.Record(id + "Arrow", arrow > 8 && others <= 4, $"arrow ink: sorted column {column}={arrow} pixels, the other columns={others}.");
        }

        IEnumerable<int> ByKey(Func<int, string> key, bool descending, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            // OrderBy is stable, so rows with an equal key keep the source order in both directions.
            IOrderedEnumerable<int> ordered = descending
                ? source.OrderByDescending(key, StringComparer.FromComparison(comparison))
                : source.OrderBy(key, StringComparer.FromComparison(comparison));
            return ordered;
        }

        ClickHeader("name", "Ascending");
        CheckSorted("nameAsc", "name", "Ascending", ByKey(run.ExpectedName, false));
        string[] firstAscending = Shown();
        ClickHeader("name", "Descending");
        CheckSorted("nameDesc", "name", "Descending", ByKey(run.ExpectedName, true));
        ClickHeader("name", "Ascending");
        checks.Record("nameAgain", Shown().SequenceEqual(firstAscending), "ascending, descending, ascending gives the first order again.");

        ClickHeader("state", "Ascending");
        CheckSorted("stateAsc", "state", "Ascending", ByKey(ListRun.ExpectedState, false));
        ClickHeader("state", "Descending");
        CheckSorted("stateDesc", "state", "Descending", ByKey(ListRun.ExpectedState, true));
        // Equal keys keep the caller's order in both directions: inside the Not configured group the rows 0, 3, 6, 9
        // stay in order.
        string[] group = Shown().Where(name => Enumerable.Range(0, 12).Where(i => i % 3 == 0).Select(run.ExpectedName).Contains(name)).ToArray();
        checks.Record(
            "equalKeysStable",
            group.SequenceEqual(new[] { 0, 3, 6, 9 }.Select(run.ExpectedName)),
            $"descending State: the rows of one state are [{string.Join(", ", group)}]; they keep the source order.");

        ClickHeader("comment", "Ascending");
        CheckSorted("commentAsc", "comment", "Ascending", ByKey(ListRun.ExpectedComment, false));
        ClickHeader("comment", "Descending");
        CheckSorted("commentDesc", "comment", "Descending", ByKey(ListRun.ExpectedComment, true));

        run.Measure("finalReadout", run.RawReadout());
        run.WriteArtifacts("console-list-sort.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    public void Console_List_ResizeAndAutoFit()
    {
        var checks = new ShellChecks("resize", "list");
        using ListRun run = ListRun.Open("resize", 12, new Dictionary<string, string> { ["BWC_E2E_CONSOLE_LONGTEXT"] = "1" });
        double scale = run.Scale;

        double[] Widths() => run.Doubles("Widths");
        Point DividerCenter(string column)
        {
            Rectangle rect = run.Rect(ListRun.DividerId(column)) ?? throw new InvalidOperationException($"The divider of {column} is not showing.");
            return new Point(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
        }

        checks.Record("startWidths", Widths().SequenceEqual([350, 100, 200]), $"widths at start=[{string.Join(", ", Widths())}].");

        // A drag of 60 DIP to the right widens the first column by 60.
        Point center = DividerCenter("name");
        ShellRun.DragStart(center, (int)Math.Round(60 * scale));
        ShellRun.DragEnd();
        double widened = Widths()[0];
        checks.Record("dragWider", Near(widened, 410, 2), $"first column after a 60 DIP drag right={widened:0.##}; expected 410.");
        checks.Record("dragDidNotSort", run.Str("Sort") == "none", $"sort after the drag='{run.Str("Sort")}'; a divider drag is not a header click.");
        run.Measure("afterDragRight", widened);

        // A double click on the divider fits the column to its widest text (the header or a cell).
        Point divider = DividerCenter("name");
        run.Shell.Find(ListRun.DividerId("name"))?.DoubleClick();
        _ = ShellRun.WaitUntil(() => Widths()[0] < 300, TimeSpan.FromSeconds(5));
        double fitted = Widths()[0];
        checks.Record("autoFit", fitted is > 60 and < 170, $"first column after the divider double click={fitted:0.##} DIP; the widest text is 'Setting 00011' (about 85 DIP).");
        checks.Record("autoFitDidNotSort", run.Str("Sort") == "none", $"sort after the double click='{run.Str("Sort")}'.");
        run.Measure("autoFit", fitted);
        run.Measure("dividerBeforeAutoFit", divider.ToString());

        // A drag far to the left stops at the minimum width, and the divider can still be taken.
        center = DividerCenter("name");
        ShellRun.DragStart(center, -4000);
        ShellRun.DragEnd();
        double minimum = Widths()[0];
        checks.Record("dragToZero", Near(minimum, 12, 0.6), $"first column after a drag far left={minimum:0.##}; the minimum is 12 DIP, never zero.");
        center = DividerCenter("name");
        ShellRun.DragStart(center, (int)Math.Round(30 * scale));
        ShellRun.DragEnd();
        double regrown = Widths()[0];
        checks.Record("dragFromMinimum", Near(regrown, 42, 2), $"first column after a 30 DIP drag from the minimum={regrown:0.##}; expected 42.");

        // A cell of 5,000 characters: the fitted width is limited and the rows keep their height.
        run.Shell.Find(ListRun.DividerId("comment"))?.DoubleClick();
        _ = ShellRun.WaitUntil(() => Widths()[2] > 1000, TimeSpan.FromSeconds(8));
        double comment = Widths()[2];
        checks.Record("longTextFit", Near(comment, 4096, 1), $"comment column after the divider double click={comment:0.##}; a 5,000 character cell is limited to 4096.");
        List<ListItemView> items = run.Items();
        double rowHeight = items.Count > 0 ? run.Logical(items[0].Rect.Height) : -1;
        checks.Record("longTextRowHeight", Near(rowHeight, 19, 1.5) && items.Count == 12, $"row height with the long cell={rowHeight:0.##} DIP, realized rows={items.Count}.");

        run.Measure("widths", Widths());
        run.WriteArtifacts("console-list-resize.png");
        checks.AssertAllPassed();
    }

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;
}
