using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Bwc.Policy.Tests.Golden;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Filter;

/// <summary>
/// Header sort tests of the Local Group Policy Editor against the capture F/Sort_Columns (VIEW-014, VIEW-015,
/// BEH-025): each of the three columns clicked three times with the order the capture recorded after each click, the
/// order of digits, case and symbols, the default order, the sort of another node, and the sort state across a
/// restart. The capture shows that a click sorts the rows in the order they are shown now, so a tie keeps the order
/// of the sort before (the State and Comment orders depend on the click before). The failure modes are in
/// docs/failure-modes/gpedit-filter.md, sections 14 to 18. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditSortTests
{
    private const string CaseId = "F/Sort_Columns";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Sort_DefaultOrder()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("sort", "default");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-sort-default");
        sandbox.SeedStandard();
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-sort-default", sandbox);
        GoldenCase golden = GoldenCase.Load(CaseId);

        // F/Sort_Columns op 5: a node with no sort mark lists folders by name and then policies in the order of the policy ids
        // (Pol_Sort_Digit1, Pol_Sort_Digit10, Pol_Sort_Digit2, Pol_Sort_LeadingSymbol, Pol_Sort_LowerAlpha, Pol_Sort_NonAscii,
        // Pol_Sort_UpperAlpha, Pol_Sort_Zulu), not by display name.
        run.GoTo(FilterNames.SortCasesPath);
        string[] byId = Names(golden, 5);
        string[] rows = run.WaitRowsInOrder(byId);
        checks.Record("sortCases.order", rows.SequenceEqual(byId), $"rows [{string.Join("|", rows)}]; classic [{string.Join("|", byId)}].");
        checks.Record(
            "sortCases.notByName",
            !byId.Select(StripSuffix).SequenceEqual(byId.Select(StripSuffix).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)),
            "the recorded order is not the display-name order, so this test tells the two orders apart.");
        checks.Record("sortCases.noMark", run.ListReadout().GetValueOrDefault("Sort") == "none" && run.ListReadout().GetValueOrDefault("SortDir") == "None", $"sort mark {run.ListReadout().GetValueOrDefault("Sort")} {run.ListReadout().GetValueOrDefault("SortDir")}; classic shows no mark (the three sort values of op 5 are alike).");
        checks.Record("sortCases.noHeaderMark", new[] { "setting", "state", "comment" }.All(c => !HeaderStatus(run, c).Contains("Sorted", StringComparison.Ordinal)), "no header reports a sort.");

        // F/Sort_Columns op 17: Basics, read after the Comment sort of the other node; all comments there are alike, so the
        // order is the default order of the node. It is the order of the ids: Pol_Both_WithElements, Pol_Class_Both, ...
        run.GoTo(BasicsPath);
        string[] basics = Names(golden, 17);
        checks.Record("basics.order", run.WaitRowsInOrder(basics).SequenceEqual(basics), $"Basics rows [{string.Join("|", run.RowNames())}]; classic [{string.Join("|", basics)}].");

        // F/Nodes_Computer and F/Nodes_User: the list of every node of the fixture, nothing sorted. Folder rows come first by
        // name; policy rows follow in the order of their ids. Pol_SharedList_Two comes before Pol_Shared_Alpha, which tells
        // an ordinal compare of the ids from a case-insensitive one ('L' is below '_', 'l' is above it).
        foreach ((string caseId, string side) in new[] { ("F/Nodes_Computer", Computer), ("F/Nodes_User", User) })
        {
            GoldenCase nodes = GoldenCase.Load(caseId);
            foreach (GoldenOp read in nodes.Ops.Where(o => o.Kind == "readList"))
            {
                GoldenOp select = nodes.Ops.Last(o => o.Index < read.Index && o.Kind == "selectNode");
                string[] path = [Root, side, .. (select.Op["path"] as JsonArray ?? new JsonArray()).Select(p => p!.GetValue<string>())];
                JsonArray recorded = read.Data?["rows"] as JsonArray ?? new JsonArray();
                string[] expected = recorded.Select(r => r!["state"]!.GetValue<string>().Length == 0 ? r["name"]!.GetValue<string>() : FilterNames.Row(r["name"]!.GetValue<string>())).ToArray();
                string id = GpeditCommandRun.Slug(side) + "." + GpeditCommandRun.Slug(string.Join(string.Empty, path.Skip(2)));
                run.GoTo(path);
                string[] actual = run.WaitRowsInOrder(expected);
                checks.Record(id, actual.SequenceEqual(expected), $"{string.Join(" > ", path.Skip(1))}: rows [{string.Join("|", actual)}]; classic [{string.Join("|", expected)}].");
            }
        }

        run.WriteArtifacts("gpedit-sort-default.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Sort_Columns_ThreeClicksEach()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("sort", "columns");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-sort-columns");
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-sort-columns", sandbox);
        GoldenCase golden = GoldenCase.Load(CaseId);
        run.GoTo(FilterNames.SortCasesPath);
        checks.Record("rowsLoaded", ShellRun.WaitUntil(() => run.RowNames().Length == 8), $"rows: {string.Join("|", run.RowNames())}.");

        // Ops 6 to 14: Setting, State and Comment, three clicks each, in this order. The first click of a column sorts
        // ascending, the second descending, the third ascending again.
        string? lastColumn = null;
        string direction = "none";
        foreach (GoldenOp op in golden.Ops.Where(o => o.Kind == "sortColumn"))
        {
            string column = op.Text("column")!;
            string id = column.ToLowerInvariant();
            string label = op.Text("label")!;
            direction = column == lastColumn && direction == "Ascending" ? "Descending" : "Ascending";
            lastColumn = column;
            string[] expected = FirstRows(op);

            bool clicked = ClickHeader(run, id);
            checks.Record($"{id}.{label.Replace(' ', '_')}.clicked", clicked, $"{label}: the header {column} was invoked.");
            string[] rows = run.WaitRowsInOrder(expected);
            checks.Record($"{id}.{label.Replace(' ', '_')}.order", rows.SequenceEqual(expected), $"{label}: rows [{string.Join("|", rows)}]; classic [{string.Join("|", expected)}].");
            bool marked = ShellRun.WaitUntil(() => run.ListReadout().GetValueOrDefault("Sort") == id && run.ListReadout().GetValueOrDefault("SortDir") == direction, TimeSpan.FromSeconds(5));
            checks.Record($"{id}.{label.Replace(' ', '_')}.mark", marked, $"{label}: sort mark {run.ListReadout().GetValueOrDefault("Sort")} {run.ListReadout().GetValueOrDefault("SortDir")}; expected {id} {direction}.");
            checks.Record(
                $"{id}.{label.Replace(' ', '_')}.arrowOnlyHere",
                new[] { "setting", "state", "comment" }.All(c => HeaderStatus(run, c).Contains("Sorted", StringComparison.Ordinal) == (c == id)),
                $"{label}: only the header {column} reports a sort; statuses {string.Join(",", new[] { "setting", "state", "comment" }.Select(c => c + "='" + HeaderStatus(run, c) + "'"))}.");
        }

        // Op 15: the order after the last click is kept when the list is read again.
        string[] last = Names(golden, 15);
        checks.Record("afterLastClick", run.WaitRowsInOrder(last).SequenceEqual(last), $"rows [{string.Join("|", run.RowNames())}]; classic [{string.Join("|", last)}].");
        checks.Record("status", run.WaitStatusNumbers(8), $"status numbers [{string.Join(",", run.StatusNumbers())}]; a sort does not change the count.");

        run.WriteArtifacts("gpedit-sort-columns.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Sort_DigitsCaseAndSymbols()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("sort", "text");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-sort-text");
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-sort-text", sandbox);
        run.GoTo(FilterNames.SortCasesPath);
        _ = ClickHeader(run, "setting");

        // F/Sort_Columns op 6, the first click on Setting. The text order: a leading symbol first, then letters ignoring case,
        // a tie in source order, an accented capital after the plain letters of its word, digits as text (1, 10, 2).
        string[] ascending = run.WaitRowsInOrder(FilterNames.Rows("#Symbol policy", "alpha policy", "Alpha Policy", "Éclair policy", "Policy 1", "Policy 10", "Policy 2", "Zulu sort case"));
        checks.Record("symbolFirst", ascending.FirstOrDefault() == FilterNames.Row("#Symbol policy"), $"first row '{ascending.FirstOrDefault()}'.");
        checks.Record("caseIgnoredTieInSourceOrder", Array.IndexOf(ascending, FilterNames.Row("alpha policy")) == 1 && Array.IndexOf(ascending, FilterNames.Row("Alpha Policy")) == 2, $"alpha rows at {Array.IndexOf(ascending, FilterNames.Row("alpha policy"))} and {Array.IndexOf(ascending, FilterNames.Row("Alpha Policy"))}.");
        checks.Record("accentAfterAlpha", Array.IndexOf(ascending, FilterNames.Row("Éclair policy")) == 3, $"Éclair at {Array.IndexOf(ascending, FilterNames.Row("Éclair policy"))}.");
        checks.Record(
            "digitsAreText",
            Array.IndexOf(ascending, FilterNames.Row("Policy 1")) < Array.IndexOf(ascending, FilterNames.Row("Policy 10")) &&
            Array.IndexOf(ascending, FilterNames.Row("Policy 10")) < Array.IndexOf(ascending, FilterNames.Row("Policy 2")),
            $"digit rows: {string.Join("|", ascending.Where(n => n.StartsWith("Policy ", StringComparison.Ordinal)))}; classic puts Policy 10 before Policy 2.");
        checks.Record("zuluLast", ascending.LastOrDefault() == FilterNames.Row("Zulu sort case"), $"last row '{ascending.LastOrDefault()}'.");
        checks.Record("allEightRows", ascending.Length == 8, $"rows {ascending.Length}.");

        // Op 7, the second click: the exact reverse of the plain letters and digits, with the tie kept in source order.
        _ = ClickHeader(run, "setting");
        string[] descending = run.WaitRowsInOrder(FilterNames.Rows("Zulu sort case", "Policy 2", "Policy 10", "Policy 1", "Éclair policy", "alpha policy", "Alpha Policy", "#Symbol policy"));
        checks.Record("descendingDigits", Array.IndexOf(descending, FilterNames.Row("Policy 2")) < Array.IndexOf(descending, FilterNames.Row("Policy 10")), $"descending rows: {string.Join("|", descending)}.");
        checks.Record("descendingTieKeepsOrder", Array.IndexOf(descending, FilterNames.Row("alpha policy")) < Array.IndexOf(descending, FilterNames.Row("Alpha Policy")), "descending keeps alpha policy before Alpha Policy, as classic does (a stable descending sort).");
        checks.Record("symbolLast", descending.LastOrDefault() == FilterNames.Row("#Symbol policy"), $"last row '{descending.LastOrDefault()}'.");

        run.WriteArtifacts("gpedit-sort-text.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Sort_AnotherNode()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("sort", "node");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-sort-node");
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-sort-node", sandbox);
        GoldenCase golden = GoldenCase.Load(CaseId);

        // Ops 6 to 17: the nine clicks, then another node. The Basics list of the capture (op 17) is the rows in the order
        // of that node, whatever the sort of the first node. Back at the first node its order is kept (op 15).
        run.GoTo(FilterNames.SortCasesPath);
        foreach (GoldenOp op in golden.Ops.Where(o => o.Kind == "sortColumn"))
        {
            _ = ClickHeader(run, op.Text("column")!.ToLowerInvariant());
            _ = run.WaitRowsInOrder(FirstRows(op));
        }

        string[] sortCases = Names(golden, 15);
        checks.Record("firstNode", run.WaitRowsInOrder(sortCases).SequenceEqual(sortCases), $"rows [{string.Join("|", run.RowNames())}].");
        run.GoTo(BasicsPath);
        string[] basics = Names(golden, 17);
        checks.Record("otherNodeRows", run.WaitRowsInOrder(basics).SequenceEqual(basics), $"Basics rows [{string.Join("|", run.RowNames())}]; classic [{string.Join("|", basics)}].");
        run.GoTo(FilterNames.SortCasesPath);
        checks.Record("firstNodeKept", run.WaitRowsInOrder(sortCases).SequenceEqual(sortCases), $"Sort cases rows after the visit [{string.Join("|", run.RowNames())}].");

        // A sort on Basics sorts Basics. The capture cannot tell whether the sort of one node also applies to the next (the Basics
        // comments are all alike, so op 17 reads the same order either way), so nothing is asserted about the other node here.
        run.GoTo(BasicsPath);
        _ = ClickHeader(run, "setting");
        string[] byName = run.RowNames();
        checks.Record("basics.sortedByName", ShellRun.WaitUntil(() => IsSortedIgnoringCase(run.RowNames().Select(StripSuffix))), $"Basics rows after a Setting click: {string.Join("|", byName)}.");

        run.WriteArtifacts("gpedit-sort-node.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Sort_StateSavedAcrossRestart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("sort", "restart");
        const string name = "gpedit-sort-restart";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        FilterSeed.SortCases(sandbox);
        GoldenCase golden = GoldenCase.Load(CaseId);
        string[] settingDescending = FirstRows(golden.Ops.Single(o => o.Index == 7));
        string[] commentAscending = Names(golden, 15);

        // First run: Setting twice gives descending. The window closes and the settings are written.
        using (GpeditRun first = GpeditRun.Open(name, sandbox))
        {
            first.GoTo(FilterNames.SortCasesPath);
            _ = ClickHeader(first, "setting");
            _ = ClickHeader(first, "setting");
            checks.Record("first.descending", first.WaitRowsInOrder(settingDescending).SequenceEqual(settingDescending) && first.ListReadout().GetValueOrDefault("SortDir") == "Descending", $"rows [{string.Join("|", first.RowNames())}].");
            first.WriteArtifacts("gpedit-sort-restart-first.png");
        }

        string? file = sandbox.SettingsFilePath();
        string text = file is null ? string.Empty : File.ReadAllText(file);
        checks.Record("settingsFile", file is not null && text.Contains("setting", StringComparison.OrdinalIgnoreCase) && text.Contains("Descending", StringComparison.OrdinalIgnoreCase), $"the settings file names the sort column and direction; file='{file}'.");

        // Second run: the editor opens on the root, the node is opened again, and the list is already sorted descending.
        using (GpeditRun second = GpeditRun.Open(name, sandbox))
        {
            second.GoTo(FilterNames.SortCasesPath);
            checks.Record("second.order", second.WaitRowsInOrder(settingDescending).SequenceEqual(settingDescending), $"rows [{string.Join("|", second.RowNames())}]; expected descending by Setting.");
            checks.Record("second.mark", ShellRun.WaitUntil(() => second.ListReadout().GetValueOrDefault("Sort") == "setting" && second.ListReadout().GetValueOrDefault("SortDir") == "Descending"), $"mark {second.ListReadout().GetValueOrDefault("Sort")} {second.ListReadout().GetValueOrDefault("SortDir")}.");
            checks.Record("second.header", HeaderStatus(second, "setting").Contains("Sorted", StringComparison.Ordinal), $"header status '{HeaderStatus(second, "setting")}'.");

            // Change the sort in this run: Comment three times is the last order of the capture.
            _ = ClickHeader(second, "comment");
            _ = ClickHeader(second, "comment");
            _ = ClickHeader(second, "comment");
            second.WriteArtifacts("gpedit-sort-restart-second.png");
            checks.Record("second.changed", ShellRun.WaitUntil(() => second.ListReadout().GetValueOrDefault("Sort") == "comment" && second.ListReadout().GetValueOrDefault("SortDir") == "Ascending"), $"mark {second.ListReadout().GetValueOrDefault("Sort")} {second.ListReadout().GetValueOrDefault("SortDir")}.");
        }

        // Third run: the newest sort is the one that was saved.
        using (GpeditRun third = GpeditRun.Open(name, sandbox))
        {
            third.GoTo(FilterNames.SortCasesPath);
            checks.Record("third.mark", ShellRun.WaitUntil(() => third.ListReadout().GetValueOrDefault("Sort") == "comment" && third.ListReadout().GetValueOrDefault("SortDir") == "Ascending"), $"mark {third.ListReadout().GetValueOrDefault("Sort")} {third.ListReadout().GetValueOrDefault("SortDir")}.");
            checks.Record("third.rowsSorted", third.WaitRowsAsSet(commentAscending).Length == 8 && CommentsAscending(third), $"rows [{string.Join("|", third.RowNames())}]; comment cells [{string.Join("|", third.Rows().Select(r => r.Cells.ElementAtOrDefault(2)))}].");
            third.WriteArtifacts("gpedit-sort-restart-third.png");
        }

        checks.AssertAllPassed();
    }

    private static bool ClickHeader(GpeditRun run, string columnId)
    {
        try
        {
            _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
            FlaUI.Core.AutomationElements.AutomationElement? header = run.Shell.Find(GpeditRun.ListRun_HeaderItemId(columnId));
            if (header is null)
            {
                return false;
            }

            header.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(ShellRun.KeySettle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string HeaderStatus(GpeditRun run, string columnId) =>
        run.Shell.Find(GpeditRun.ListRun_HeaderItemId(columnId))?.Properties.ItemStatus.ValueOrDefault ?? string.Empty;

    private static string[] Names(GoldenCase golden, int index)
    {
        JsonObject data = golden.Ops.Single(o => o.Index == index).Data ?? throw new AssertFailedException($"{CaseId} operation {index}: no list was recorded.");
        return (data["rows"] as JsonArray ?? new JsonArray()).Select(r => FilterNames.Row(r!["name"]!.GetValue<string>())).ToArray();
    }

    private static string[] FirstRows(GoldenOp op)
    {
        JsonObject data = op.Data ?? throw new AssertFailedException($"{CaseId} operation {op.Index}: no rows were recorded.");
        return (data["firstRows"] as JsonArray ?? new JsonArray()).Select(r => FilterNames.Row(r!["name"]!.GetValue<string>())).ToArray();
    }

    private static string StripSuffix(string name) => name.EndsWith(FilterNames.Suffix, StringComparison.Ordinal) ? name[..^FilterNames.Suffix.Length] : name;

    private static bool IsSortedIgnoringCase(IEnumerable<string> names)
    {
        string[] list = names.ToArray();
        for (int i = 1; i < list.Length; i++)
        {
            if (string.Compare(list[i - 1], list[i], System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CompareOptions.IgnoreCase) > 0)
            {
                return false;
            }
        }

        return list.Length > 1;
    }

    private static bool CommentsAscending(GpeditRun run)
    {
        string[] comments = run.Rows().Select(r => r.Cells.ElementAtOrDefault(2) ?? string.Empty).ToArray();
        return comments.SequenceEqual(comments.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
    }
}
