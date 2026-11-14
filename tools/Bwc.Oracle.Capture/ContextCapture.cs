using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `context`: records the context menus of the tree, the value list and the text edit controls. A text control's menu is
// opened by each method in turn, and every attempt is recorded (method, popup found, resource template matched, items):
//   context-message-at-point  WM_CONTEXTMENU with the screen point of the target (regedit may ignore the point)
//   right-button-at-point     posted right button down and up at the target's client point
// A value's menu is opened by Shift+F10 with the value selected and the list focused (see ValueMethods). Each popup is matched to a resource menu by its leaf command ids, so a menu is named by what it contains. Nothing is
// chosen from a menu: every popup is closed with Escape and every dialog with IDCANCEL. Tracked: regedit-context.json.
// Text: local/context-text.json.
internal static class ContextCapture
{
    private const int ValueMenuTemplate = 105;
    private const int ListMenuTemplate = 106;
    private const int TextMenuTemplate = 108;
    private const int ModifyCommand = 912;
    private const int ModifyBinaryCommand = 917;
    private const uint VkF10 = 0x79;
    private const string PointMethod = "context-message-at-point";
    private const string ButtonMethod = "right-button-at-point";
    private const string ShiftF10Method = "shift-f10";
    // Only Shift+F10 for values. The point and right-button methods depend on the real cursor position (regedit reads it),
    // so their result changed between runs. They were tried in earlier runs and opened the list background menu.
    private static readonly string[] ValueMethods = { ShiftF10Method };
    private static readonly string[] TextMethods = { PointMethod, ButtonMethod };
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private sealed record ValueSpec(string Name, string Key, string Value, string Type);

    private static readonly ValueSpec[] Values =
    {
        new("value-reg-sz", "Strings", "Ascii", "REG_SZ"),
        new("value-reg-expand-sz", "Strings", "ExpandSz", "REG_EXPAND_SZ"),
        new("value-reg-binary", "Binary", "OneByte", "REG_BINARY"),
        new("value-reg-dword", "Numbers", "Dword1", "REG_DWORD"),
        new("value-reg-qword", "Numbers", "Qword0", "REG_QWORD"),
        new("value-reg-multi-sz", "MultiSz", "Several", "REG_MULTI_SZ"),
        new("value-reg-none", "Binary", "NoneData", "REG_NONE"),
        new("value-reg-resource-list", "OddTypes", "Resource8", "REG_RESOURCE_LIST"),
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        ResourceDump dump = ResourceDump.Load(paths);
        var menus = new List<Observed>();
        var notMeasured = new JsonArray();

        Attempt("tree-computer-root", menus, notMeasured, () => TreeContext(dump, "tree-computer-root", "Computer"));
        Attempt("tree-hive-hkcu", menus, notMeasured, () => TreeContext(dump, "tree-hive-hkcu", @"Computer\HKEY_CURRENT_USER"));
        Attempt("tree-key", menus, notMeasured, () => TreeContext(dump, "tree-key", UiProbe.LastKeyFor("Mixed")));
        foreach (ValueSpec value in Values)
        {
            Attempt(value.Name, menus, notMeasured, () => ValueContext(dump, value));
        }

        Attempt("list-background", menus, notMeasured, () => ListBackground(dump));
        Attempt("text-edit-address-bar", menus, notMeasured, () => TextEdit(dump, "text-edit-address-bar", UiProbe.LastKeyFor("Mixed"), null, null, false));
        Attempt("text-edit-string-data", menus, notMeasured,
            () => TextEdit(dump, "text-edit-string-data", UiProbe.LastKeyFor("Strings"), "Ascii", ModifyCommand, false));
        Attempt("text-edit-binary-hex", menus, notMeasured,
            () => TextEdit(dump, "text-edit-binary-hex", UiProbe.LastKeyFor("Binary"), "OneByte", ModifyBinaryCommand, false));
        Attempt("text-edit-find-what", menus, notMeasured, () => TextEdit(dump, "text-edit-find-what", UiProbe.LastKeyFor("Mixed"), null, null, true));

        Report.WriteJson(paths.ContextJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture context",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, systemDpi, allowDpi),
            ["note"] = "Each menu is matched to a resource menu by its leaf command ids. A value menu lists each method tried. Text is in local/context-text.json.",
            ["menus"] = new JsonArray(menus.Select(m => (JsonNode)m.Tracked).ToArray()),
            ["notMeasured"] = notMeasured,
        });

        Report.WriteJson(paths.LocalText("context-text.json"), new JsonObject
        {
            ["menus"] = new JsonArray(menus.Select(m => (JsonNode)m.Text).ToArray()),
        });
    }

    private static void Attempt(string name, List<Observed> menus, JsonArray notMeasured, Func<Observed> body)
    {
        try
        {
            menus.Add(body());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"context: {name} not measured: {ex.Message}");
            notMeasured.Add(new JsonObject { ["name"] = name, ["reason"] = Report.StableReason(ex.Message) });
        }
    }

    private static Observed TreeContext(ResourceDump dump, string name, string lastKey)
    {
        using RegeditWindow w = UiProbe.StartAt(lastKey, waitForList: false);
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
        IntPtr popup = UiProbe.OpenTreeContext(w, existing);
        try
        {
            List<UiProbe.MenuRow> rows = UiProbe.MenuRows(w.PopupMenuHandle(popup), 0);
            int? template = dump.MatchMenu(UiProbe.LeafIds(rows));
            Observed observed = Build(name, "tree", rows, template, null);
            observed.Text["caretPath"] = w.TreePathOf(w.TreeCaret());
            return observed;
        }
        finally
        {
            w.ClosePopup(popup);
        }
    }

    private static Observed ValueContext(ResourceDump dump, ValueSpec value)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor(value.Key), waitForList: true);
        int index = IndexOf(w, value.Value);
        w.SelectListRow(index);
        w.FocusList();
        Thread.Sleep(300);
        NativeRect label = w.ListItemRect(index, Msg.LVIR_LABEL);

        var attempts = new JsonArray();
        List<UiProbe.MenuRow>? chosen = null;
        string? chosenMethod = null;
        foreach (string method in ValueMethods)
        {
            w.SelectListRow(index);
            w.FocusList();
            Thread.Sleep(200);
            IntPtr popup = OpenBy(w, method, w.ListView, label);
            if (popup == IntPtr.Zero)
            {
                attempts.Add(new JsonObject { ["method"] = method, ["popupOpened"] = false });
                continue;
            }

            try
            {
                List<UiProbe.MenuRow> rows = UiProbe.MenuRows(w.PopupMenuHandle(popup), 0);
                int? template = dump.MatchMenu(UiProbe.LeafIds(rows));
                attempts.Add(new JsonObject
                {
                    ["method"] = method,
                    ["popupOpened"] = true,
                    ["menuTemplate"] = UiProbe.Num(template),
                    ["items"] = UiProbe.MenuTracked(rows),
                });
                if (template == ValueMenuTemplate)
                {
                    chosen = rows;
                    chosenMethod = method;
                }
            }
            finally
            {
                w.ClosePopup(popup);
            }

            if (chosen is not null)
            {
                break;
            }
        }

        if (chosen is null)
        {
            throw new InvalidOperationException($"no method opened the value menu for '{value.Value}' (RT_MENU 105); attempts: {attempts.ToJsonString()}");
        }

        Observed observed = Build(value.Name, "value", chosen, ValueMenuTemplate, value.Type);
        observed.Tracked["method"] = chosenMethod;
        observed.Tracked["attempts"] = attempts;
        return observed;
    }

    private static Observed ListBackground(ResourceDump dump)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Mixed"), waitForList: true);
        w.ClearListSelection();
        w.FocusList();
        int rows = w.ListCount();
        NativeRect last = w.ListItemRect(rows - 1);
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
        IntPtr popup = w.OpenContextMenu(w.ListView, 40, last.Bottom + 40, existing);
        try
        {
            List<UiProbe.MenuRow> items = UiProbe.MenuRows(w.PopupMenuHandle(popup), 0);
            int? template = dump.MatchMenu(UiProbe.LeafIds(items));
            if (template != ListMenuTemplate)
            {
                throw new InvalidOperationException($"the list background menu matched RT_MENU {template?.ToString() ?? "none"}, expected {ListMenuTemplate}.");
            }

            return Build("list-background", "list", items, template, null);
        }
        finally
        {
            w.ClosePopup(popup);
        }
    }

    // Right-clicks a text control by each method. The control is the address bar, the data box of Edit String, the hex
    // area of Edit Binary Value, or the Find what box. Every attempt is recorded, including a system edit menu.
    private static Observed TextEdit(ResourceDump dump, string name, string lastKey, string? valueName, int? command, bool find)
    {
        using RegeditWindow w = UiProbe.StartAt(lastKey, waitForList: valueName is not null);
        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        IntPtr dlg = IntPtr.Zero;
        IntPtr edit;
        if (valueName is not null)
        {
            int index = IndexOf(w, valueName);
            w.SelectListRow(index);
            w.FocusList();
            Thread.Sleep(300);
            Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(command!.Value), IntPtr.Zero);
            dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, before, OpenTimeout, RegeditWindow.DialogClass);
            if (dlg == IntPtr.Zero)
            {
                throw new InvalidOperationException($"command {command} opened no dialog for '{valueName}'.");
            }

            Thread.Sleep(800);
            edit = UiProbe.LargestEditable(w, dlg);
        }
        else if (find)
        {
            w.PostMenuCommand("&Edit", "&Find...");
            dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, before, OpenTimeout, RegeditWindow.DialogClass);
            if (dlg == IntPtr.Zero)
            {
                throw new InvalidOperationException("Edit > Find opened no dialog.");
            }

            Thread.Sleep(800);
            edit = UiProbe.LargestEditable(w, dlg);
        }
        else
        {
            edit = w.AddressBar;
        }

        try
        {
            if (edit == IntPtr.Zero)
            {
                throw new InvalidOperationException("no text control found for the context menu.");
            }

            Win32.GetClientRect(edit, out NativeRect client);
            var attempts = new JsonArray();
            var attemptText = new JsonArray();
            bool matches108 = false;
            foreach (string method in TextMethods)
            {
                IntPtr popup = OpenBy(w, method, edit, client);
                if (popup == IntPtr.Zero)
                {
                    attempts.Add(new JsonObject { ["method"] = method, ["popupOpened"] = false });
                    continue;
                }

                try
                {
                    List<UiProbe.MenuRow> rows = UiProbe.MenuRows(w.PopupMenuHandle(popup), 0);
                    List<int> leaves = UiProbe.LeafIds(rows);
                    int? template = dump.MatchMenu(leaves);
                    bool system = leaves.Contains(768) && leaves.Contains(772);
                    matches108 |= template == TextMenuTemplate;
                    attempts.Add(new JsonObject
                    {
                        ["method"] = method,
                        ["popupOpened"] = true,
                        ["menuTemplate"] = UiProbe.Num(template),
                        ["systemEditMenu"] = system,
                        ["items"] = UiProbe.MenuTracked(rows),
                    });
                    attemptText.Add(new JsonObject { ["method"] = method, ["items"] = UiProbe.MenuLocal(rows) });
                }
                finally
                {
                    w.ClosePopup(popup);
                }
            }

            var tracked = new JsonObject
            {
                ["name"] = name,
                ["kind"] = "text-edit",
                ["measured"] = true,
                ["targetClass"] = RegeditWindow.ClassOf(edit),
                ["targetId"] = Win32.GetDlgCtrlID(edit),
                ["targetClientWidth"] = client.Width,
                ["targetClientHeight"] = client.Height,
                ["matchesRtMenu108"] = matches108,
                ["attempts"] = attempts,
            };
            if (dlg != IntPtr.Zero)
            {
                tracked["dialogTemplate"] = UiProbe.Num(dump.MatchDialog(UiProbe.SortedControlIds(UiProbe.Controls(w, dlg)), UiProbe.TextOf(dlg)));
            }

            var text = new JsonObject { ["name"] = name, ["attempts"] = attemptText };
            return new Observed(name, tracked, text);
        }
        finally
        {
            if (dlg != IntPtr.Zero)
            {
                UiProbe.CloseWindow(w, dlg);
            }
        }
    }

    // Opens a popup on control by one method. Returns the popup, or IntPtr.Zero when none appeared.
    private static IntPtr OpenBy(RegeditWindow w, string method, IntPtr control, NativeRect clientTarget)
    {
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
        int cx = (clientTarget.Left + clientTarget.Right) / 2;
        int cy = (clientTarget.Top + clientTarget.Bottom) / 2;
        switch (method)
        {
            case PointMethod:
            {
                (int sx, int sy) = UiProbe.CentreOnScreen(control, clientTarget);
                UiProbe.PostContextMenuAt(control, sx, sy);
                break;
            }

            case ButtonMethod:
                w.RightClick(control, cx, cy);
                break;

            case ShiftF10Method:
                KeyInput.Press(w.Main, control, (int)VkF10, new KeyInput.Mods(Shift: true));
                break;

            default:
                throw new ArgumentException($"unknown method {method}");
        }

        IntPtr popup = w.TryWaitForNewPopup(existing, TimeSpan.FromSeconds(4));
        Thread.Sleep(500);
        return popup;
    }

    // Builds the tracked and local observation of one menu. expectedValueType adds the value's type and Modify flags.
    private static Observed Build(string name, string kind, List<UiProbe.MenuRow> rows, int? template, string? valueType)
    {
        var tracked = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["measured"] = true,
            ["menuTemplate"] = UiProbe.Num(template),
            ["items"] = UiProbe.MenuTracked(rows),
        };
        if (valueType is not null)
        {
            tracked["valueType"] = valueType;
            tracked["modifyEnabled"] = !LeafGrayed(rows, ModifyCommand);
            tracked["modifyBinaryDataEnabled"] = !LeafGrayed(rows, ModifyBinaryCommand);
        }

        var text = new JsonObject
        {
            ["name"] = name,
            ["items"] = UiProbe.MenuLocal(rows),
        };
        return new Observed(name, tracked, text);
    }

    private static bool LeafGrayed(IEnumerable<UiProbe.MenuRow> rows, int id)
    {
        foreach (UiProbe.MenuRow row in rows)
        {
            if (row.Id == id)
            {
                return row.Grayed;
            }

            if (row.Children.Count > 0 && LeafGrayed(row.Children, id))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOf(RegeditWindow w, string name)
    {
        w.WaitForListPopulated(TimeSpan.FromSeconds(25));
        List<string> names = w.ListNames();
        int index = names.IndexOf(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"value '{name}' is not in the list.");
        }

        return index;
    }
}
