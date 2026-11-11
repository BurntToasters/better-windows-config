using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `dialogs`: opens each classic dialog that can be opened without changing anything outside BWC-Oracle,
// screenshots it, records each control's rectangle relative to the dialog client area, and cancels it.
// Every dialog runs in its own regedit process, so no state carries from one dialog to the next.
// Tracked output holds numbers only (regedit-dialog-rects.json). Control text and titles are Microsoft
// strings, so they go to the untracked docs/parity/oracle/local/dialog-text.json.
internal static class DialogCapture
{
    private const string MixedLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Mixed";
    private const string LocalMachineLastKey = @"Computer\HKEY_LOCAL_MACHINE";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(25);

    private delegate void Trigger(RegeditWindow w);

    private sealed record Spec(string Name, string LastKey, Trigger Open, bool Inline = false);

    private static readonly Spec[] Specs =
    {
        new("edit-string", MixedLastKey, w => ModifyValue(w, "Text", "&Modify...")),
        new("edit-expand-string", MixedLastKey, w => ModifyValue(w, "Expand", "&Modify...")),
        new("edit-dword", MixedLastKey, w => ModifyValue(w, "Number", "&Modify...")),
        new("edit-qword", MixedLastKey, w => ModifyValue(w, "Big", "&Modify...")),
        new("edit-multi-string", MixedLastKey, w => ModifyValue(w, "List", "&Modify...")),
        new("edit-binary", MixedLastKey, w => ModifyValue(w, "Blob", "Modify &Binary Data...")),
        new("find", MixedLastKey, w => w.PostMenuCommand("&Edit", "&Find...")),
        new("export", MixedLastKey, w => w.PostMenuCommand("&File", "&Export...")),
        new("import", MixedLastKey, w => w.PostMenuCommand("&File", "&Import...")),
        new("load-hive", LocalMachineLastKey, w => w.PostMenuCommand("&File", "&Load Hive...")),
        new("connect-network", MixedLastKey, w => w.PostMenuCommand("&File", "&Connect Network Registry...")),
        new("permissions", MixedLastKey, w => w.PostMenuCommand("&Edit", "&Permissions...")),
        new("print", MixedLastKey, w => w.PostMenuCommand("&File", "&Print...\tCtrl+P")),
        new("add-favorite", MixedLastKey, w => w.PostMenuCommand("F&avorites", "&Add to Favorites...")),
        new("remove-favorite", MixedLastKey, w => w.PostMenuCommand("F&avorites", "&Remove Favorite...")),
        new("rename-key-inline", MixedLastKey, w => w.PostMenuCommand("&Edit", "&Rename"), Inline: true),
        new("new-key-inline", MixedLastKey, w => w.PostMenuCommand("&Edit", "&New", "&Key"), Inline: true),
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        var records = new List<(string Name, JsonObject Json)>();
        var notCaptured = new JsonArray();
        var text = new JsonObject();
        uint windowDpi = 0;

        foreach (Spec spec in Specs)
        {
            try
            {
                (JsonObject record, JsonObject textRecord, uint dpi) = CaptureOne(paths, spec);
                records.Add((spec.Name, record));
                text[spec.Name] = textRecord;
                windowDpi = dpi;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dialogs: {spec.Name} not captured: {ex.Message}");
                notCaptured.Add(new JsonObject { ["name"] = spec.Name, ["reason"] = Report.StableReason(ex.Message) });
            }
        }

        if (windowDpi == 0)
        {
            windowDpi = systemDpi;
        }

        var dialogs = new JsonArray(records.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => (JsonNode)r.Json).ToArray());

        Report.WriteJson(paths.DialogRectsJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture dialogs",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["note"] = "Rectangles are device pixels relative to the dialog client origin (inline captures: the main window client origin). Screenshots and control text are untracked under docs/parity/oracle/dialogs and docs/parity/oracle/local.",
            ["dialogs"] = dialogs,
            ["notCaptured"] = notCaptured,
        });

        Report.WriteJson(Path.Combine(paths.LocalDir, "dialog-text.json"), text);
    }

    private static (JsonObject Record, JsonObject Text, uint Dpi) CaptureOne(RepoPaths paths, Spec spec)
    {
        using RegeditWindow w = StartWithLastKey(spec.LastKey);
        var before = new HashSet<IntPtr>(w.OwnedDialogs());
        spec.Open(w);

        if (spec.Inline)
        {
            return CaptureInline(paths, spec, w);
        }

        IntPtr dlg = w.WaitForNewDialog(before, OpenTimeout);
        Thread.Sleep(800);

        uint dpi = Win32.GetDpiForWindow(dlg);
        if (!Win32.GetWindowRect(dlg, out NativeRect window) || !Win32.GetClientRect(dlg, out NativeRect client))
        {
            throw new InvalidOperationException("GetWindowRect or GetClientRect failed for the dialog.");
        }

        var origin = new NativePoint { X = 0, Y = 0 };
        Win32.ClientToScreen(dlg, ref origin);

        string title = WindowTitle(dlg);
        string screenshot = Path.Combine(paths.DialogsDir, spec.Name + ".png");
        Report.SaveWindow(dlg, screenshot);

        var controls = new List<(JsonObject Json, string Text)>();
        foreach (IntPtr child in w.ChildrenOf(dlg).Where(c => Win32.GetParent(c) == dlg))
        {
            controls.Add(ControlRecord(child, origin.X, origin.Y));
        }

        controls.Sort((a, b) =>
        {
            int c = a.Json["id"]!.GetValue<int>().CompareTo(b.Json["id"]!.GetValue<int>());
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(a.Json["class"]!.GetValue<string>(), b.Json["class"]!.GetValue<string>());
            if (c != 0)
            {
                return c;
            }

            c = a.Json["y"]!.GetValue<int>().CompareTo(b.Json["y"]!.GetValue<int>());
            return c != 0 ? c : a.Json["x"]!.GetValue<int>().CompareTo(b.Json["x"]!.GetValue<int>());
        });

        var record = new JsonObject
        {
            ["name"] = spec.Name,
            ["kind"] = "dialog",
            ["class"] = RegeditWindow.ClassOf(dlg),
            ["windowWidth"] = window.Width,
            ["windowHeight"] = window.Height,
            ["clientOffsetX"] = origin.X - window.Left,
            ["clientOffsetY"] = origin.Y - window.Top,
            ["clientWidth"] = client.Width,
            ["clientHeight"] = client.Height,
            ["controls"] = new JsonArray(controls.Select(c => (JsonNode)c.Json).ToArray()),
        };

        var textRecord = new JsonObject
        {
            ["title"] = title,
            ["controls"] = new JsonArray(controls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Json["id"]!.GetValue<int>(),
                ["class"] = c.Json["class"]!.GetValue<string>(),
                ["text"] = c.Text,
            }).ToArray()),
        };

        w.CloseDialog(dlg);
        return (record, textRecord, dpi);
    }

    // Inline edits (tree rename, new key name) have no dialog. Their edit control is a child of the tree view.
    private static (JsonObject Record, JsonObject Text, uint Dpi) CaptureInline(RepoPaths paths, Spec spec, RegeditWindow w)
    {
        IntPtr edit = RegeditWindow.WaitForNewWindow(() => w.ChildOfClassOrZero(w.Tree, "Edit"), OpenTimeout, "the inline tree edit");
        Thread.Sleep(800);

        uint dpi = Win32.GetDpiForWindow(w.Main);
        if (!Win32.GetClientRect(w.Main, out NativeRect mainClient))
        {
            throw new InvalidOperationException("GetClientRect failed for the main window.");
        }

        // The edit sits on the caret item's row, which moves with the tree's scroll position, so its rectangle is
        // recorded relative to the caret item's text rectangle (the tree scroll position is not stable between runs).
        var treeOrigin = new NativePoint { X = 0, Y = 0 };
        Win32.ClientToScreen(w.Tree, ref treeOrigin);
        NativeRect item = w.TreeItemRect(w.TreeCaret());
        int itemX = treeOrigin.X + item.Left;
        int itemY = treeOrigin.Y + item.Top;
        NativeRect editScreen = w.ScreenRectOf(edit);
        string screenshot = Path.Combine(paths.DialogsDir, spec.Name + ".png");
        Report.SaveWindow(w.Main, screenshot);

        var control = new JsonObject
        {
            ["id"] = Win32.GetDlgCtrlID(edit),
            ["class"] = RegeditWindow.ClassOf(edit),
            ["visible"] = Win32.IsWindowVisible(edit),
            ["enabled"] = Win32Ext.IsWindowEnabled(edit),
            ["x"] = editScreen.Left - itemX,
            ["y"] = editScreen.Top - itemY,
            ["width"] = editScreen.Width,
            ["height"] = editScreen.Height,
        };

        var record = new JsonObject
        {
            ["name"] = spec.Name,
            ["kind"] = "inline",
            ["class"] = RegeditWindow.ClassOf(w.Main),
            ["reference"] = "caret tree item text rectangle",
            ["windowWidth"] = mainClient.Width,
            ["windowHeight"] = mainClient.Height,
            ["clientOffsetX"] = 0,
            ["clientOffsetY"] = 0,
            ["clientWidth"] = mainClient.Width,
            ["clientHeight"] = mainClient.Height,
            ["controls"] = new JsonArray { control },
        };

        var textRecord = new JsonObject
        {
            ["title"] = WindowTitle(w.Main),
            ["controls"] = new JsonArray(),
        };

        // Cancel the inline edit with Escape. The key name is never committed.
        Win32.PostMessageW(edit, Wm.KEYDOWN, new IntPtr(Wm.VK_ESCAPE), IntPtr.Zero);
        Thread.Sleep(500);
        return (record, textRecord, dpi);
    }

    private static (JsonObject Json, string Text) ControlRecord(IntPtr h, int originX, int originY)
    {
        NativeRect screen = new();
        Win32.GetWindowRect(h, out screen);
        string text = WindowText(h);
        var json = new JsonObject
        {
            ["id"] = Win32.GetDlgCtrlID(h),
            ["class"] = RegeditWindow.ClassOf(h),
            ["visible"] = Win32.IsWindowVisible(h),
            ["enabled"] = Win32Ext.IsWindowEnabled(h),
            ["x"] = screen.Left - originX,
            ["y"] = screen.Top - originY,
            ["width"] = screen.Width,
            ["height"] = screen.Height,
        };
        return (json, text);
    }

    private static void ModifyValue(RegeditWindow w, string valueName, string menuLabel)
    {
        int index = IndexOfName(w, valueName);
        w.SelectListRow(index);
        Thread.Sleep(200);

        // The label rectangle is the point on the value's name. A double click there opens the same editor as
        // Modify (Modify Binary Data for binary values). It is tried first because it needs no menu state.
        NativeRect label = w.ListItemRect(index, Msg.LVIR_LABEL);
        w.FocusList();
        int cx = (label.Left + label.Right) / 2;
        int cy = (label.Top + label.Bottom) / 2;
        var dialogsBefore = new HashSet<IntPtr>(w.OwnedDialogs());
        w.DoubleClick(w.ListView, cx, cy);
        if (w.TryWaitForNewDialog(dialogsBefore, TimeSpan.FromSeconds(6)) != IntPtr.Zero)
        {
            return;
        }

        // Fallback: Modify and Modify Binary Data live only in the value context menu, so read their ids from it.
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
        IntPtr popup = w.OpenContextMenu(w.ListView, cx, cy, existing);
        uint id;
        bool grayed;
        try
        {
            (id, grayed) = RegeditWindow.PopupItemByLabel(w.PopupMenuHandle(popup), menuLabel);
        }
        finally
        {
            w.ClosePopup(popup);
        }

        if (grayed)
        {
            throw new InvalidOperationException($"'{menuLabel}' is grayed for value '{valueName}'.");
        }

        Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(id), IntPtr.Zero);
    }

    private static int IndexOfName(RegeditWindow w, string name)
    {
        w.WaitForListPopulated(OpenTimeout);
        List<string> names = w.ListNames();
        int index = names.IndexOf(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"value '{name}' not in the list: {string.Join(", ", names)}");
        }

        return index;
    }

    private static RegeditWindow StartWithLastKey(string lastKey)
    {
        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(lastKey));
        return RegeditWindow.Start();
    }

    private static string WindowTitle(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(512);
        Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowText(IntPtr h) => WindowTitle(h);
}
