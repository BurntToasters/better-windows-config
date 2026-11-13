using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.Oracle.Capture;

// One classic regedit process started by this tool, plus the message-level driver for its main window.
// Every window is found by the process id this tool started. Input is posted with PostMessage or sent
// with SendMessageTimeout; no SendInput, cursor or foreground calls, so the user's desktop is not taken over.
internal sealed class RegeditWindow : IDisposable
{
    internal const string MainClass = "RegEdit_RegEdit";
    internal const string PopupClass = "#32768";
    internal const string DialogClass = "#32770";

    private const uint SmtoAbortIfHung = 0x0002;
    private const uint LvifState = 0x0008;

    private readonly Process _process;

    private RegeditWindow(Process process, IntPtr main)
    {
        _process = process;
        Main = main;
    }

    internal IntPtr Main { get; }

    internal int ProcessId => _process.Id;

    internal IntPtr ProcessHandle => _process.Handle;

    internal bool HasExited => _process.HasExited;

    internal int ExitCode => _process.ExitCode;

    // Starts regedit with __COMPAT_LAYER=RunAsInvoker on the child only and waits for its main window.
    internal static RegeditWindow Start(IEnumerable<string>? arguments = null)
    {
        Process process = RegeditProcess.Start(arguments ?? Array.Empty<string>());
        process.WaitForInputIdle(15000);
        IntPtr main = WaitForMainWindow(process);
        return new RegeditWindow(process, main);
    }

    private static IntPtr WaitForMainWindow(Process proc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited)
            {
                throw new InvalidOperationException($"regedit exited with code {proc.ExitCode} before its window appeared.");
            }

            IntPtr found = IntPtr.Zero;
            foreach (IntPtr h in TopLevel(proc.Id, MainClass))
            {
                found = h;
                break;
            }

            if (found != IntPtr.Zero)
            {
                return found;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"regedit pid {proc.Id} showed no {MainClass} window within 30s.");
    }

    // Asks the window to close with WM_CLOSE. Callers wait for the process; Dispose kills it if it is still running.
    internal void CloseMain()
    {
        if (!_process.HasExited)
        {
            Win32.PostMessageW(Main, Wm.CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!_process.WaitForExit(15000))
            {
                throw new TimeoutException($"regedit pid {_process.Id} did not exit within 15s of WM_CLOSE.");
            }
        }
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            try
            {
                CloseGracefully();
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being closed.
            }
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill();
                _process.WaitForExit(5000);
                Console.WriteLine($"regedit: killed pid {_process.Id} that this run started");
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }
        }

        _process.Dispose();
    }

    // ---- Top-level windows of this process ----

    internal List<IntPtr> TopLevel(string className, bool visibleOnly = true) => TopLevel(_process.Id, className, visibleOnly);

    internal static List<IntPtr> TopLevel(int pid, string className, bool visibleOnly = true)
    {
        var result = new List<IntPtr>();
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid && ClassOf(h) == className && (!visibleOnly || Win32.IsWindowVisible(h)))
            {
                result.Add(h);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    // Dialogs owned by the main window (regedit's own dialogs, common file dialogs and property sheets).
    internal List<IntPtr> OwnedDialogs()
    {
        var result = new List<IntPtr>();
        foreach (IntPtr h in TopLevel(DialogClass))
        {
            if (Win32Ext.GetWindow(h, Wm.GW_OWNER) == Main || Win32.GetParent(h) == Main)
            {
                result.Add(h);
            }
        }

        return result;
    }

    // Waits for a dialog owned by the main window that was not in the existing set. IntPtr.Zero means none yet.
    internal IntPtr WaitForNewDialog(HashSet<IntPtr> existing, TimeSpan timeout)
    {
        return WithWindowList(() => WaitForNewWindow(() => OwnedDialogs().FirstOrDefault(h => !existing.Contains(h)), timeout, "dialog owned by the main window"));
    }

    internal IntPtr WaitForNewPopup(HashSet<IntPtr> existing, TimeSpan timeout)
    {
        return WithWindowList(() => WaitForNewWindow(() => TopLevel(PopupClass).FirstOrDefault(h => !existing.Contains(h)), timeout, "popup menu window"));
    }

    // Returns the probe's window, or IntPtr.Zero when none appeared within the timeout (no exception).
    internal IntPtr TryWaitForNewPopup(HashSet<IntPtr> existing, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            IntPtr h = TopLevel(PopupClass).FirstOrDefault(p => !existing.Contains(p));
            if (h != IntPtr.Zero)
            {
                return h;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    // Adds the list of this process's windows to a timeout, so a failed capture says what was on screen.
    private IntPtr WithWindowList(Func<IntPtr> wait)
    {
        try
        {
            return wait();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Windows of pid {_process.Id}: {DescribeWindows()}", ex);
        }
    }

    internal static IntPtr WaitForNewWindow(Func<IntPtr> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            IntPtr h = probe();
            if (h != IntPtr.Zero)
            {
                return h;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException($"no {what} appeared within {timeout.TotalSeconds}s.");
    }

    // Closes a dialog or popup the way a user would cancel it: IDCANCEL first, then WM_CLOSE.
    // Never sends IDOK or Enter, so a dialog cannot commit a change.
    internal void CloseDialog(IntPtr dialog)
    {
        if (!Win32Ext.IsWindow(dialog))
        {
            return;
        }

        Win32.PostMessageW(dialog, Wm.COMMAND, new IntPtr(Wm.IDCANCEL), IntPtr.Zero);
        if (!WaitGone(dialog, TimeSpan.FromSeconds(5)))
        {
            Win32.PostMessageW(dialog, Wm.CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!WaitGone(dialog, TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"dialog 0x{dialog:X} did not close after IDCANCEL and WM_CLOSE.");
            }
        }
    }

    // Closes an open popup menu with Escape, falling back to WM_CANCELMODE on the main window.
    internal void ClosePopup(IntPtr popup)
    {
        if (!Win32Ext.IsWindow(popup) || !Win32.IsWindowVisible(popup))
        {
            return;
        }

        Win32.PostMessageW(popup, Wm.KEYDOWN, new IntPtr(Wm.VK_ESCAPE), new IntPtr(0x00010001));
        if (!WaitGone(popup, TimeSpan.FromSeconds(2)))
        {
            Win32.PostMessageW(Main, Wm.CANCELMODE, IntPtr.Zero, IntPtr.Zero);
            if (!WaitGone(popup, TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException($"popup 0x{popup:X} did not close after Escape and WM_CANCELMODE.");
            }
        }
    }

    private static bool WaitGone(IntPtr h, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Win32Ext.IsWindow(h) || !Win32.IsWindowVisible(h))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return !Win32Ext.IsWindow(h) || !Win32.IsWindowVisible(h);
    }

    // ---- Main menu ----

    // Walks the menu by its labels (ampersands and accelerator text removed) and returns the menu and position of the last label.
    internal (IntPtr Menu, int Position) Find(params string[] path)
    {
        IntPtr menu = Win32.GetMenu(Main);
        if (menu == IntPtr.Zero)
        {
            throw new InvalidOperationException("regedit has no menu bar.");
        }

        for (int depth = 0; depth < path.Length; depth++)
        {
            int count = Win32.GetMenuItemCount(menu);
            int pos = -1;
            var labels = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string label = LabelAt(menu, i);
                labels.Add(label);
                if (pos < 0 && label == Normalize(path[depth]))
                {
                    pos = i;
                }
            }

            if (pos < 0)
            {
                throw new InvalidOperationException($"menu item '{path[depth]}' not found; items: {string.Join(" | ", labels)}");
            }

            if (depth == path.Length - 1)
            {
                return (menu, pos);
            }

            menu = Win32.GetSubMenu(menu, pos);
            if (menu == IntPtr.Zero)
            {
                throw new InvalidOperationException($"menu item '{path[depth]}' has no submenu.");
            }
        }

        throw new ArgumentException("empty menu path");
    }

    internal static string LabelAt(IntPtr menu, int position)
    {
        var text = new StringBuilder(256);
        Win32.GetMenuStringW(menu, (uint)position, text, text.Capacity, Wm.MF_BYPOSITION);
        return Normalize(text.ToString());
    }

    // Menu label without accelerator text after the tab and without mnemonic ampersands.
    internal static string Normalize(string label)
    {
        int tab = label.IndexOf('\t');
        if (tab >= 0)
        {
            label = label[..tab];
        }

        return label.Replace("&", string.Empty);
    }

    internal IntPtr ChildOfClassOrZero(IntPtr parent, string className) => ChildOfClass(parent, className);

    internal bool IsGrayed(IntPtr menu, int position) => (Win32.GetMenuState(menu, (uint)position, Wm.MF_BYPOSITION) & Wm.MF_GRAYED) != 0;

    // Posts the WM_COMMAND for a menu item. Throws, posting nothing, when the item is grayed: a posted command
    // would run even though the item is disabled, so the state is checked first.
    internal void PostMenuCommand(params string[] path)
    {
        (IntPtr menu, int pos) = Find(path);
        if (IsGrayed(menu, pos))
        {
            throw new InvalidOperationException($"menu item '{string.Join(" > ", path)}' is grayed in this state.");
        }

        uint id = Win32.GetMenuItemID(menu, pos);
        if (id == 0 || id == uint.MaxValue)
        {
            throw new InvalidOperationException($"menu item '{string.Join(" > ", path)}' has no command id.");
        }

        Win32.PostMessageW(Main, Wm.COMMAND, new IntPtr(id), IntPtr.Zero);
    }

    // Opens a top-level menu the way the Alt+letter key does (WM_SYSCOMMAND SC_KEYMENU with the character).
    internal void OpenTopMenu(char accelerator)
    {
        Win32.PostMessageW(Main, Wm.SYSCOMMAND, new IntPtr(Wm.SC_KEYMENU), new IntPtr(char.ToUpperInvariant(accelerator)));
    }

    // Right-clicks a point given in the client coordinates of a control. Classic regedit shows its context menu on button up.
    internal void RightClick(IntPtr control, int x, int y)
    {
        IntPtr lp = new IntPtr((y << 16) | (x & 0xFFFF));
        Win32.PostMessageW(control, Wm.RBUTTONDOWN, new IntPtr(2), lp);
        Win32.PostMessageW(control, Wm.RBUTTONUP, IntPtr.Zero, lp);
    }

    // Finds an item by label in a popup menu (depth first). Returns its command id and grayed state.
    internal static (uint Id, bool Grayed) PopupItemByLabel(IntPtr hmenu, string label)
    {
        (uint Id, bool Grayed)? found = FindItem(hmenu, Normalize(label), 0);
        return found ?? throw new InvalidOperationException($"popup menu item '{label}' not found; items: {DescribeMenu(hmenu)}");
    }

    private static (uint Id, bool Grayed)? FindItem(IntPtr hmenu, string label, int depth)
    {
        int count = Win32.GetMenuItemCount(hmenu);
        for (int i = 0; i < count && depth < 4; i++)
        {
            if (LabelAt(hmenu, i) == label)
            {
                bool grayed = (Win32.GetMenuState(hmenu, (uint)i, Wm.MF_BYPOSITION) & Wm.MF_GRAYED) != 0;
                return (Win32.GetMenuItemID(hmenu, i), grayed);
            }

            IntPtr sub = Win32.GetSubMenu(hmenu, i);
            if (sub != IntPtr.Zero)
            {
                (uint Id, bool Grayed)? inner = FindItem(sub, label, depth + 1);
                if (inner is not null)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    // The menu whose items are drawn in an open popup window. Regedit's context menus come from a
    // one-item root menu (an unlabeled popup), so the drawn items are that popup's submenu.
    internal IntPtr PopupMenuHandle(IntPtr popup)
    {
        IntPtr hmenu = Send(popup, Wm.MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
        if (hmenu == IntPtr.Zero)
        {
            throw new InvalidOperationException($"popup 0x{popup:X} reported no menu handle.");
        }

        if (Win32.GetMenuItemCount(hmenu) == 1)
        {
            IntPtr sub = Win32.GetSubMenu(hmenu, 0);
            if (sub != IntPtr.Zero)
            {
                return sub;
            }
        }

        return hmenu;
    }

    // Labels of a menu and its submenus, for error messages only.
    internal static string DescribeMenu(IntPtr hmenu, int depth = 0)
    {
        var parts = new List<string>();
        int count = Win32.GetMenuItemCount(hmenu);
        for (int i = 0; i < count && depth < 4; i++)
        {
            IntPtr sub = Win32.GetSubMenu(hmenu, i);
            string label = LabelAt(hmenu, i);
            parts.Add(sub == IntPtr.Zero ? label : $"{label}[{DescribeMenu(sub, depth + 1)}]");
        }

        return string.Join(" | ", parts);
    }

    // Titles and classes of every top-level window owned by the process, for timeout messages.
    internal string DescribeWindows()
    {
        var parts = new List<string>();
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)_process.Id)
            {
                var sb = new StringBuilder(256);
                Win32.GetWindowTextW(h, sb, sb.Capacity);
                parts.Add($"0x{h:X} class '{ClassOf(h)}' title '{sb}' visible {Win32.IsWindowVisible(h)} owner 0x{Win32Ext.GetWindow(h, Wm.GW_OWNER):X}");
            }

            return true;
        }, IntPtr.Zero);
        return parts.Count == 0 ? "(no top-level windows)" : string.Join("; ", parts);
    }

    // ---- Controls ----

    internal List<IntPtr> ChildrenOf(IntPtr parent)
    {
        var list = new List<IntPtr>();
        Win32.EnumChildWindows(parent, (h, _) =>
        {
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    internal IntPtr ChildOfClass(IntPtr parent, string className)
    {
        return ChildrenOf(parent).FirstOrDefault(c => ClassOf(c) == className);
    }

    internal IntPtr Tree => ChildOfClass(Main, "SysTreeView32");

    internal IntPtr ListView => ChildOfClass(Main, "SysListView32");

    // The address bar is the Edit control nearest the top of the main window (metrics use the same rule).
    internal IntPtr AddressBar
    {
        get
        {
            IntPtr best = IntPtr.Zero;
            int bestTop = int.MaxValue;
            foreach (IntPtr c in ChildrenOf(Main).Where(c => ClassOf(c) == "Edit"))
            {
                int top = ScreenRectOf(c).Top;
                if (top < bestTop)
                {
                    best = c;
                    bestTop = top;
                }
            }

            return best;
        }
    }

    internal string AddressText()
    {
        var sb = new StringBuilder(1024);
        Win32.GetWindowTextW(AddressBar, sb, sb.Capacity);
        return sb.ToString();
    }

    // The full path of a tree item, root first, built from the item texts ("Computer\HKEY_CURRENT_USER\...").
    // The address bar is not used: classic regedit leaves it empty in the default capture (VIEW-005).
    internal string TreePathOf(IntPtr item)
    {
        var parts = new List<string>();
        for (IntPtr cur = item; cur != IntPtr.Zero; cur = Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_PARENT), cur))
        {
            parts.Add(TreeText(cur));
        }

        parts.Reverse();
        return string.Join(@"\", parts);
    }

    // Moves keyboard focus to the value list with classic regedit's own pane switch command (Tab and F6, command 640),
    // so value commands and context menus act on the list. Focus is read with GetGUIThreadInfo; nothing is clicked.
    internal void FocusList()
    {
        uint thread = Win32.GetWindowThreadProcessId(Main, out _);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (Win32Ext.GetGUIThreadInfo(thread, ref info) && info.hwndFocus == ListView)
            {
                return;
            }

            Win32.PostMessageW(Main, Wm.COMMAND, new IntPtr(PaneSwitchCommand), IntPtr.Zero);
            Thread.Sleep(300);
        }

        throw new InvalidOperationException("keyboard focus did not move to the value list.");
    }

    private const int PaneSwitchCommand = 640;

    internal string SelectedTreeText()
    {
        IntPtr caret = Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_CARET), IntPtr.Zero);
        return caret == IntPtr.Zero ? string.Empty : TreeText(caret);
    }

    internal IntPtr TreeCaret() => Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_CARET), IntPtr.Zero);

    internal List<IntPtr> TreeRoots()
    {
        var roots = new List<IntPtr>();
        IntPtr item = Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_ROOT), IntPtr.Zero);
        while (item != IntPtr.Zero)
        {
            roots.Add(item);
            item = Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_NEXT), item);
        }

        return roots;
    }

    internal IntPtr TreeChild(IntPtr item) => Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_CHILD), item);

    internal IntPtr TreeNext(IntPtr item) => Send(Tree, Msg.TVM_GETNEXTITEM, new IntPtr((int)Wm.TVGN_NEXT), item);

    internal void SelectTreeItem(IntPtr item)
    {
        Send(Tree, Wm.TVM_SELECTITEM, new IntPtr((int)Wm.TVGN_CARET), item);
    }

    internal string TreeText(IntPtr item)
    {
        const int chars = 256;
        using var text = new RemoteBuffer(_process.Handle, chars * 2);
        using var tvi = new RemoteBuffer(_process.Handle, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0, 4), Msg.TVIF_HANDLE | Msg.TVIF_TEXT);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(8, 8), item.ToInt64());
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(24, 8), text.Address.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(32, 4), chars - 1);
        tvi.Write(s);
        Send(Tree, Msg.TVM_GETITEMW, IntPtr.Zero, tvi.Address);
        return ReadWString(text.ReadAll());
    }

    internal NativeRect TreeItemRect(IntPtr item)
    {
        using var buf = new RemoteBuffer(_process.Handle, 16);
        byte[] init = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(init.AsSpan(0, 8), item.ToInt64());
        buf.Write(init);
        IntPtr ok = Send(Tree, Msg.TVM_GETITEMRECT, new IntPtr(1), buf.Address);
        if (ok == IntPtr.Zero)
        {
            throw new InvalidOperationException("TVM_GETITEMRECT failed for the tree item.");
        }

        return ReadRect(buf.ReadAll());
    }

    internal int ListCount() => Send(ListView, Msg.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();

    // Waits until the value list has rows for the key that was selected at launch, then lets the first paint settle.
    internal void WaitForListPopulated(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && ListCount() == 0)
        {
            Thread.Sleep(200);
        }

        if (ListCount() == 0)
        {
            throw new TimeoutException($"the value list stayed empty for {timeout.TotalSeconds}s.");
        }

        Thread.Sleep(800);
    }

    // Asks the window to close (WM_CLOSE) and waits briefly. Dispose kills the process if it is still running.
    internal void CloseGracefully()
    {
        if (_process.HasExited)
        {
            return;
        }

        Win32.PostMessageW(Main, Wm.CLOSE, IntPtr.Zero, IntPtr.Zero);
        _process.WaitForExit(5000);
    }

    internal int SelectedListIndex() => Send(ListView, Wm.LVM_GETNEXTITEM, new IntPtr(-1), new IntPtr(Wm.LVNI_SELECTED)).ToInt32();

    internal List<string> ListNames()
    {
        int count = ListCount();
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            names.Add(ListItemText(i, 0));
        }

        return names;
    }

    internal string ListItemText(int index, int subItem)
    {
        const int chars = 512;
        using var text = new RemoteBuffer(_process.Handle, chars * 2);
        using var lvi = new RemoteBuffer(_process.Handle, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(4, 4), index);
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(8, 4), subItem);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(24, 8), text.Address.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(32, 4), chars - 1);
        lvi.Write(s);
        Send(ListView, Wm.LVM_GETITEMTEXTW, new IntPtr(index), lvi.Address);
        return ReadWString(text.ReadAll());
    }

    // Rectangle of a row in list client coordinates. LVIR_BOUNDS spans every column; LVIR_LABEL is the name column text.
    internal NativeRect ListItemRect(int index, int which = Msg.LVIR_BOUNDS)
    {
        using var buf = new RemoteBuffer(_process.Handle, 16);
        byte[] init = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(init.AsSpan(0, 4), which);
        buf.Write(init);
        Send(ListView, Msg.LVM_GETITEMRECT, new IntPtr(index), buf.Address);
        return ReadRect(buf.ReadAll());
    }

    // Posts a left double click at a client point. A double click on a list row opens the same editor as Modify.
    internal void DoubleClick(IntPtr control, int x, int y)
    {
        IntPtr lp = new IntPtr((y << 16) | (x & 0xFFFF));
        Win32.PostMessageW(control, Wm.LBUTTONDOWN, new IntPtr(Wm.MK_LBUTTON), lp);
        Win32.PostMessageW(control, Wm.LBUTTONUP, IntPtr.Zero, lp);
        Win32.PostMessageW(control, Wm.LBUTTONDBLCLK, new IntPtr(Wm.MK_LBUTTON), lp);
        Win32.PostMessageW(control, Wm.LBUTTONUP, IntPtr.Zero, lp);
    }

    // Like WaitForNewDialog, but returns IntPtr.Zero instead of throwing when no dialog appears.
    internal IntPtr TryWaitForNewDialog(HashSet<IntPtr> existing, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            IntPtr h = OwnedDialogs().FirstOrDefault(d => !existing.Contains(d));
            if (h != IntPtr.Zero)
            {
                return h;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    // Opens the context menu for a control at its selection or caret. The keyboard form of WM_CONTEXTMENU
    // (lParam -1) is tried first: a posted right click takes its hit position from the real cursor, which
    // this tool never moves, so it can open the wrong menu. The right click at the given client point is the fallback.
    internal IntPtr OpenContextMenu(IntPtr control, int clientX, int clientY, HashSet<IntPtr> existing)
    {
        Win32.PostMessageW(control, Wm.CONTEXTMENU, control, new IntPtr(-1));
        IntPtr popup = TryWaitForNewPopup(existing, TimeSpan.FromSeconds(3));
        if (popup != IntPtr.Zero)
        {
            return popup;
        }

        RightClick(control, clientX, clientY);
        return WaitForNewPopup(existing, TimeSpan.FromSeconds(10));
    }

    // Clears the list selection (every row), leaving the focus where it is.
    internal void ClearListSelection()
    {
        using var buf = new RemoteBuffer(_process.Handle, 96);
        byte[] clear = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(0, 4), LvifState);
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(16, 4), Wm.LVIS_SELECTED);
        buf.Write(clear);
        Send(ListView, Wm.LVM_SETITEMSTATE, new IntPtr(-1), buf.Address);
    }

    // Selects one value row and makes it the focused item. Only the list's own selection state changes.
    internal void SelectListRow(int index)
    {
        using var buf = new RemoteBuffer(_process.Handle, 96);
        byte[] clear = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(0, 4), LvifState);
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(16, 4), Wm.LVIS_SELECTED);
        buf.Write(clear);
        Send(ListView, Wm.LVM_SETITEMSTATE, new IntPtr(-1), buf.Address);

        byte[] pick = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(0, 4), LvifState);
        BinaryPrimitives.WriteInt32LittleEndian(pick.AsSpan(4, 4), index);
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(12, 4), Wm.LVIS_SELECTED | Wm.LVIS_FOCUSED);
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(16, 4), Wm.LVIS_SELECTED | Wm.LVIS_FOCUSED);
        buf.Write(pick);
        Send(ListView, Wm.LVM_SETITEMSTATE, new IntPtr(index), buf.Address);
        Send(ListView, Wm.LVM_ENSUREVISIBLE, new IntPtr(index), IntPtr.Zero);
    }

    internal NativeRect ScreenRectOf(IntPtr h)
    {
        if (!Win32.GetWindowRect(h, out NativeRect r))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        }

        return r;
    }

    internal IntPtr Send(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (!Win32.SendMessageTimeoutW(hwnd, msg, w, l, SmtoAbortIfHung, 5000, out IntPtr result))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SendMessageTimeout 0x{msg:X4} to 0x{hwnd:X} failed or timed out.");
        }

        return result;
    }

    internal static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadWString(byte[] utf16)
    {
        string s = Encoding.Unicode.GetString(utf16);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static NativeRect ReadRect(byte[] b) => new()
    {
        Left = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0, 4)),
        Top = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4, 4)),
        Right = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8, 4)),
        Bottom = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12, 4)),
    };
}
