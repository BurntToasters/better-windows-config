using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Accessibility;

/// <summary>
/// UI Automation checks for the value list on a seeded HKCU sandbox. The list is a Table and a Grid: the row and column
/// counts match the values, the headers are Name, Type, and Data, and each row supports SelectionItem. The name cell
/// of each row has the value name (the default value is "(Default)"), the Data cell has its own text, and both are
/// reachable through GridPattern.GetItem. F2 opens the rename box named "Rename value". Failure modes:
/// docs/failure-modes/accessibility.md section 2.
/// </summary>
[TestClass]
public sealed class ListAccessibilityTests
{
    private const string DefaultName = "(Default)";
    private const string AlphaName = "AlphaString";
    private const string AlphaData = "alpha";
    private const string RenameBoxId = "ListRenameBox";
    private const int ColumnCount = 3;
    private static readonly string[] HeaderNames = ["Name", "Type", "Data"];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Accessibility")]
    [TestCategory("Mutating")]
    public void List_ExposesTableGridHeadersRowsAndCells()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("a11y.list.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        SandboxRegistry? sandbox = null;
        try
        {
            sandbox = ControlTestSupport.SeedRegistry(config, 0);
            using AppSession session = AccessibilitySupport.Launch(config, sandbox, "a11y-list");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "a11y.list.navigate");
            using var driver = new EditingDriver(session);
            CheckList(session, driver);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(0, failures.Count, $"List accessibility checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void CheckList(AppSession session, EditingDriver driver)
    {
        AutomationElement? list = session.Find(ControlTestSupport.ListControl);
        E2EResults.Record(
            "a11y.list.table-type",
            AccessibilitySupport.ControlTypeOf(list) == "Table",
            $"list control type={AccessibilitySupport.ControlTypeOf(list)}.");
        if (list is null)
        {
            return;
        }

        bool gridSupported = AccessibilitySupport.HasPattern(list, e => e.Patterns.Grid.IsSupported);
        bool tableSupported = AccessibilitySupport.HasPattern(list, e => e.Patterns.Table.IsSupported);
        E2EResults.Record("a11y.list.grid-table-supported", gridSupported && tableSupported, $"Grid={gridSupported}, Table={tableSupported}.");

        AutomationElement[] rows = ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem);
        int rowCount = rows.Length;
        int reportedRows = gridSupported ? list.Patterns.Grid.Pattern.RowCount.Value : -1;
        int reportedColumns = gridSupported ? list.Patterns.Grid.Pattern.ColumnCount.Value : -1;
        E2EResults.Record(
            "a11y.list.counts",
            reportedRows == rowCount && reportedRows > 0 && reportedColumns == ColumnCount,
            $"Grid RowCount={reportedRows} (DataItem rows={rowCount}), ColumnCount={reportedColumns} (expected {ColumnCount}).");

        string[] headers = list.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .Select(AccessibilitySupport.NameOf)
            .ToArray();
        E2EResults.Record(
            "a11y.list.headers",
            headers.SequenceEqual(HeaderNames, StringComparer.Ordinal),
            $"header names=[{string.Join(",", headers)}], expected [{string.Join(",", HeaderNames)}].");

        CheckRowsAndCells(session, list, reportedRows, gridSupported);
        CheckDefaultRow(rows);
        CheckSelection(session, rows);
        CheckRenameName(session, driver);
    }

    /// <summary>
    /// Each realized row has a name cell (column 0) whose Name is the value name, and a data cell (column 2) whose
    /// Name is the data text. Both come from GridPattern.GetItem, and the two cells are different elements.
    /// </summary>
    private static void CheckRowsAndCells(AppSession session, AutomationElement list, int rowCount, bool gridSupported)
    {
        if (!gridSupported || rowCount <= 0)
        {
            E2EResults.Record("a11y.list.cells", false, "no grid to read cells from.");
            return;
        }

        var nameMismatch = new List<string>();
        var sameCell = new List<string>();
        bool alphaDataOk = false;
        for (int row = 0; row < rowCount; row++)
        {
            AutomationElement? rowElement = session.Find("ListRow_" + row.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string rowName = AccessibilitySupport.NameOf(rowElement);
            AutomationElement? nameCell = CellAt(list, row, 0);
            AutomationElement? dataCell = CellAt(list, row, 2);

            if (AccessibilitySupport.NameOf(nameCell) != rowName)
            {
                nameMismatch.Add($"row {row} '{rowName}' name cell='{AccessibilitySupport.NameOf(nameCell)}'");
            }

            if (AccessibilitySupport.NameOf(nameCell) == AccessibilitySupport.NameOf(dataCell) && rowName.Length > 0)
            {
                sameCell.Add($"row {row} '{rowName}'");
            }

            if (rowName == AlphaName)
            {
                alphaDataOk = AccessibilitySupport.NameOf(dataCell) == AlphaData;
            }
        }

        E2EResults.Record(
            "a11y.list.name-cell",
            nameMismatch.Count == 0,
            nameMismatch.Count == 0
                ? "every name cell carries its value name."
                : $"name cells that do not carry the value name: {string.Join("; ", nameMismatch)}.");
        E2EResults.Record(
            "a11y.list.cells-distinct",
            sameCell.Count == 0,
            sameCell.Count == 0 ? "name and data cells are separate elements." : $"name and data cells are the same element: {string.Join("; ", sameCell)}.");
        E2EResults.Record("a11y.list.data-cell", alphaDataOk, $"the data cell of '{AlphaName}' reads '{AlphaData}'.");
    }

    /// <summary>The cell at a grid position through GridPattern.GetItem, or null when the grid does not return one.</summary>
    private static AutomationElement? CellAt(AutomationElement list, int row, int column)
    {
        try
        {
            return list.Patterns.Grid.Pattern.GetItem(row, column);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CheckDefaultRow(AutomationElement[] rows)
    {
        bool found = rows.Any(row => AccessibilitySupport.NameOf(row) == DefaultName);
        E2EResults.Record("a11y.list.default-row", found, $"a row named '{DefaultName}' exists={found}.");
    }

    /// <summary>SelectionItem on a row: Select makes that row the only selected row.</summary>
    private static void CheckSelection(AppSession session, AutomationElement[] rows)
    {
        AutomationElement? alpha = rows.FirstOrDefault(row => AccessibilitySupport.NameOf(row) == AlphaName);
        bool supported = AccessibilitySupport.HasPattern(alpha, e => e.Patterns.SelectionItem.IsSupported);
        E2EResults.Record("a11y.list.selection-supported", supported, "the row supports SelectionItem.");
        if (alpha is null || !supported)
        {
            return;
        }

        ControlTestSupport.Select(alpha);
        bool selected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindRow(session, AlphaName)),
            TimeSpan.FromSeconds(3));
        E2EResults.Record("a11y.list.select", selected, $"Select on '{AlphaName}' set IsSelected={selected}.");
    }

    private static AutomationElement? FindRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, name);

    /// <summary>F2 on the selected value opens the inline rename box named "Rename value", and Escape cancels it.</summary>
    private static void CheckRenameName(AppSession session, EditingDriver driver)
    {
        _ = driver;
        AutomationElement? alpha = FindRow(session, AlphaName);
        if (alpha is not null)
        {
            ControlTestSupport.Select(alpha);
        }

        _ = ControlTestSupport.EnsureForeground(session);
        Keyboard.Type(VirtualKeyShort.F2);
        AutomationElement? box = AccessibilitySupport.WaitForId(session, RenameBoxId, TimeSpan.FromSeconds(3));
        string name = AccessibilitySupport.NameOf(box);
        E2EResults.Record("a11y.list.rename-name", name == "Rename value", $"rename box found={box is not null}, name='{name}'.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        _ = ControlTestSupport.WaitUntil(() => session.Find(RenameBoxId) is null, TimeSpan.FromSeconds(3));
    }
}
