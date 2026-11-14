using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `remote`: File > Connect Network Registry to 127.0.0.1, then reads the remote node, its children, the context menus
// of the remote node and of HKEY_USERS\<SID>\Software, the File menu states with each selected, File > Import's first
// picker (cancelled), and File > Disconnect Network Registry. Writes nothing to any registry. The only OK pressed is
// the Select Computer dialog's OK (connect, after the edit reads back 127.0.0.1) and the disconnect dialog's OK (after
// the 127.0.0.1 entry is selected and read back). See docs/failure-modes/oracle.md section 18. Tracked:
// regedit-remote.json (ids, flags, counts, hive names). Text: local/remote-text.json (redacted).
internal static class RemoteCapture
{
    private const string Target = "127.0.0.1";
    private const string UsersHive = "HKEY_USERS";
    private const int RemoteNodeTemplate = 107;
    private const int KeyContextTemplate = 104;
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20);
    private static readonly int[] FileMenuIds = { 658, 659, 680, 681, 660, 768 };
    private static readonly string[] FileMenuNames = { "Import", "Export", "Load Hive", "Unload Hive", "Print", "Disconnect" };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        ResourceDump dump = ResourceDump.Load(paths);
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("the current user's SID is unavailable.");

        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Mixed"), waitForList: true);
        uint windowDpi = Win32.GetDpiForWindow(w.Main);
        var local = new JsonObject();
        var tracked = new JsonObject();
        var notMeasured = new JsonArray();

        IntPtr node = IntPtr.Zero;
        try
        {
            (node, JsonObject connectTracked, JsonObject connectLocal) = Connect(dump, w);
            tracked["connect"] = connectTracked;
            local["connect"] = connectLocal;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"remote: connect not measured: {ex.Message}");
            tracked["connect"] = new JsonObject { ["measured"] = false, ["reason"] = Report.StableReason(ex.Message) };
            notMeasured.Add(new JsonObject { ["name"] = "connect", ["reason"] = Report.StableReason(ex.Message) });
        }

        if (node != IntPtr.Zero)
        {
            MeasureConnected(dump, w, node, sid, tracked, local, notMeasured);
        }
        else
        {
            foreach (string name in new[] { "remote-node", "remote-subkey", "file-menu", "import-picker", "disconnect" })
            {
                notMeasured.Add(new JsonObject { ["name"] = name, ["reason"] = "no remote node: the connect did not succeed." });
            }
        }

        Report.WriteJson(paths.RemoteJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture remote",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["target"] = Target,
            ["note"] = "Connected to the loopback address only. Nothing was written to any registry. Titles, item text and the address bar are in local/remote-text.json, redacted.",
            ["measurements"] = tracked,
            ["notMeasured"] = notMeasured,
        });

        Report.WriteJson(paths.LocalText("remote-text.json"), local);
    }

    // Opens the connect dialog, types the target, reads it back, and presses OK only when it matches. Returns the
    // remote node if the tree gained one. A message box shown by a failed connect is recorded and cancelled.
    private static (IntPtr Node, JsonObject Tracked, JsonObject Local) Connect(ResourceDump dump, RegeditWindow w)
    {
        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        w.PostMenuCommand("&File", "&Connect Network Registry...");
        IntPtr picker = UiProbe.WaitForNewTopLevel(w.ProcessId, before, ConnectWait);
        if (picker == IntPtr.Zero)
        {
            throw new InvalidOperationException("File > Connect Network Registry opened no dialog.");
        }

        Thread.Sleep(800);
        Observed pickerObs = DescribeDeep(dump, w, picker, "connect-picker");
        // Visible, enabled edits, largest first. The first one that reads back exactly the target is the computer name box.
        var candidates = w.ChildrenOf(picker)
            .Where(c => IsTextClass(RegeditWindow.ClassOf(c)) && Win32.IsWindowVisible(c) && Win32Ext.IsWindowEnabled(c))
            .OrderByDescending(c =>
            {
                Win32.GetWindowRect(c, out NativeRect r);
                return (long)r.Width * r.Height;
            })
            .ToList();
        IntPtr edit = IntPtr.Zero;
        var attempts = new List<string>();
        foreach (IntPtr c in candidates)
        {
            int style = Win32Ext.GetWindowLong(c, Wm.GWL_STYLE);
            bool readOnly = (style & 0x0800) != 0;
            UiProbe.SetEditText(c, Target);
            string readBack = UiProbe.ReadEditText(w, c);
            attempts.Add($"id {Win32.GetDlgCtrlID(c)} class {RegeditWindow.ClassOf(c)} readonly {readOnly} WM_SETTEXT read back '{readBack.Length}' chars");
            if (readBack != Target && !readOnly)
            {
                // SendInput for this picker only, gated before each character (docs/failure-modes/oracle.md, section 5).
                bool typed = KeyInput.TypeIntoPicker(w.Main, picker, c, Target, out string reason);
                readBack = UiProbe.ReadEditText(w, c);
                attempts.Add($"id {Win32.GetDlgCtrlID(c)} SendInput gated {(typed ? "typed" : "stopped")}: {reason}; read back '{readBack.Length}' chars");
                if (!typed)
                {
                    UiProbe.CloseWindow(w, picker);
                    throw new InvalidOperationException($"not measured: {reason}. Picker cancelled with IDCANCEL; no OK pressed.");
                }
            }
            if (readBack == Target)
            {
                edit = c;
                break;
            }
        }

        if (edit == IntPtr.Zero)
        {
            string controls = string.Join("; ", UiProbe.Controls(w, picker, deep: true).Select(c => $"{c.Class} id {c.Id} visible {c.Visible}"));
            UiProbe.CloseWindow(w, picker);
            throw new InvalidOperationException(
                $"no edit of the connect dialog accepted {Target} (tried: {string.Join(", ", attempts)}); cancelled without OK. Controls: {controls}");
        }

        var connectTracked = new JsonObject
        {
            ["measured"] = true,
            ["picker"] = pickerObs.Tracked,
            ["editReadBackMatches"] = true,
        };
        var connectLocal = new JsonObject { ["picker"] = pickerObs.Text };

        Win32.PostMessageW(picker, Wm.COMMAND, new IntPtr(UiProbe.IdOk), IntPtr.Zero);
        var deadline = DateTime.UtcNow + ConnectWait;
        IntPtr node = IntPtr.Zero;
        while (DateTime.UtcNow < deadline)
        {
            node = UiProbe.FindTreeItem(w, t => t.Contains(Target, StringComparison.Ordinal));
            if (node != IntPtr.Zero)
            {
                break;
            }

            IntPtr message = FirstOtherDialog(w, before, picker);
            if (message != IntPtr.Zero)
            {
                Thread.Sleep(500);
                Observed messageObs = DescribeDeep(dump, w, message, "connect-message");
                connectTracked["connected"] = false;
                connectTracked["message"] = messageObs.Tracked;
                connectLocal["message"] = messageObs.Text;
                UiProbe.CloseWindow(w, message);
                break;
            }

            Thread.Sleep(200);
        }

        if (node == IntPtr.Zero && UiProbe.VisibleTopLevels(w.ProcessId).Contains(picker))
        {
            UiProbe.CloseWindow(w, picker);
            connectTracked["pickerStillOpen"] = true;
        }

        connectTracked["connected"] = node != IntPtr.Zero;
        return (node, connectTracked, connectLocal);
    }

    // Edit controls and rich edit controls, the two text classes a dialog's name box can be.
    private static bool IsTextClass(string cls) => cls == "Edit" || cls.StartsWith("RICHEDIT", StringComparison.Ordinal);

    private static IntPtr FirstOtherDialog(RegeditWindow w, HashSet<IntPtr> before, IntPtr picker)
    {
        foreach (IntPtr h in UiProbe.VisibleTopLevels(w.ProcessId).OrderBy(x => x.ToInt64()))
        {
            if (!before.Contains(h) && h != picker && RegeditWindow.ClassOf(h) == RegeditWindow.DialogClass)
            {
                return h;
            }
        }

        return IntPtr.Zero;
    }

    private static void MeasureConnected(ResourceDump dump, RegeditWindow w, IntPtr node, string sid,
        JsonObject tracked, JsonObject local, JsonArray notMeasured)
    {
        // The node's children (hive names are registry constants, so they are kept as names).
        List<IntPtr> children = UiProbe.TreeChildren(w, node);
        var childNames = children.Select(c => w.TreeText(c)).ToList();
        var nodeLocal = new JsonObject
        {
            ["label"] = w.TreeText(node),
            ["children"] = new JsonArray(childNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };
        tracked["remoteNode"] = new JsonObject
        {
            ["measured"] = true,
            ["childCount"] = childNames.Count,
            ["hiveChildren"] = new JsonArray(childNames.Where(n => n.StartsWith("HKEY_", StringComparison.Ordinal))
                .Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };

        // Context menu on the remote node.
        UiProbe.SelectItem(w, node);
        tracked["remoteNodeContext"] = ContextOf(dump, w, "remote-node", RemoteNodeTemplate, local);

        // HKEY_USERS\<SID>\Software.
        IntPtr users = children.FirstOrDefault(c => w.TreeText(c) == UsersHive);
        if (users == IntPtr.Zero)
        {
            notMeasured.Add(new JsonObject { ["name"] = "remote-subkey", ["reason"] = "the remote node has no HKEY_USERS child." });
            return;
        }

        UiProbe.ExpandTreeItem(w, users);
        IntPtr userItem = UiProbe.TreeChildren(w, users).FirstOrDefault(c => w.TreeText(c) == sid);
        if (userItem == IntPtr.Zero)
        {
            notMeasured.Add(new JsonObject { ["name"] = "remote-subkey", ["reason"] = "HKEY_USERS on the remote node has no entry for the current user." });
            return;
        }

        UiProbe.ExpandTreeItem(w, userItem);
        IntPtr software = UiProbe.TreeChildren(w, userItem).FirstOrDefault(c => w.TreeText(c) == "Software");
        if (software == IntPtr.Zero)
        {
            notMeasured.Add(new JsonObject { ["name"] = "remote-subkey", ["reason"] = "the user's hive on the remote node has no Software key." });
            return;
        }

        UiProbe.SelectItem(w, software);
        local["subkeyAddressBar"] = w.AddressText();
        local["subkeySelected"] = w.SelectedTreeText();
        tracked["remoteSubkeyContext"] = ContextOf(dump, w, "remote-subkey", KeyContextTemplate, local);
        tracked["remoteSubkeyAddressBarLength"] = w.AddressText().Length;
        tracked["fileMenuWithRemoteSubkey"] = FileMenuStates(w, local, "fileMenuRemoteSubkey");

        // File menu with the remote node, then File > Import's first picker.
        UiProbe.SelectItem(w, node);
        tracked["fileMenuWithRemoteNode"] = FileMenuStates(w, local, "fileMenuRemoteNode");
        tracked["importPicker"] = OpenImportPicker(dump, w, local);

        // Disconnect through File > Disconnect Network Registry.
        tracked["disconnect"] = Disconnect(dump, w, node, local);
    }


    // The menu of a context popup, matched to a resource template by its leaf ids.
    private static JsonObject ContextOf(ResourceDump dump, RegeditWindow w, string name, int? expectedTemplate, JsonObject local)
    {
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
        IntPtr popup = UiProbe.OpenTreeContext(w, existing);
        try
        {
            List<UiProbe.MenuRow> rows = UiProbe.MenuRows(w.PopupMenuHandle(popup), 0);
            List<int> leaves = UiProbe.LeafIds(rows);
            int? template = dump.MatchMenu(leaves);
            local[name + "-menu"] = UiProbe.MenuLocal(rows);
            return new JsonObject
            {
                ["measured"] = true,
                ["menuTemplate"] = UiProbe.Num(template),
                ["expectedTemplate"] = UiProbe.Num(expectedTemplate),
                ["items"] = UiProbe.MenuTracked(rows),
            };
        }
        finally
        {
            w.ClosePopup(popup);
        }
    }

    // Reads the enabled state of the File menu items the capture names, with the current selection.
    private static JsonArray FileMenuStates(RegeditWindow w, JsonObject local, string name)
    {
        (IntPtr popup, List<UiProbe.MenuRow> rows) = UiProbe.OpenTopMenu(w, 'F');
        try
        {
            var states = new JsonArray();
            for (int i = 0; i < FileMenuIds.Length; i++)
            {
                UiProbe.MenuRow? row = rows.FirstOrDefault(r => r.Id == FileMenuIds[i]);
                states.Add(new JsonObject
                {
                    ["id"] = FileMenuIds[i],
                    ["name"] = FileMenuNames[i],
                    ["found"] = row is not null,
                    ["enabled"] = row is not null && !row.Grayed,
                });
            }

            local[name + "-items"] = UiProbe.MenuLocal(rows);
            return states;
        }
        finally
        {
            w.ClosePopup(popup);
        }
    }

    // File > Import with the remote computer connected. The first window must be the computer picker. It is cancelled.
    private static JsonObject OpenImportPicker(ResourceDump dump, RegeditWindow w, JsonObject local)
    {
        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        w.PostMenuCommand("&File", "&Import...");
        IntPtr dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, before, TimeSpan.FromSeconds(15));
        if (dlg == IntPtr.Zero)
        {
            return new JsonObject { ["measured"] = false, ["reason"] = "File > Import opened no window with the remote node selected." };
        }

        Thread.Sleep(800);
        try
        {
            Observed obs = DescribeDeep(dump, w, dlg, "import-picker");
            local["importPicker"] = obs.Text;
            return new JsonObject
            {
                ["measured"] = true,
                ["picker"] = obs.Tracked,
                ["cancelled"] = true,
            };
        }
        finally
        {
            UiProbe.CloseWindow(w, dlg);
        }
    }

    // File > Disconnect Network Registry. Selects the 127.0.0.1 entry of the list, reads the selection back, and
    // presses OK. Cancels instead when the entry or the list is not what was expected.
    private static JsonObject Disconnect(ResourceDump dump, RegeditWindow w, IntPtr node, JsonObject local)
    {
        UiProbe.SelectItem(w, node);
        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        w.PostMenuCommand("&File", "&Disconnect Network Registry...");
        IntPtr dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, before, TimeSpan.FromSeconds(15));
        if (dlg == IntPtr.Zero)
        {
            return new JsonObject { ["measured"] = false, ["reason"] = "File > Disconnect Network Registry opened no dialog." };
        }

        Thread.Sleep(800);
        var result = new JsonObject { ["measured"] = true };
        try
        {
            Observed obs = DescribeDeep(dump, w, dlg, "disconnect-dialog");
            result["dialog"] = obs.Tracked;
            local["disconnectDialog"] = obs.Text;

            IntPtr list = w.ChildrenOf(dlg).FirstOrDefault(c => RegeditWindow.ClassOf(c) == "ListBox");
            if (list == IntPtr.Zero)
            {
                result["selected"] = false;
                result["reason"] = "the disconnect dialog has no list box; cancelled.";
                return result;
            }

            int count = UiProbe.ListBoxCount(w, list);
            int index = -1;
            for (int i = 0; i < count; i++)
            {
                if (UiProbe.ListBoxText(w, list, i).Contains(Target, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                result["selected"] = false;
                result["reason"] = "no list entry names the remote computer; cancelled.";
                return result;
            }

            w.Send(list, UiProbe.LbSetCurSel, new IntPtr(index), IntPtr.Zero);
            int readBack = w.Send(list, UiProbe.LbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (readBack != index)
            {
                result["selected"] = false;
                result["reason"] = "the selection did not read back; cancelled.";
                return result;
            }

            Win32.PostMessageW(dlg, Wm.COMMAND, new IntPtr(UiProbe.IdOk), IntPtr.Zero);
            result["selected"] = true;
            result["okPosted"] = true;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && UiProbe.FindTreeItem(w, t => t.Contains(Target, StringComparison.Ordinal)) != IntPtr.Zero)
            {
                Thread.Sleep(200);
            }

            result["remoteNodeGone"] = UiProbe.FindTreeItem(w, t => t.Contains(Target, StringComparison.Ordinal)) == IntPtr.Zero;
            return result;
        }
        finally
        {
            if (Win32Ext.IsWindow(dlg))
            {
                UiProbe.CloseWindow(w, dlg);
            }
        }
    }

    // Describes a window with every descendant control, its template by control ids, and its title (local only).
    private static Observed DescribeDeep(ResourceDump dump, RegeditWindow w, IntPtr h, string name)
    {
        List<UiProbe.Ctl> controls = UiProbe.Controls(w, h, deep: true);
        var tracked = new JsonObject
        {
            ["class"] = RegeditWindow.ClassOf(h),
            ["dialogTemplate"] = UiProbe.Num(dump.MatchDialog(UiProbe.SortedControlIds(controls))),
            ["controls"] = UiProbe.ControlsTracked(controls),
        };
        var text = new JsonObject
        {
            ["title"] = UiProbe.TextOf(h),
            ["controls"] = UiProbe.ControlsLocal(controls),
        };
        return new Observed(name, tracked, text);
    }
}
