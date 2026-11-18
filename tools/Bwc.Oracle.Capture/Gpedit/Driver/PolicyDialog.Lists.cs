using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// List elements: a Show... button opens the modal Show Contents dialog with a grid. Reading is Cancel afterwards; editing is cell by
// cell with read-back, then OK. Failure mode 10 of the driver document. The grid structure is not known from the static dump, so every
// failure carries the grid's UI Automation tree.
internal sealed partial class PolicyDialog
{
    private WinInfo OpenShowContents(OptionGroup group)
    {
        var known = driver.Dialogs().Select(w => w.Hwnd).ToHashSet();
        AutomationElement button = AutomationElement.FromHandle(group.Control);
        if (!ProbeUia.Invoke(button))
        {
            DriverNative.Click(group.Control);
        }

        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < end)
        {
            driver.Context.ThrowIfCancelled();
            driver.ThrowIfDead();
            foreach (WinInfo window in driver.NewDialogs(known))
            {
                DialogKind kind = driver.Classify(window);
                if (kind == DialogKind.ShowContents)
                {
                    Thread.Sleep(400);
                    return window;
                }

                if (kind == DialogKind.MessageBox)
                {
                    throw new DriverException($"opening the list of '{group.Label}' showed a message box", evidence: driver.DismissMessageBox(window, null));
                }
            }

            Thread.Sleep(150);
        }

        throw new DriverException($"no Show Contents dialog opened for '{group.Label}'");
    }

    private static List<AutomationElement> GridRowElements(AutomationElement grid)
    {
        List<AutomationElement> children = ProbeUia.Find(grid, TreeScope.Children, Condition.TrueCondition);
        List<AutomationElement> rows = children.Where(c => c.Current.ControlType == ControlType.DataItem).ToList();
        if (rows.Count == 0)
        {
            rows = children.Where(c => c.Current.ControlType != ControlType.Header && c.Current.ControlType != ControlType.ScrollBar
                && ProbeUia.Find(c, TreeScope.Children, Condition.TrueCondition).Count > 0).ToList();
        }

        return rows;
    }

    private static string CellValue(AutomationElement cell)
    {
        if (cell.TryGetCurrentPattern(ValuePattern.Pattern, out object? value))
        {
            return ((ValuePattern)value).Current.Value ?? string.Empty;
        }

        return ProbeUia.NameOf(cell);
    }

    private static List<List<string>> GridCells(AutomationElement grid) =>
        GridRowElements(grid).Select(r => ProbeUia.Find(r, TreeScope.Children, Condition.TrueCondition).Select(CellValue).ToList()).ToList();

    private static bool IsEmptyRow(List<string> cells) => cells.All(c => string.IsNullOrWhiteSpace(c));

    private AutomationElement Grid(IntPtr dialog)
    {
        AutomationElement root = AutomationElement.FromHandle(dialog);
        return GpeditDriver.ById(root, "dataGrid")
            ?? throw new DriverException("no 'dataGrid' in the Show Contents dialog", evidence: ProbeUia.Tree(root, 6, 200, rawView: false));
    }

    private JsonObject ReadGrid(IntPtr dialog)
    {
        AutomationElement root = AutomationElement.FromHandle(dialog);
        AutomationElement grid = Grid(dialog);
        List<List<string>> cells = GridCells(grid);
        var rows = new JsonArray(cells.Select(r => (JsonNode?)new JsonArray(r.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray())).ToArray());
        int filled = cells.Count(r => !IsEmptyRow(r));
        var node = new JsonObject
        {
            ["title"] = ProbeWindows.TitleOf(dialog),
            ["label"] = GpeditDriver.ById(root, "label") is { } label ? ProbeUia.NameOf(label) : null,
            ["rowCount"] = cells.Count,
            ["filledRows"] = filled,
            ["rows"] = rows,
        };
        if (cells.Count == 0)
        {
            node["tree"] = ProbeUia.Tree(grid, 4, 300, rawView: false);
        }

        return node;
    }

    private JsonObject CloseShowContents(WinInfo dialog, bool ok, string? answer)
    {
        var result = new JsonObject { ["button"] = ok ? "OK" : "Cancel" };
        if (!ok)
        {
            result["closedBy"] = driver.CancelWindow(dialog);
            return result;
        }

        var known = driver.Dialogs().Select(w => w.Hwnd).ToHashSet();
        AutomationElement root = AutomationElement.FromHandle(dialog.Hwnd);
        AutomationElement button = GpeditDriver.ById(root, "buttonOK") ?? throw new DriverException("no OK button in the Show Contents dialog");
        ProbeUia.Focus(button);
        Thread.Sleep(300);
        if (!ProbeUia.Invoke(button))
        {
            ProbeUia.Click(button, doubleClick: false);
        }

        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < end)
        {
            JsonArray boxes = driver.AnswerNewMessageBoxes(known, answer);
            if (boxes.Count > 0)
            {
                result["messageBoxes"] = boxes;
                result["blockedByMessageBox"] = true;
                return result;
            }

            if (!GpeditDriver.IsOpen(dialog.Hwnd))
            {
                result["closed"] = true;
                return result;
            }

            Thread.Sleep(100);
        }

        throw new DriverException("the Show Contents dialog stayed open 10 seconds after OK");
    }

    private WinInfo InfoOf(IntPtr hwnd) =>
        ProbeWindows.TopLevel(driver.Pid).FirstOrDefault(w => w.Hwnd == hwnd) ?? throw new DriverException($"window 0x{hwnd:X} is gone");

    // Opens Show Contents, reads the grid and cancels.
    private JsonObject ReadListContents(OptionGroup group)
    {
        WinInfo dialog = OpenShowContents(group);
        try
        {
            return ReadGrid(dialog.Hwnd);
        }
        finally
        {
            if (GpeditDriver.IsOpen(dialog.Hwnd))
            {
                driver.CancelWindow(InfoOf(dialog.Hwnd));
            }
        }
    }

    private void SelectGridRow(IntPtr dialog, AutomationElement grid, int row)
    {
        DriverInput.RequireForeground(dialog, driver.Pid);
        ProbeUia.Focus(grid);
        Thread.Sleep(150);
        DriverInput.Chord(new[] { DriverInput.VkControl }, DriverInput.VkHome);
        for (int i = 0; i < row; i++)
        {
            DriverInput.Key(DriverInput.VkDown);
        }
    }

    // Writes one cell with the keyboard (Ctrl+Home, Down and Right to the cell, F2, select all, type, Enter). The caller reads the grid back.
    private string WriteCell(IntPtr dialog, int row, int column, string text)
    {
        AutomationElement grid = Grid(dialog);
        SelectGridRow(dialog, grid, row);
        for (int i = 0; i < column; i++)
        {
            DriverInput.Key(DriverInput.VkRight);
        }

        DriverInput.Key(DriverInput.VkF2);
        DriverInput.Chord(new[] { DriverInput.VkControl }, DriverInput.VkA);
        DriverInput.TypeText(text);
        DriverInput.Key(ProbeInput.VkReturn);
        Thread.Sleep(300);
        return "keyboard";
    }

    private string? ReadCell(IntPtr dialog, int row, int column)
    {
        List<List<string>> cells = GridCells(Grid(dialog));
        return row < cells.Count && column < cells[row].Count ? cells[row][column] : null;
    }

    private void ClearGrid(IntPtr dialog)
    {
        for (int pass = 0; pass < 200; pass++)
        {
            List<List<string>> cells = GridCells(Grid(dialog));
            int last = cells.FindLastIndex(r => !IsEmptyRow(r));
            if (last < 0)
            {
                return;
            }

            AutomationElement grid = Grid(dialog);
            SelectGridRow(dialog, grid, last);
            DriverInput.Chord(new[] { DriverInput.VkShift }, DriverInput.VkSpace);
            DriverInput.Key(DriverInput.VkDelete);
            Thread.Sleep(300);
        }

        throw new DriverException("the grid still has rows after 200 delete passes", evidence: ProbeUia.Tree(Grid(dialog), 4, 300, rawView: false));
    }

    private JsonObject EditList(OptionGroup group, OptionSpec spec)
    {
        WinInfo dialog = OpenShowContents(group);
        var result = new JsonObject();
        try
        {
            result["before"] = ReadGrid(dialog.Hwnd);
            if (spec.ClearRows is true)
            {
                ClearGrid(dialog.Hwnd);
                List<List<string>> afterClear = GridCells(Grid(dialog.Hwnd));
                if (afterClear.Any(r => !IsEmptyRow(r)))
                {
                    throw new DriverException("rows remain after clearing", evidence: ReadGrid(dialog.Hwnd));
                }
            }

            var methods = new JsonArray();
            List<List<string>> wanted = spec.Rows ?? new List<List<string>>();
            for (int r = 0; r < wanted.Count; r++)
            {
                for (int c = 0; c < wanted[r].Count; c++)
                {
                    string via = WriteCell(dialog.Hwnd, r, c, wanted[r][c]);
                    string? read = ReadCell(dialog.Hwnd, r, c);
                    methods.Add(new JsonObject { ["row"] = r, ["column"] = c, ["via"] = via, ["requested"] = wanted[r][c], ["read"] = read });
                    if (!string.Equals(read, wanted[r][c], StringComparison.Ordinal))
                    {
                        throw new DriverException($"grid cell ({r},{c}) reads '{read}', not '{wanted[r][c]}'", evidence: ReadGrid(dialog.Hwnd));
                    }
                }
            }

            result["cells"] = methods;
            result["afterEdit"] = ReadGrid(dialog.Hwnd);
            JsonObject close = CloseShowContents(dialog, ok: true, answer: null);
            result["close"] = close;
            if (close["blockedByMessageBox"]?.GetValue<bool>() == true)
            {
                throw new DriverException("the Show Contents dialog was blocked by a message box on OK", evidence: close);
            }

            return result;
        }
        finally
        {
            if (GpeditDriver.IsOpen(dialog.Hwnd))
            {
                driver.CancelWindow(InfoOf(dialog.Hwnd));
            }
        }
    }
}
