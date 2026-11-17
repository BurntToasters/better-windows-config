using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `resource-viewers`: Modify on a hardware resource value opens a classic viewer (Resource Lists 116, Resources 1305,
// Requirements List 1501). Each viewer's Display... button opens its child viewer (1203 from 116, 1506 from 1501).
// Samples: HKLM read-only values (Physical Memory .Translated, HARDWARE\DESCRIPTION\System Configuration Data) and the
// seeded malformed OddTypes values (Resource8, Full9, Req10). Nothing is written. Every dialog is closed with CloseDialog.
// Tracked: regedit-resource-viewers.json (ids, states, counts, templates). Text: local/resource-viewers-text.json.
internal static class ResourceViewersCapture
{
    private const int DisplayChildOf116 = 1203;
    private const int DisplayChildOf1501 = 1506;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private sealed record Sample(string Name, string Type, string Key, string Value);

    private static readonly Sample[] Samples =
    {
        new("hklm-resource-list", "REG_RESOURCE_LIST", @"Computer\HKEY_LOCAL_MACHINE\HARDWARE\RESOURCEMAP\System Resources\Physical Memory", ".Translated"),
        new("hklm-full-resource-descriptor", "REG_FULL_RESOURCE_DESCRIPTOR", @"Computer\HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System", "Configuration Data"),
        new("seed-resource-list", "REG_RESOURCE_LIST", UiProbe.LastKeyFor("OddTypes"), "Resource8"),
        new("seed-full-resource-descriptor", "REG_FULL_RESOURCE_DESCRIPTOR", UiProbe.LastKeyFor("OddTypes"), "Full9"),
        new("seed-requirements-list", "REG_RESOURCE_REQUIREMENTS_LIST", UiProbe.LastKeyFor("OddTypes"), "Req10"),
    };

    private static readonly HashSet<string> UnstableSamples = new() { "seed-resource-list", "seed-requirements-list" };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        ResourceDump dump = ResourceDump.Load(paths);
        var viewers = new JsonArray();
        var viewersText = new JsonArray();
        var notMeasured = new JsonArray();

        foreach (Sample sample in Samples)
        {
            // Modify on the malformed Resource8 and Req10 values made classic regedit exit with an access violation in one
            // run and open no viewer in another. Their observed outcome goes to the local text only, and the tracked
            // file records the fixed reason, so two runs compare equal.
            bool unstable = UnstableSamples.Contains(sample.Name);
            try
            {
                Observed observed = OpenViewer(dump, sample);
                viewersText.Add(observed.Text);
                if (!unstable)
                {
                    viewers.Add(observed.Tracked);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"resource-viewers: {sample.Name} not measured: {ex.Message}");
                viewersText.Add(new JsonObject { ["name"] = sample.Name, ["error"] = ex.Message });
                if (!unstable)
                {
                    notMeasured.Add(new JsonObject { ["name"] = sample.Name, ["reason"] = Report.StableReason(ex.Message) });
                }
            }

            if (unstable)
            {
                notMeasured.Add(new JsonObject
                {
                    ["name"] = sample.Name,
                    ["reason"] = "classic regedit exits with an access violation (0xC0000005) or opens no viewer on Modify; the outcome differs between runs",
                });
            }
        }

        Report.WriteJson(Path.Combine(paths.OracleDocs, "regedit-resource-viewers.json"), new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture resource-viewers",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, systemDpi, allowDpi),
            ["note"] = "Modify opens the viewer. Child viewers open from Display... after the list row is selected. Text is in local/resource-viewers-text.json.",
            ["viewers"] = viewers,
            ["notMeasured"] = notMeasured,
        });

        Report.WriteJson(paths.LocalText("resource-viewers-text.json"), new JsonObject { ["viewers"] = viewersText });
    }

    private static Observed OpenViewer(ResourceDump dump, Sample sample)
    {
        using RegeditWindow w = UiProbe.StartAt(sample.Key, waitForList: true);
        int index = IndexOfValue(w, sample.Value);
        w.SelectListRow(index);
        w.FocusList();
        Thread.Sleep(300);

        // Modify is not in the Edit menu for a value. The label double click opens the editor; the value context
        // menu's Modify is the fallback. Both are recorded as the route that worked.
        var tracked = new JsonObject
        {
            ["name"] = sample.Name,
            ["valueType"] = sample.Type,
            ["measured"] = true,
        };
        var text = new JsonObject { ["name"] = sample.Name, ["value"] = sample.Value };
        NativeRect label = w.ListItemRect(index, Msg.LVIR_LABEL);
        int cx = (label.Left + label.Right) / 2;
        int cy = (label.Top + label.Bottom) / 2;
        var before = new HashSet<IntPtr>(w.OwnedDialogs());
        KeyInput.Press(w.Main, w.ListView, Wm.VK_RETURN, default);
        IntPtr dlg = w.TryWaitForNewDialog(before, TimeSpan.FromSeconds(6));
        string route = "enter";
        if (dlg == IntPtr.Zero)
        {
            before = new HashSet<IntPtr>(w.OwnedDialogs());
            w.DoubleClick(w.ListView, cx, cy);
            dlg = w.TryWaitForNewDialog(before, TimeSpan.FromSeconds(6));
            route = "double-click";
        }
        if (dlg == IntPtr.Zero && w.HasExited)
        {
            throw new InvalidOperationException($"regedit exited with code {w.ExitCode} when {sample.Type} was activated.");
        }

        if (dlg == IntPtr.Zero)
        {
            var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
            IntPtr popup;
            try
            {
                popup = w.OpenContextMenu(w.ListView, cx, cy, existing);
            }
            catch (InvalidOperationException)
            {
                // Neither Enter, double click, nor the context menu opened a viewer. The outcome is the measurement.
                tracked["contextMenu"] = "no popup";
                tracked["opened"] = false;
                return new Observed(sample.Name, tracked, text);
            }

            (uint id, bool grayed) = (0u, true);
            try
            {
                (id, grayed) = RegeditWindow.PopupItemByLabel(w.PopupMenuHandle(popup), "&Modify...");
            }
            finally
            {
                w.ClosePopup(popup);
            }

            tracked["modifyEnabled"] = !grayed;
            if (grayed)
            {
                tracked["opened"] = false;
                return new Observed(sample.Name, tracked, text);
            }

            HashSet<IntPtr> topBefore = UiProbe.VisibleTopLevels(w.ProcessId);
            Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(id), IntPtr.Zero);
            dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, topBefore, OpenTimeout, RegeditWindow.DialogClass);
            route = "context-menu";
        }

        tracked["modifyRoute"] = route;
        if (dlg == IntPtr.Zero)
        {
            tracked["opened"] = false;
            return new Observed(sample.Name, tracked, text);
        }

        Thread.Sleep(800);
        try
        {
            tracked["opened"] = true;
            Describe(w, dlg, dump, tracked, text, out List<UiProbe.Ctl> controls);
            var children = new JsonArray();
            var childrenText = new JsonArray();
            foreach (int trigger in new[] { DisplayChildOf116, DisplayChildOf1501 })
            {
                UiProbe.Ctl? button = controls.FirstOrDefault(c => c.Id == trigger && c.Class == "Button");
                if (button is null)
                {
                    continue;
                }

                (JsonObject child, JsonObject childText) = OpenChild(w, dlg, dump, button, controls);
                children.Add(child);
                childrenText.Add(childText);
            }

            if (children.Count > 0)
            {
                tracked["children"] = children;
                text["children"] = childrenText;
            }

            return new Observed(sample.Name, tracked, text);
        }
        finally
        {
            w.CloseDialog(dlg);
        }
    }

    // Records one dialog: template, controls with states, list contents, and edit and static texts (text goes local).
    private static void Describe(RegeditWindow w, IntPtr dlg, ResourceDump dump, JsonObject tracked, JsonObject text, out List<UiProbe.Ctl> controls)
    {
        controls = UiProbe.Controls(w, dlg);
        List<int> ids = UiProbe.SortedControlIds(controls);
        int? template = dump.MatchDialog(ids, UiProbe.TextOf(dlg));
        tracked["dialogTemplate"] = UiProbe.Num(template);
        tracked["controlIds"] = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        tracked["controls"] = UiProbe.ControlsTracked(controls);
        text["title"] = UiProbe.TextOf(dlg);
        text["controls"] = UiProbe.ControlsLocal(controls);

        var lists = new JsonArray();
        var listsText = new JsonArray();
        foreach (UiProbe.Ctl c in controls.Where(c => c.Class is "SysListView32" or "ListBox" or "ColumnListBox"))
        {
            (JsonObject listTracked, JsonObject listLocal) = ReadList(w, c);
            lists.Add(listTracked);
            listsText.Add(listLocal);
        }

        tracked["lists"] = lists;
        text["lists"] = listsText;
    }

    private static (JsonObject Tracked, JsonObject Local) ReadList(RegeditWindow w, UiProbe.Ctl list)
    {
        var rows = new JsonArray();
        var local = new JsonArray();
        int columns = 0;
        if (list.Class == "SysListView32")
        {
            IntPtr header = w.Send(list.Hwnd, Msg.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            columns = header == IntPtr.Zero ? 0 : w.Send(header, Wm.HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            int count = w.Send(list.Hwnd, Msg.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            for (int row = 0; row < count; row++)
            {
                var cells = new List<string>();
                for (int col = 0; col < columns; col++)
                {
                    cells.Add(ListViewText(w, list.Hwnd, row, col));
                }

                rows.Add(new JsonObject { ["cellsNonEmpty"] = cells.Count(c => c.Length > 0) });
                local.Add(new JsonObject { ["cells"] = new JsonArray(cells.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) });
            }
        }
        else
        {
            int count = UiProbe.ListBoxCount(w, list.Hwnd);
            for (int row = 0; row < count; row++)
            {
                string line = UiProbe.ListBoxText(w, list.Hwnd, row);
                rows.Add(new JsonObject { ["nonEmpty"] = line.Length > 0 });
                local.Add(new JsonObject { ["text"] = line });
            }
        }

        // The column list also answers UI Automation. Its ListItem elements give the row count when the list box
        // messages do not report rows. The item names go to the local text only.
        UiaProbe.Tree tree = UiaProbe.Walk(list.Hwnd);
        List<string> items = tree.Elements.Where(e => e.ControlType == "ControlType.ListItem").Select(e => e.Name).ToList();
        var tracked = new JsonObject
        {
            ["id"] = list.Id,
            ["class"] = list.Class,
            ["rowCount"] = rows.Count,
            ["uiaListItemCount"] = items.Count,
            ["columnCount"] = columns,
            ["rows"] = rows,
        };
        var localList = new JsonObject { ["id"] = list.Id, ["rows"] = local };
        localList["uiaItems"] = new JsonArray(items.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        return (tracked, localList);
    }

    // Reads one list view cell with LVM_GETITEMTEXTW in regedit's address space. A cell that is not text reads empty.
    private static string ListViewText(RegeditWindow w, IntPtr list, int row, int column)
    {
        const int chars = 512;
        using var text = new RemoteBuffer(w.ProcessHandle, chars * 2);
        using var lvi = new RemoteBuffer(w.ProcessHandle, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(4, 4), row);
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(8, 4), column);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(24, 8), text.Address.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(32, 4), chars - 1);
        lvi.Write(s);
        w.Send(list, Wm.LVM_GETITEMTEXTW, new IntPtr(row), lvi.Address);
        return Encoding.Unicode.GetString(text.ReadAll()).Split('\0')[0];
    }

    // Clicks the Display... button of the open viewer. The row is selected first when the viewer has a list (Requirements).
    private static (JsonObject Tracked, JsonObject Local) OpenChild(
        RegeditWindow w,
        IntPtr parent,
        ResourceDump dump,
        UiProbe.Ctl button,
        List<UiProbe.Ctl> parentControls)
    {
        // The Display button is enabled or not after a row is selected. Both states are recorded.
        UiProbe.Ctl? list = parentControls.FirstOrDefault(c => c.Class is "SysListView32" or "ListBox" or "ColumnListBox");
        bool enabledBeforeSelect = Win32Ext.IsWindowEnabled(button.Hwnd);
        if (list is not null && list.Class == "SysListView32")
        {
            SelectFirstRow(w, list.Hwnd);
        }
        else if (list is not null)
        {
            w.Send(list.Hwnd, UiProbe.LbSetCurSel, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(200);
        }

        bool enabledAfterSelect = Win32Ext.IsWindowEnabled(button.Hwnd);
        var state = new JsonObject
        {
            ["trigger"] = button.Id,
            ["enabledBeforeSelect"] = enabledBeforeSelect,
            ["enabledAfterSelect"] = enabledAfterSelect,
        };
        if (!enabledAfterSelect)
        {
            state["opened"] = false;
            return (state, new JsonObject { ["trigger"] = button.Id });
        }

        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        Win32.PostMessageW(button.Hwnd, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        IntPtr child = UiProbe.WaitForNewTopLevel(w.ProcessId, before, OpenTimeout, RegeditWindow.DialogClass);
        if (child == IntPtr.Zero)
        {
            state["opened"] = false;
            return (state, new JsonObject { ["trigger"] = button.Id });
        }

        Thread.Sleep(800);
        try
        {
            var tracked = new JsonObject { ["trigger"] = button.Id, ["enabled"] = true, ["opened"] = true };
            tracked["enabledBeforeSelect"] = enabledBeforeSelect;
            tracked["enabledAfterSelect"] = enabledAfterSelect;
            var local = new JsonObject { ["trigger"] = button.Id };
            Describe(w, child, dump, tracked, local, out _);
            return (tracked, local);
        }
        finally
        {
            w.CloseDialog(child);
        }
    }

    private static void SelectFirstRow(RegeditWindow w, IntPtr list)
    {
        using var buf = new RemoteBuffer(w.ProcessHandle, 96);
        byte[] pick = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(0, 4), 0x8);
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(12, 4), Wm.LVIS_SELECTED | Wm.LVIS_FOCUSED);
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(16, 4), Wm.LVIS_SELECTED | Wm.LVIS_FOCUSED);
        buf.Write(pick);
        w.Send(list, Wm.LVM_SETITEMSTATE, IntPtr.Zero, buf.Address);
        Thread.Sleep(200);
    }

    private static int IndexOfValue(RegeditWindow w, string name)
    {
        w.WaitForListPopulated(TimeSpan.FromSeconds(25));
        int index = w.ListNames().IndexOf(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"value '{name}' is not in the list.");
        }

        return index;
    }
}
