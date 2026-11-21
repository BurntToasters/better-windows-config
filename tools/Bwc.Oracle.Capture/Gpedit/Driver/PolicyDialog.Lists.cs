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

    // The grid as read from UI Automation: the classification of GridRead and the elements it points at (children and, per child, its cells).
    private sealed record GridLive(GridShape Shape, List<AutomationElement> Children, List<List<AutomationElement>> Cells, List<GridChildInfo> Infos);

    private static string TypeName(AutomationElement element) =>
        element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);

    private static GridCellInfo CellInfo(AutomationElement cell)
    {
        string? value = cell.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) ? ((ValuePattern)pattern).Current.Value ?? string.Empty : null;
        return new GridCellInfo(TypeName(cell), value, ProbeUia.NameOf(cell), cell.TryGetCurrentPattern(GridItemPattern.Pattern, out _));
    }

    private static GridLive ReadLive(AutomationElement grid)
    {
        var children = new List<AutomationElement>();
        var cells = new List<List<AutomationElement>>();
        var infos = new List<GridChildInfo>();
        foreach (AutomationElement child in ProbeUia.Find(grid, TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                string type = TypeName(child);
                // A scroll bar is never a row, so its buttons are not read.
                List<AutomationElement> own = type == "ScrollBar" ? new List<AutomationElement>() : ProbeUia.Find(child, TreeScope.Children, Condition.TrueCondition);
                infos.Add(new GridChildInfo(type, ProbeUia.NameOf(child), own.Select(CellInfo).ToList()));
                children.Add(child);
                cells.Add(own);
            }
            catch (ElementNotAvailableException)
            {
                // The row went away while the grid was read (a delete is under way): it is not a row any more.
            }
        }

        return new GridLive(GridRead.Classify(infos), children, cells, infos);
    }

    // The rows of the list (the new row is not one), each as the text of its data cells, "(null)" shown as empty.
    private static List<List<string>> GridCells(AutomationElement grid) =>
        ReadLive(grid).Shape.Rows.Select(r => r.Texts.Select(GridRead.Shown).ToList()).ToList();

    private static int FilledRows(GridShape shape) => shape.Rows.Count(r => !r.IsEmpty);

    // How the grid was classified, for the result file (a diagnostic, never part of a golden).
    private static JsonObject GridEvidence(GridLive live)
    {
        GridShape shape = live.Shape;
        return new JsonObject
        {
            ["children"] = new JsonArray(live.Infos.Select(i => (JsonNode?)JsonValue.Create($"{i.ControlType}: {i.Name} ({i.Cells.Count} cells)")).ToArray()),
            ["headerRows"] = new JsonArray(shape.HeaderChildren.Select(i => (JsonNode?)JsonValue.Create(live.Infos[i].Name)).ToArray()),
            ["placeholder"] = shape.Placeholder is { } p ? shape.All[p].Name : null,
            ["rows"] = new JsonArray(shape.All.Select(r => (JsonNode?)new JsonObject
            {
                ["name"] = r.Name,
                ["child"] = r.ChildIndex,
                ["rowHeaderCell"] = r.RowHeader,
                ["cells"] = new JsonArray(r.CellIndexes.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["texts"] = new JsonArray(r.Texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            }).ToArray()),
        };
    }

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
        GridLive live = ReadLive(grid);
        List<List<string>> cells = live.Shape.Rows.Select(r => r.Texts.Select(GridRead.Shown).ToList()).ToList();
        var rows = new JsonArray(cells.Select(r => (JsonNode?)new JsonArray(r.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray())).ToArray());
        var node = new JsonObject
        {
            ["title"] = ProbeWindows.TitleOf(dialog),
            ["label"] = GpeditDriver.ById(root, "label") is { } label ? ProbeUia.NameOf(label) : null,
            ["rowCount"] = cells.Count,
            ["filledRows"] = FilledRows(live.Shape),
            ["rows"] = rows,
            ["gridRead"] = GridEvidence(live),
        };
        if (live.Shape.All.Count == 0)
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

    // The keyboard route to a data row: Ctrl+Home goes to the first data cell, then one Down press per data row. The new row, when it exists, is
    // the data row after the last row of the list.
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

    // Writes one cell with the keyboard (Ctrl+Home, Down and Right to the cell, F2, select all, type, Enter). The row is the data row index
    // and the column the data cell index, both as GridRead counts them. The caller reads the grid back.
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

    // The same data cell WriteCell wrote, "(null)" read as empty. The row may be the new row of the grid, which holds the text just written.
    private string? ReadCell(IntPtr dialog, int row, int column)
    {
        GridShape shape = ReadLive(Grid(dialog)).Shape;
        return row < shape.All.Count && column < shape.All[row].Texts.Count ? GridRead.Shown(shape.All[row].Texts[column]) : null;
    }

    private const int MaxClearPasses = 200;
    private const int MaxStalledPasses = 3;

    // Says why a cell editor is open, or null when none is. An editor is a child of the grid of control type Edit, or the focused element when
    // it is an Edit with its own window handle in mmc (a cell is an Edit too, but it has no window handle).
    private string? OpenEditor(AutomationElement grid)
    {
        foreach (AutomationElement child in ProbeUia.Find(grid, TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (child.Current.ControlType == ControlType.Edit)
                {
                    return "an Edit child of the grid";
                }
            }
            catch (ElementNotAvailableException)
            {
                // Gone while looked at: not an editor.
            }
        }

        try
        {
            AutomationElement focused = AutomationElement.FocusedElement;
            if (focused.Current.ControlType == ControlType.Edit && focused.Current.NativeWindowHandle != 0 && focused.Current.ProcessId == driver.Pid)
            {
                return "the focused element is an Edit with a window handle";
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            // No focused element could be read: no evidence of an editor.
        }

        return null;
    }

    // Before a key press of a clear: no cell may be in edit mode, or the key would reach the editor. Escape leaves the edit mode. It is pressed only
    // when an editor was seen, because Escape in the dialog would cancel it. What was done goes into the pass log.
    private void CloseEditorBeforeKeys(IntPtr dialog, AutomationElement grid, JsonObject entry)
    {
        string? editor = OpenEditor(grid);
        if (editor is null)
        {
            return;
        }

        entry["editorSeen"] = editor;
        for (int attempt = 0; attempt < 2 && editor is not null; attempt++)
        {
            DriverInput.RequireForeground(dialog, driver.Pid);
            DriverInput.Key(ProbeInput.VkEscape);
            Thread.Sleep(300);
            editor = OpenEditor(grid);
        }

        entry["editorEscaped"] = true;
        if (editor is not null)
        {
            throw new DriverException($"a cell editor stays open after Escape ({editor}); no key was sent into the grid", evidence: entry);
        }
    }

    // What UI Automation says about the selection of a row: the row, or its header, when one offers SelectionItemPattern. A Windows Forms grid
    // may offer none, then the answer is unknown.
    private static string SelectionOf(params AutomationElement?[] candidates)
    {
        foreach (AutomationElement? element in candidates)
        {
            if (element is not null && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern))
            {
                return ((SelectionItemPattern)pattern).Current.IsSelected ? "selected" : "not selected";
            }
        }

        return "unknown";
    }

    // Selects one data row to delete: first by invoking its row header (which selects the whole row), then the keyboard route when the row has
    // no header, the invoke did not work, or UI Automation says the row is not selected. Returns the route used.
    private string SelectRowForDelete(IntPtr dialog, AutomationElement grid, GridLive live, int target, JsonObject entry)
    {
        GridRowRead row = live.Shape.All[target];
        int slot = row.ChildIndex;
        AutomationElement? rowElement = slot >= 0 && slot < live.Children.Count ? live.Children[slot] : null;
        AutomationElement? header = row.RowHeader is { } h && slot >= 0 && slot < live.Cells.Count && h < live.Cells[slot].Count ? live.Cells[slot][h] : null;
        if (header is null)
        {
            entry["headerInvoke"] = "the row has no row header";
        }
        else
        {
            bool invoked;
            try
            {
                invoked = ProbeUia.Invoke(header);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                invoked = false;
                entry["headerInvokeError"] = ex.Message;
            }

            Thread.Sleep(200);
            string selection = SelectionOf(rowElement, header);
            entry["selection"] = selection;
            if (!invoked)
            {
                entry["headerInvoke"] = "no Invoke pattern, or it failed";
            }
            else if (selection == "not selected")
            {
                entry["headerInvoke"] = "invoked, but the row is not selected";
            }
            else
            {
                entry["headerInvoke"] = "invoked";
                DriverInput.RequireForeground(dialog, driver.Pid);
                ProbeUia.Focus(grid);
                Thread.Sleep(150);
                return "rowHeader";
            }
        }

        // The old route: the row is made the current row with the keyboard and selected with Shift+Space.
        SelectGridRow(dialog, grid, target);
        DriverInput.Chord(new[] { DriverInput.VkShift }, DriverInput.VkSpace);
        return "keyboard";
    }

    private static JsonArray RowValues(GridShape shape) =>
        new(shape.Rows.Select(r => (JsonNode?)new JsonArray(r.Texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray())).ToArray());

    // Deletes the data rows one by one, the last first. Returns the log of the passes. Stops with the log and the grid tree when
    // MaxStalledPasses passes in a row did not lower the number of rows, and never runs more than MaxClearPasses. No key sent here is text.
    private JsonArray ClearGrid(IntPtr dialog)
    {
        var passes = new JsonArray();
        int stalled = 0;
        for (int pass = 0; pass < MaxClearPasses; pass++)
        {
            AutomationElement grid = Grid(dialog);
            GridLive before = ReadLive(grid);
            int count = before.Shape.Rows.Count;
            if (count == 0)
            {
                return passes;
            }

            int target = count - 1;
            var entry = new JsonObject
            {
                ["pass"] = pass,
                ["row"] = target,
                ["rowName"] = before.Shape.Rows[target].Name,
                ["rowsBefore"] = count,
                ["filledBefore"] = FilledRows(before.Shape),
                ["beforeValues"] = RowValues(before.Shape),
            };
            passes.Add(entry);
            CloseEditorBeforeKeys(dialog, grid, entry);
            entry["route"] = SelectRowForDelete(dialog, grid, before, target, entry);
            DriverInput.RequireForeground(dialog, driver.Pid);
            DriverInput.Key(DriverInput.VkDelete);
            Thread.Sleep(300);

            AutomationElement gridAfter = Grid(dialog);
            GridLive after = ReadLive(gridAfter);
            int left = after.Shape.Rows.Count;
            entry["rowsAfter"] = left;
            entry["filledAfter"] = FilledRows(after.Shape);
            entry["afterValues"] = RowValues(after.Shape);
            entry["editorOpenAfter"] = OpenEditor(gridAfter);
            stalled = left < count ? 0 : stalled + 1;
            if (stalled >= MaxStalledPasses)
            {
                throw new DriverException(
                    $"{MaxStalledPasses} delete passes in a row did not remove a row ({left} rows left)",
                    evidence: new JsonObject { ["passes"] = passes, ["gridRead"] = GridEvidence(after), ["tree"] = ProbeUia.Tree(gridAfter, 4, 300, rawView: false) });
            }
        }

        throw new DriverException(
            $"the grid still has rows after {MaxClearPasses} delete passes",
            evidence: new JsonObject { ["passes"] = passes, ["tree"] = ProbeUia.Tree(Grid(dialog), 4, 300, rawView: false) });
    }

    private static JsonArray Copies(JsonArray boxes) => new(boxes.Select(b => b?.DeepClone()).ToArray());

    private JsonArray OpenWindows() => new(driver.Dialogs().Select(w => (JsonNode?)new JsonObject
    {
        ["title"] = w.Title,
        ["class"] = w.Class,
        ["kind"] = driver.Classify(w).ToString(),
    }).ToArray());

    // OK of Show Contents brought a message box (already captured and answered in `close`). Reads whether the dialog is still open, which
    // takes up to 1.5 s because a dialog that classic accepts after the box may need a moment to go. When it is still open the list was
    // refused: the dialog is cancelled and the read says it closed. The fields go to the set entry (ListRefusal). A dialog that does not close
    // after Cancel is a driver failure, with the result so far and the open windows as evidence. Failure mode 38 of the driver document.
    private void RecordMessageBoxOnOk(WinInfo dialog, JsonObject close, JsonObject result)
    {
        JsonArray boxes = close["messageBoxes"]!.AsArray();
        var known = driver.Dialogs().Select(w => w.Hwnd).ToHashSet();
        DateTime end = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        bool open;
        while (true)
        {
            driver.Context.ThrowIfCancelled();
            driver.ThrowIfDead();
            foreach (JsonNode? more in Copies(driver.AnswerNewMessageBoxes(known, null)))
            {
                boxes.Add(more);
            }

            open = GpeditDriver.IsOpen(dialog.Hwnd);
            if (!open || DateTime.UtcNow >= end)
            {
                break;
            }

            Thread.Sleep(100);
        }

        bool? cancelled = null;
        string? route = null;
        var afterCancel = new JsonArray();
        if (ListRefusal.IsRefused(boxes.Count, open))
        {
            var knownBeforeCancel = driver.Dialogs().Select(w => w.Hwnd).ToHashSet();
            route = driver.CancelWindow(InfoOf(dialog.Hwnd));
            foreach (JsonNode? more in Copies(driver.AnswerNewMessageBoxes(knownBeforeCancel, null)))
            {
                afterCancel.Add(more);
            }

            cancelled = !GpeditDriver.IsOpen(dialog.Hwnd);
        }

        ListRefusal.Apply(result, boxes, open, cancelled, route, afterCancel);
        if (cancelled == false)
        {
            result["openWindows"] = OpenWindows();
            throw new DriverException("the Show Contents dialog stayed open after the message box on OK and Cancel", evidence: result);
        }
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
                result["clearPasses"] = ClearGrid(dialog.Hwnd);
                if (ReadLive(Grid(dialog.Hwnd)).Shape.Rows.Count > 0)
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
                // Classic can refuse a list (an empty one, for one) with a message box. That is an observed outcome, not a driver failure.
                RecordMessageBoxOnOk(dialog, close, result);
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
