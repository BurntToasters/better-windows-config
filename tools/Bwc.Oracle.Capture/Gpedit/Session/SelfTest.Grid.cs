using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The decision "which children of the Show Contents grid are data rows, which cells are data cells, which row is the new row". Failure mode 37
// of the driver document. These cases were written before the code they check. The shapes are the ones the 2026-11-21 run on classic recorded.
internal static partial class SelfTest
{
    private static GridCellInfo RowHeaderCell(int row) => new("Header", string.Empty, "Row " + row, false);

    private static GridCellInfo DataCell(int row, string? value, string column = "Value") =>
        new("Edit", value, $"{column} Row {row}, Not sorted.", true);

    private static GridChildInfo FormsTopRow(params string[] columns) =>
        new("Custom", "Top Row", new[] { new GridCellInfo("Header", null, "Top Left Header Cell", false) }
            .Concat(columns.Select(c => new GridCellInfo("Header", null, c, false))).ToList());

    private static GridChildInfo FormsRow(int row, params string?[] values) =>
        new("Custom", "Row " + row, new[] { RowHeaderCell(row) }.Concat(values.Select(v => DataCell(row, v))).ToList());

    private static void GridCases(RepoPaths repo, Action<string, Action> Case)
    {
        Case("grid: the shape of operation 2 has a header row, one filled row and the new row", () =>
        {
            GridShape shape = GridRead.Classify(new[] { FormsTopRow("Value"), FormsRow(0, "e"), FormsRow(1, "(null)") });
            Expect(shape.HeaderChildren.SequenceEqual(new[] { 0 }), "the header row is not child 0: " + string.Join(",", shape.HeaderChildren));
            Expect(shape.All.Count == 2, $"{shape.All.Count} data rows, not 2");
            Expect(shape.Placeholder == 1, "the new row is not the last data row");
            Expect(shape.Rows.Count == 1, "the new row was counted as a row of the list");
            GridRowRead row = shape.Rows[0];
            Expect(row.ChildIndex == 1 && row.Name == "Row 0", "the data row is not child 1 'Row 0'");
            Expect(row.RowHeader == 0, "the row header is not cell 0");
            Expect(row.CellIndexes.SequenceEqual(new[] { 1 }), "the data cell is not cell 1");
            Expect(row.Texts.SequenceEqual(new[] { "e" }), "the data cell text is not 'e'");
            Expect(!row.IsEmpty, "the filled row is empty");
            Expect(shape.All[1].ChildIndex == 2 && shape.All[1].IsEmpty, "the new row is not child 2 and empty");
        });

        Case("grid: the shape of operation 4 holds the header row and only the new row", () =>
        {
            GridShape shape = GridRead.Classify(new[] { FormsTopRow("Value"), FormsRow(0, "(null)") });
            Expect(shape.All.Count == 1 && shape.Placeholder == 0, "the one row is not the new row");
            Expect(shape.Rows.Count == 0, "the new row was counted as a row of the list");
            Expect(shape.Rows.Count(r => !r.IsEmpty) == 0, "a filled row exists in an empty list");
        });

        Case("grid: an empty grid with only the header row has no data row and no new row", () =>
        {
            GridShape shape = GridRead.Classify(new[] { FormsTopRow("Value") });
            Expect(shape.HeaderChildren.SequenceEqual(new[] { 0 }), "the header row was not found");
            Expect(shape.All.Count == 0 && shape.Rows.Count == 0 && shape.Placeholder is null, "a row was invented in an empty grid");
            GridShape none = GridRead.Classify(Array.Empty<GridChildInfo>());
            Expect(none.All.Count == 0 && none.Placeholder is null && none.HeaderChildren.Count == 0, "a grid with no child has rows or a header");
        });

        Case("grid: a grid of DataItem rows works as before, and a cell without grid item keeps the name fallback", () =>
        {
            var children = new List<GridChildInfo>
            {
                new("Header", "Value", new List<GridCellInfo>()),
                new("DataItem", "Row 0", new[] { new GridCellInfo("Edit", "alpha", "Value Row 0", true) }),
                new("DataItem", "Row 1", new[] { new GridCellInfo("Edit", "beta", "Value Row 1", true) }),
                new("ScrollBar", "Vertical", new[] { new GridCellInfo("Button", null, "Page up", false) }),
            };
            GridShape shape = GridRead.Classify(children);
            Expect(shape.All.Count == 2 && shape.Placeholder is null, "DataItem rows were not taken as they are");
            Expect(shape.Rows.Select(r => r.ChildIndex).SequenceEqual(new[] { 1, 2 }), "the DataItem children are not the rows");
            Expect(shape.Rows[0].CellIndexes.SequenceEqual(new[] { 0 }) && shape.Rows[0].Texts[0] == "alpha", "the first DataItem cell was not read");

            var plain = new List<GridChildInfo>
            {
                new("Custom", "r0", new[] { new GridCellInfo("Text", null, "apple", false), new GridCellInfo("Text", null, "pear", false) }),
            };
            GridShape named = GridRead.Classify(plain);
            Expect(named.Rows.Count == 1 && named.Rows[0].Texts.SequenceEqual(new[] { "apple", "pear" }), "the name fallback of a cell without grid item was lost");
        });

        Case("grid: a two column grid keeps both data cells and skips the row header", () =>
        {
            var children = new List<GridChildInfo>
            {
                FormsTopRow("Name", "Value"),
                new("Custom", "Row 0", new[] { RowHeaderCell(0), DataCell(0, "name1", "Name"), DataCell(0, "value1") }),
                new("Custom", "Row 1", new[] { RowHeaderCell(1), DataCell(1, "name2", "Name"), DataCell(1, "(null)") }),
                new("Custom", "Row 2", new[] { RowHeaderCell(2), DataCell(2, "(null)", "Name"), DataCell(2, "(null)") }),
            };
            GridShape shape = GridRead.Classify(children);
            Expect(shape.Placeholder == 2 && shape.Rows.Count == 2, "the new row of a two column grid was not found");
            Expect(shape.Rows[0].CellIndexes.SequenceEqual(new[] { 1, 2 }), "the data cells of a two column row are not cells 1 and 2");
            Expect(shape.Rows[0].Texts.SequenceEqual(new[] { "name1", "value1" }), "the two cells of row 0 were not read in order");
            Expect(!shape.Rows[1].IsEmpty, "a row with a name and a null value was taken as empty");
            Expect(shape.Rows[1].Texts.SequenceEqual(new[] { "name2", "(null)" }), "the raw text of a null cell was changed");
            Expect(GridRead.Shown("(null)") == string.Empty && GridRead.Shown("name2") == "name2", "(null) is not shown as empty");
        });

        Case("grid: the text (null) is empty only as a whole cell text, and the new row is the last row only", () =>
        {
            Expect(GridRead.IsEmptyText("(null)") && GridRead.IsEmptyText(null) && GridRead.IsEmptyText("") && GridRead.IsEmptyText("  "), "an empty text is not empty");
            Expect(!GridRead.IsEmptyText("(null) x") && !GridRead.IsEmptyText("x(null)") && !GridRead.IsEmptyText("(NULL)"), "a text that only contains (null) is empty");

            GridShape leading = GridRead.Classify(new[] { FormsTopRow("Value"), FormsRow(0, "(null) x"), FormsRow(1, "(null)") });
            Expect(leading.Placeholder == 1 && leading.Rows.Count == 1 && !leading.Rows[0].IsEmpty, "a row whose text starts with (null) was taken as empty");

            GridShape middle = GridRead.Classify(new[] { FormsTopRow("Value"), FormsRow(0, "a"), FormsRow(1, ""), FormsRow(2, "b") });
            Expect(middle.Placeholder is null && middle.Rows.Count == 3, "an empty row before a filled row was taken as the new row");
            Expect(middle.Rows[1].IsEmpty && middle.Rows.Count(r => !r.IsEmpty) == 2, "the empty row in the middle was not kept as an empty row");

            GridShape both = GridRead.Classify(new[] { FormsTopRow("Value"), FormsRow(0, "a"), FormsRow(1, ""), FormsRow(2, "(null)") });
            Expect(both.Placeholder == 2 && both.Rows.Count == 2 && both.Rows[1].IsEmpty, "an empty committed row before the new row was lost");

            GridShape nullValue = GridRead.Classify(new[] { FormsTopRow("Value"), FormsRow(0, "a"), new GridChildInfo("Custom", "Row 1", new[] { RowHeaderCell(1), DataCell(1, null) }) });
            Expect(nullValue.Placeholder == 1, "a data cell with no value pattern is not an empty cell");
        });

        Case("grid: a cell editor, a scroll bar and a child without cells are never rows", () =>
        {
            var children = new List<GridChildInfo>
            {
                FormsTopRow("Value"),
                FormsRow(0, "a"),
                new("Edit", "Value", new List<GridCellInfo>()),
                new("Edit", "editor", new[] { new GridCellInfo("Text", "a", "a", false) }),
                new("ScrollBar", "Vertical", new[] { new GridCellInfo("Button", null, "Line down", false) }),
                new("Custom", "gap", new List<GridCellInfo>()),
                FormsRow(1, "(null)"),
            };
            GridShape shape = GridRead.Classify(children);
            Expect(shape.All.Select(r => r.ChildIndex).SequenceEqual(new[] { 1, 6 }), "rows: " + string.Join(",", shape.All.Select(r => r.ChildIndex)));
            Expect(shape.Placeholder == 1 && shape.Rows.Count == 1, "the new row was not found around other children");
        });

        Case("grid: control type names are read with or without the ControlType prefix, in any case", () =>
        {
            var children = new List<GridChildInfo>
            {
                new("ControlType.Custom", "Top Row", new[] { new GridCellInfo("ControlType.Header", null, "Value", false) }),
                new("custom", "Row 0", new[] { new GridCellInfo("header", "", "Row 0", false), new GridCellInfo("ControlType.Edit", "x", "Value Row 0", true) }),
            };
            GridShape shape = GridRead.Classify(children);
            Expect(shape.HeaderChildren.SequenceEqual(new[] { 0 }) && shape.Rows.Count == 1 && shape.Rows[0].Texts[0] == "x", "the prefix or the case changed the answer");
        });

        Case("grid: a cell with grid item and no value pattern reads empty, not as its name", () =>
        {
            GridShape shape = GridRead.Classify(new[] { FormsTopRow("Value"), new GridChildInfo("Custom", "Row 0", new[] { RowHeaderCell(0), new GridCellInfo("Edit", null, "Value Row 0, Not sorted.", true) }) });
            Expect(shape.All[0].Texts.SequenceEqual(new[] { string.Empty }), "the description of a cell was read as its content");
            Expect(shape.Placeholder == 0, "a last row whose only data cell has no value is not the new row");
        });

        Case("grid: no planned list cell is the text (null)", () =>
        {
            MatrixPlan plan = MatrixPlan.Build(repo);
            int cells = 0;
            foreach (MatrixCase c in plan.Cases)
            {
                foreach (GpeditOp op in c.Ops)
                {
                    foreach (OptionSpec option in op.Options ?? new List<OptionSpec>())
                    {
                        foreach (string cell in (option.Rows ?? new List<List<string>>()).SelectMany(r => r))
                        {
                            cells++;
                            Expect(!string.Equals(cell, "(null)", StringComparison.Ordinal), $"{c.FullId} plans the list cell text (null), which the grid read takes as empty");
                        }
                    }
                }
            }

            Expect(cells > 0, "no list cell is planned, so the check proved nothing");
        });

        Case("grid: the classification and the clear log never reach a golden", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            var contents = new JsonObject
            {
                ["rowCount"] = 0,
                ["gridRead"] = new JsonObject { ["rowNames"] = new JsonArray("ZZ-MARKER-grid-row"), ["placeholder"] = 1 },
            };
            var edit = new JsonObject
            {
                ["before"] = contents,
                ["clearPasses"] = new JsonArray(new JsonObject { ["route"] = "ZZ-MARKER-clear-route", ["rowsBefore"] = 2, ["rowsAfter"] = 1 }),
                ["close"] = new JsonObject { ["button"] = "OK" },
            };
            JsonNode cleaned = scrub.Clean(new JsonObject { ["contents"] = edit })!;
            string text = cleaned.ToJsonString();
            Expect(!text.Contains("ZZ-MARKER", StringComparison.Ordinal), "a diagnostic field reached the golden data: " + text);
            Expect(!text.Contains("gridRead", StringComparison.Ordinal) && !text.Contains("clearPasses", StringComparison.Ordinal), "a diagnostic key reached the golden data");
            Expect(cleaned["contents"]!["before"]!["rowCount"] is not null, "the row count was lost from the golden data");
        });
    }
}
