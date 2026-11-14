using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// One named observation: tracked numbers and classes, and the untracked text for the local JSON.
internal sealed record Observed(string Name, JsonObject Tracked, JsonObject Text);

// Shared by the 0.1.2 GUI captures: focus and selection state, new top-level windows, popup menu rows, dialog controls,
// tab and list box items. Text read here goes to the untracked local JSON. Tracked JSON takes ids, classes, flags,
// geometry and counts. Text is read through remote buffers, because regedit runs in its own process.
internal static class UiProbe
{
    internal const uint TvgnPrevious = 2;
    internal const uint TvmExpand = 0x1102;
    internal const uint TveExpand = 0x2;
    internal const uint TcmGetItemCount = 0x1304;
    internal const uint TcmGetItemW = 0x133C;
    internal const uint TcifText = 0x1;
    internal const uint LbGetCount = 0x018B;
    internal const uint LbGetText = 0x0189;
    internal const uint LbGetTextLen = 0x018A;
    internal const uint LbSetCurSel = 0x0186;
    internal const uint LbGetCurSel = 0x0188;
    internal const uint BstChecked = 1;
    internal const uint CommandFocusPane = 640;
    internal const int IdOk = 1;
    internal const int IdCancel = 2;

    private const int WmSetText = 0x000C;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);

    internal static string LastKeyFor(string relative) =>
        relative.Length == 0
            ? @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle"
            : @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\" + relative;

    // Starts regedit with the Regedit applet LastKey set to lastKey. The applet state is restored by OracleSession.
    internal static RegeditWindow StartAt(string lastKey, bool waitForList)
    {
        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(lastKey));
        RegeditWindow w = RegeditWindow.Start();
        if (waitForList)
        {
            w.WaitForListPopulated(TimeSpan.FromSeconds(25));
        }
        else
        {
            Thread.Sleep(Settle);
        }

        return w;
    }

    internal static void Settle800() => Thread.Sleep(Settle);

    internal static string TextOf(IntPtr h)
    {
        if (h == IntPtr.Zero)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(1024);
        Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static IntPtr FocusOf(IntPtr main)
    {
        uint thread = Win32.GetWindowThreadProcessId(main, out _);
        var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return Win32Ext.GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    // Moves keyboard focus to target with classic regedit's pane switch (command 640). Throws if focus does not arrive.
    internal static void EnsureFocus(RegeditWindow w, IntPtr target)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (FocusOf(w.Main) == target)
            {
                return;
            }

            Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(CommandFocusPane), IntPtr.Zero);
            Thread.Sleep(300);
        }

        throw new InvalidOperationException("keyboard focus did not reach the requested control.");
    }

    internal sealed record Probe(
        string FocusClass,
        int FocusId,
        string FocusText,
        int CaretDepth,
        int CaretIndex,
        string CaretPath,
        int ListCount,
        int ListSelected,
        string ListSelectedName,
        string Address);

    internal static Probe Read(RegeditWindow w)
    {
        IntPtr focus = FocusOf(w.Main);
        IntPtr caret = w.TreeCaret();
        int depth = 0;
        for (IntPtr p = ParentOf(w, caret); p != IntPtr.Zero; p = ParentOf(w, p))
        {
            depth++;
        }

        int index = 0;
        for (IntPtr p = PreviousOf(w, caret); p != IntPtr.Zero; p = PreviousOf(w, p))
        {
            index++;
        }

        int selected = w.SelectedListIndex();
        return new Probe(
            focus == IntPtr.Zero ? string.Empty : RegeditWindow.ClassOf(focus),
            focus == IntPtr.Zero ? 0 : Win32.GetDlgCtrlID(focus),
            focus == IntPtr.Zero ? string.Empty : TextOf(focus),
            depth,
            index,
            caret == IntPtr.Zero ? string.Empty : w.TreePathOf(caret),
            w.ListCount(),
            selected,
            selected >= 0 ? w.ListItemText(selected, 0) : string.Empty,
            w.AddressText());
    }

    // A nullable number as JSON: null when absent, so an unknown template or a missing accelerator is never a 0.
    internal static JsonNode? Num(int? value) => value is int v ? JsonValue.Create(v) : null;

    internal static JsonObject TrackedProbe(Probe p) => new()
    {
        ["focusClass"] = p.FocusClass,
        ["focusId"] = p.FocusId,
        ["caretDepth"] = p.CaretDepth,
        ["caretIndex"] = p.CaretIndex,
        ["listCount"] = p.ListCount,
        ["listSelected"] = p.ListSelected,
        ["addressLength"] = p.Address.Length,
    };

    internal static JsonObject LocalProbe(Probe p) => new()
    {
        ["focusText"] = p.FocusText,
        ["caretPath"] = p.CaretPath,
        ["listSelectedName"] = p.ListSelectedName,
        ["address"] = p.Address,
    };

    private static IntPtr ParentOf(RegeditWindow w, IntPtr item) =>
        item == IntPtr.Zero ? IntPtr.Zero : w.Send(w.Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_PARENT), item);

    private static IntPtr PreviousOf(RegeditWindow w, IntPtr item) =>
        item == IntPtr.Zero ? IntPtr.Zero : w.Send(w.Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)TvgnPrevious), item);

    // ---- Top-level windows of the started process ----

    internal static HashSet<IntPtr> VisibleTopLevels(int pid)
    {
        var set = new HashSet<IntPtr>();
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid && Win32.IsWindowVisible(h))
            {
                set.Add(h);
            }

            return true;
        }, IntPtr.Zero);
        return set;
    }

    // Waits for a visible top-level window of the process that was not in before. className filters when given.
    // IntPtr.Zero when none appears within the timeout (no exception).
    internal static IntPtr WaitForNewTopLevel(int pid, HashSet<IntPtr> before, TimeSpan timeout, string? className = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (IntPtr h in VisibleTopLevels(pid).OrderBy(x => x.ToInt64()))
            {
                if (before.Contains(h))
                {
                    continue;
                }

                if (className is null || RegeditWindow.ClassOf(h) == className)
                {
                    return h;
                }
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    internal static void CloseWindow(RegeditWindow w, IntPtr h)
    {
        if (RegeditWindow.ClassOf(h) == RegeditWindow.PopupClass)
        {
            w.ClosePopup(h);
        }
        else
        {
            w.CloseDialog(h);
        }
    }

    // ---- Menus ----

    internal sealed record MenuRow(int Index, int Id, string Label, bool Separator, bool Grayed, bool Checked, List<MenuRow> Children);

    internal static List<MenuRow> MenuRows(IntPtr hmenu, int depth)
    {
        var rows = new List<MenuRow>();
        int count = Win32.GetMenuItemCount(hmenu);
        for (int i = 0; i < count; i++)
        {
            uint flags = Win32.GetMenuState(hmenu, (uint)i, Wm.MF_BYPOSITION);
            bool separator = (flags & Wm.MF_SEPARATOR) != 0;
            IntPtr sub = Win32.GetSubMenu(hmenu, i);
            List<MenuRow> children = sub != IntPtr.Zero && depth < 3 ? MenuRows(sub, depth + 1) : new List<MenuRow>();
            rows.Add(new MenuRow(
                i,
                separator ? 0 : (int)Win32.GetMenuItemID(hmenu, i),
                RegeditWindow.LabelAt(hmenu, i),
                separator,
                (flags & Wm.MF_GRAYED) != 0,
                (flags & Wm.MF_CHECKED) != 0,
                children));
        }

        return rows;
    }

    // Command ids of the leaf items (no popup, no separator), sorted. The shape the resource dump uses for matching.
    internal static List<int> LeafIds(IEnumerable<MenuRow> rows)
    {
        var ids = new List<int>();
        foreach (MenuRow row in rows)
        {
            if (row.Separator)
            {
                continue;
            }

            if (row.Children.Count > 0)
            {
                ids.AddRange(LeafIds(row.Children));
            }
            else if (row.Id > 0)
            {
                ids.Add(row.Id);
            }
        }

        ids.Sort();
        return ids;
    }

    internal static JsonArray MenuTracked(IEnumerable<MenuRow> rows)
    {
        var array = new JsonArray();
        foreach (MenuRow row in rows)
        {
            var node = new JsonObject
            {
                ["index"] = row.Index,
                ["id"] = row.Separator ? null : JsonValue.Create(row.Id),
                ["separator"] = row.Separator,
                ["grayed"] = row.Grayed,
                ["checked"] = row.Checked,
                ["submenu"] = row.Children.Count > 0,
            };
            if (row.Children.Count > 0)
            {
                node["children"] = MenuTracked(row.Children);
            }

            array.Add(node);
        }

        return array;
    }

    internal static JsonArray MenuLocal(IEnumerable<MenuRow> rows)
    {
        var array = new JsonArray();
        foreach (MenuRow row in rows)
        {
            var node = new JsonObject { ["index"] = row.Index, ["text"] = row.Label };
            if (row.Children.Count > 0)
            {
                node["children"] = MenuLocal(row.Children);
            }

            array.Add(node);
        }

        return array;
    }

    // Opens a top-level menu by its accelerator letter and returns the rows of the popup that appears.
    internal static (IntPtr Popup, List<MenuRow> Rows) OpenTopMenu(RegeditWindow w, char accelerator)
    {
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));
        w.OpenTopMenu(accelerator);
        IntPtr popup = w.WaitForNewPopup(existing, TimeSpan.FromSeconds(10));
        Thread.Sleep(500);
        return (popup, MenuRows(w.PopupMenuHandle(popup), 0));
    }

    // ---- Dialog and popup descriptions ----

    internal sealed record Ctl(
        IntPtr Hwnd,
        int Id,
        string Class,
        bool Visible,
        bool Enabled,
        int Style,
        int Checked,
        string Text,
        int X,
        int Y,
        int Width,
        int Height);

    // Direct children of a dialog, sorted by id, class, then position (the same order DialogCapture uses). deep: every
    // descendant, for pages whose controls sit inside a tab page or a child dialog.
    internal static List<Ctl> Controls(RegeditWindow w, IntPtr dlg, bool deep = false)
    {
        (int originX, int originY) = OriginXY(dlg);
        var list = new List<Ctl>();
        IEnumerable<IntPtr> children = deep ? w.ChildrenOf(dlg) : w.ChildrenOf(dlg).Where(c => Win32.GetParent(c) == dlg);
        foreach (IntPtr c in children)
        {
            Win32.GetWindowRect(c, out NativeRect r);
            int style = Win32Ext.GetWindowLong(c, Wm.GWL_STYLE);
            string cls = RegeditWindow.ClassOf(c);
            int checkedState = -1;
            if (cls == "Button" && IsCheckable(style))
            {
                checkedState = w.Send(c, Wm.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32() == BstChecked ? 1 : 0;
            }

            list.Add(new Ctl(
                c,
                Win32.GetDlgCtrlID(c),
                cls,
                Win32.IsWindowVisible(c),
                Win32Ext.IsWindowEnabled(c),
                style,
                checkedState,
                TextOf(c),
                r.Left - originX,
                r.Top - originY,
                r.Width,
                r.Height));
        }

        list.Sort((a, b) =>
        {
            int k = a.Id.CompareTo(b.Id);
            if (k != 0)
            {
                return k;
            }

            k = string.CompareOrdinal(a.Class, b.Class);
            if (k != 0)
            {
                return k;
            }

            k = a.Y.CompareTo(b.Y);
            return k != 0 ? k : a.X.CompareTo(b.X);
        });
        return list;
    }

    // Button styles 2 to 6 and 9 are check boxes and radio buttons (BS_CHECKBOX, BS_RADIOBUTTON and the auto forms).
    private static bool IsCheckable(int style)
    {
        int type = style & 0xF;
        return type is 2 or 3 or 4 or 5 or 6 or 9;
    }

    internal static (int X, int Y) OriginXY(IntPtr h)
    {
        var p = new NativePoint { X = 0, Y = 0 };
        Win32.ClientToScreen(h, ref p);
        return (p.X, p.Y);
    }

    internal static JsonArray ControlsTracked(IEnumerable<Ctl> controls)
    {
        var array = new JsonArray();
        foreach (Ctl c in controls)
        {
            var node = new JsonObject
            {
                ["id"] = c.Id,
                ["class"] = c.Class,
                ["visible"] = c.Visible,
                ["enabled"] = c.Enabled,
                ["style"] = $"0x{(uint)c.Style:X8}",
                ["x"] = c.X,
                ["y"] = c.Y,
                ["width"] = c.Width,
                ["height"] = c.Height,
            };
            if (c.Checked >= 0)
            {
                node["checked"] = c.Checked == 1;
            }

            array.Add(node);
        }

        return array;
    }

    internal static JsonArray ControlsLocal(IEnumerable<Ctl> controls)
    {
        var array = new JsonArray();
        foreach (Ctl c in controls)
        {
            array.Add(new JsonObject { ["id"] = c.Id, ["class"] = c.Class, ["text"] = c.Text });
        }

        return array;
    }

    internal static List<int> SortedControlIds(IEnumerable<Ctl> controls) => controls.Select(c => c.Id).OrderBy(i => i).ToList();

    // Describes one new top-level window of the started process. Popups list their rows. Dialogs list their controls.
    internal static Observed Describe(RegeditWindow w, IntPtr h, string name)
    {
        string cls = RegeditWindow.ClassOf(h);
        var tracked = new JsonObject { ["class"] = cls, ["visible"] = Win32.IsWindowVisible(h) };
        var text = new JsonObject { ["title"] = TextOf(h) };
        if (cls == RegeditWindow.PopupClass)
        {
            List<MenuRow> rows = MenuRows(w.PopupMenuHandle(h), 0);
            tracked["kind"] = "popup";
            tracked["items"] = MenuTracked(rows);
            text["items"] = MenuLocal(rows);
        }
        else if (cls == RegeditWindow.DialogClass)
        {
            List<Ctl> controls = Controls(w, h);
            tracked["kind"] = "dialog";
            tracked["controls"] = ControlsTracked(controls);
            text["controls"] = ControlsLocal(controls);
        }
        else
        {
            tracked["kind"] = "window";
        }

        return new Observed(name, tracked, text);
    }

    // ---- List box, tab, tree and list helpers ----

    internal static IntPtr ChildOfClass(RegeditWindow w, IntPtr parent, string className) =>
        w.ChildrenOf(parent).FirstOrDefault(c => Win32.GetParent(c) == parent && RegeditWindow.ClassOf(c) == className);

    // The child with the largest area, ignoring static text and buttons. Used to find the data box of an edit dialog.
    internal static IntPtr LargestEditable(RegeditWindow w, IntPtr dlg)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        foreach (IntPtr c in w.ChildrenOf(dlg).Where(c => Win32.GetParent(c) == dlg && Win32.IsWindowVisible(c)))
        {
            string cls = RegeditWindow.ClassOf(c);
            if (cls is "Static" or "Button" or "ComboBox")
            {
                continue;
            }

            Win32.GetWindowRect(c, out NativeRect r);
            long area = (long)r.Width * r.Height;
            if (area > bestArea)
            {
                best = c;
                bestArea = area;
            }
        }

        return best;
    }

    internal static List<string> TabTexts(RegeditWindow w, IntPtr tab)
    {
        var names = new List<string>();
        if (tab == IntPtr.Zero)
        {
            return names;
        }

        int count = w.Send(tab, TcmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
        for (int i = 0; i < count; i++)
        {
            names.Add(TabText(w, tab, i));
        }

        return names;
    }

    private static string TabText(RegeditWindow w, IntPtr tab, int index)
    {
        const int chars = 256;
        using var text = new RemoteBuffer(w.ProcessHandle, chars * 2);
        using var item = new RemoteBuffer(w.ProcessHandle, 40);
        byte[] s = new byte[40];
        BitConverter.GetBytes(TcifText).CopyTo(s, 0);
        BitConverter.GetBytes(text.Address.ToInt64()).CopyTo(s, 16);
        BitConverter.GetBytes(chars - 1).CopyTo(s, 24);
        item.Write(s);
        w.Send(tab, TcmGetItemW, new IntPtr(index), item.Address);
        return Encoding.Unicode.GetString(text.ReadAll()).Split('\0')[0];
    }

    internal static int ListBoxCount(RegeditWindow w, IntPtr lb) => w.Send(lb, LbGetCount, IntPtr.Zero, IntPtr.Zero).ToInt32();

    internal static string ListBoxText(RegeditWindow w, IntPtr lb, int index)
    {
        int length = w.Send(lb, LbGetTextLen, new IntPtr(index), IntPtr.Zero).ToInt32();
        if (length <= 0)
        {
            return string.Empty;
        }

        using var text = new RemoteBuffer(w.ProcessHandle, (length + 2) * 2);
        w.Send(lb, LbGetText, new IntPtr(index), text.Address);
        return Encoding.Unicode.GetString(text.ReadAll()).Split('\0')[0];
    }

    // Reads an edit or rich edit control of regedit's process with WM_GETTEXTLENGTH and WM_GETTEXT into a remote buffer.
    // GetWindowText is not used here: it returned empty for the Connect picker's rich edit while text was present.
    internal static string ReadEditText(RegeditWindow w, IntPtr h)
    {
        const uint wmGetTextLength = 0x000E;
        const uint wmGetText = 0x000D;
        try
        {
            int length = w.Send(h, wmGetTextLength, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (length <= 0)
            {
                return string.Empty;
            }

            using var buffer = new RemoteBuffer(w.ProcessHandle, (length + 1) * 2);
            w.Send(h, wmGetText, new IntPtr(length + 1), buffer.Address);
            return Encoding.Unicode.GetString(buffer.ReadAll()).Split('\0')[0];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Stable text: the handle value would differ between runs.
            throw new InvalidOperationException("the name box did not answer WM_GETTEXT (failed or timed out).");
        }
    }

    // Writes text into an edit control of another process. Returns the text read back.
    internal static string SetEditText(IntPtr edit, string value)
    {
        Win32Ext.SendMessageText(edit, WmSetText, IntPtr.Zero, value);
        return TextOf(edit);
    }

    // Screen point of the centre of a rectangle given in the client coordinates of h.
    internal static (int X, int Y) CentreOnScreen(IntPtr h, NativeRect clientRect)
    {
        (int ox, int oy) = OriginXY(h);
        return (ox + (clientRect.Left + clientRect.Right) / 2, oy + (clientRect.Top + clientRect.Bottom) / 2);
    }

    internal static (int X, int Y) CentreOnScreen(IntPtr h)
    {
        Win32.GetWindowRect(h, out NativeRect r);
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    // lParam for WM_CONTEXTMENU: the low word is x and the high word is y, both in screen coordinates.
    internal static IntPtr ScreenLParam(int x, int y) => new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    // Posts WM_CONTEXTMENU to a control with an explicit screen point. The point selects the item, not the real cursor.
    internal static void PostContextMenuAt(IntPtr control, int x, int y) =>
        Win32.PostMessageW(control, Wm.CONTEXTMENU, control, ScreenLParam(x, y));

    // The tree item whose text matches, searched from the roots through the children that are present.
    internal static IntPtr FindTreeItem(RegeditWindow w, Func<string, bool> match)
    {
        var stack = new Stack<IntPtr>();
        foreach (IntPtr root in w.TreeRoots())
        {
            stack.Push(root);
        }

        while (stack.Count > 0)
        {
            IntPtr item = stack.Pop();
            if (match(w.TreeText(item)))
            {
                return item;
            }

            var children = new List<IntPtr>();
            for (IntPtr c = w.TreeChild(item); c != IntPtr.Zero; c = w.TreeNext(c))
            {
                children.Add(c);
            }

            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        return IntPtr.Zero;
    }

    internal static List<IntPtr> TreeChildren(RegeditWindow w, IntPtr item)
    {
        var children = new List<IntPtr>();
        for (IntPtr c = w.TreeChild(item); c != IntPtr.Zero; c = w.TreeNext(c))
        {
            children.Add(c);
        }

        return children;
    }

    internal static void ExpandTreeItem(RegeditWindow w, IntPtr item)
    {
        w.Send(w.Tree, TvmExpand, new IntPtr((int)TveExpand), item);
        Thread.Sleep(400);
    }

    internal static void SelectItem(RegeditWindow w, IntPtr item)
    {
        w.SelectTreeItem(item);
        Thread.Sleep(600);
    }

    // Opens the tree's context menu for the caret item (the keyboard form of WM_CONTEXTMENU, as MenuCapture does).
    internal static IntPtr OpenTreeContext(RegeditWindow w, HashSet<IntPtr> existing)
    {
        Win32.PostMessageW(w.Tree, Wm.CONTEXTMENU, w.Tree, new IntPtr(-1));
        IntPtr popup = w.TryWaitForNewPopup(existing, TimeSpan.FromSeconds(4));
        if (popup == IntPtr.Zero)
        {
            throw new InvalidOperationException("no context menu opened on the tree.");
        }

        Thread.Sleep(500);
        return popup;
    }
}
