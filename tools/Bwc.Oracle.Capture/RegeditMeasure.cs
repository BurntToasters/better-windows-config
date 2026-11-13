using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// Measures a running regedit main window with Win32 messages only (no UIA).
// Window-level positions are relative to the main window's top-left; control-level
// item rects are in that control's client space, so the output does not depend on screen position.
internal sealed class RegeditMeasure
{
    private readonly IntPtr _window;
    private readonly IntPtr _process;
    private readonly NativeRect _origin;

    internal RegeditMeasure(IntPtr window, IntPtr process)
    {
        if (IntPtr.Size != 8)
        {
            throw new InvalidOperationException("RegeditMeasure writes 64-bit pointer layouts; run the tool as a 64-bit process.");
        }

        _window = window;
        _process = process;
        if (!Win32.GetWindowRect(window, out NativeRect origin))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        }

        _origin = origin;
    }

    internal sealed record ChildInfo(IntPtr Handle, string Class, int Id, string Text, bool Visible, string ParentClass, NativeRect Rect);

    internal List<ChildInfo> Children()
    {
        var list = new List<ChildInfo>();
        Win32.EnumChildWindows(_window, (h, _) =>
        {
            IntPtr parent = Win32.GetParent(h);
            list.Add(new ChildInfo(
                h,
                ClassName(h),
                Win32.GetDlgCtrlID(h),
                WindowText(h),
                Win32.IsWindowVisible(h),
                parent == IntPtr.Zero ? string.Empty : ClassName(parent),
                ScreenRect(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    internal JsonObject MainWindow(out int dpi)
    {
        dpi = (int)Win32.GetDpiForWindow(_window);
        if (!Win32.GetClientRect(_window, out NativeRect client))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetClientRect failed.");
        }

        var clientOrigin = new NativePoint { X = 0, Y = 0 };
        Win32.ClientToScreen(_window, ref clientOrigin);
        var node = new JsonObject
        {
            ["className"] = ClassName(_window),
            ["title"] = WindowText(_window),
            ["dpi"] = dpi,
            ["windowWidth"] = _origin.Width,
            ["windowHeight"] = _origin.Height,
            ["clientWidth"] = client.Width,
            ["clientHeight"] = client.Height,
            ["clientOffsetX"] = clientOrigin.X - _origin.Left,
            ["clientOffsetY"] = clientOrigin.Y - _origin.Top,
        };
        return node;
    }

    internal JsonObject MenuBar()
    {
        IntPtr menu = Win32.GetMenu(_window);
        if (menu == IntPtr.Zero)
        {
            throw new InvalidOperationException("regedit main window has no menu bar handle.");
        }

        var mbi = new MenuBarInfo { CbSize = (uint)Marshal.SizeOf<MenuBarInfo>() };
        if (!Win32.GetMenuBarInfo(_window, Msg.OBJID_MENU, 0, ref mbi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMenuBarInfo(OBJID_MENU) failed.");
        }

        var items = new JsonArray();
        int count = Win32.GetMenuItemCount(menu);
        var text = new StringBuilder(128);
        for (uint i = 0; i < count; i++)
        {
            text.Clear();
            Win32.GetMenuStringW(menu, i, text, text.Capacity, Msg.MF_BYPOSITION);
            Win32.GetMenuItemRect(_window, menu, i, out NativeRect r);
            items.Add(new JsonObject
            {
                ["index"] = (int)i,
                ["text"] = text.ToString(),
                ["rect"] = WindowRect(r),
            });
        }

        return new JsonObject
        {
            ["height"] = mbi.RcBar.Height,
            ["rect"] = WindowRect(mbi.RcBar),
            ["items"] = items,
        };
    }

    internal JsonObject Tree(ChildInfo tree)
    {
        IntPtr h = tree.Handle;
        int itemHeight = Send(h, Msg.TVM_GETITEMHEIGHT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        int indent = Send(h, Msg.TVM_GETINDENT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        int total = Send(h, Msg.TVM_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();

        var first = new JsonArray();
        IntPtr item = Send(h, Msg.TVM_GETNEXTITEM, new IntPtr(5), IntPtr.Zero); // TVGN_FIRSTVISIBLE
        for (int i = 0; i < 6 && item != IntPtr.Zero; i++)
        {
            first.Add(TreeItem(h, item));
            item = Send(h, Msg.TVM_GETNEXTITEM, new IntPtr(6), item); // TVGN_NEXTVISIBLE
        }

        IntPtr caret = Send(h, Msg.TVM_GETNEXTITEM, new IntPtr((int)Msg.TVGN_CARET), IntPtr.Zero);
        return new JsonObject
        {
            ["className"] = tree.Class,
            ["rect"] = WindowRect(tree.Rect),
            ["font"] = FontOf(h),
            ["itemHeight"] = itemHeight,
            ["indent"] = indent,
            ["totalItems"] = total,
            ["selectedItem"] = caret == IntPtr.Zero ? null : TreeItemText(h, caret),
            ["firstVisibleItems"] = first,
        };
    }

    internal JsonObject ListView(ChildInfo list)
    {
        IntPtr h = list.Handle;
        IntPtr header = Send(h, Msg.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        NativeRect headerScreen = ScreenRect(header);
        NativePoint clientOrigin = new();
        Win32.ClientToScreen(h, ref clientOrigin);

        int count = Send(h, Msg.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        int top = Send(h, Msg.LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero).ToInt32();

        var rows = new JsonArray();
        var bounds = new List<NativeRect>();
        for (int i = 0; i < Math.Min(6, count); i++)
        {
            NativeRect b = ListItemRect(h, i, Msg.LVIR_BOUNDS);
            bounds.Add(b);
            rows.Add(new JsonObject
            {
                ["index"] = i,
                ["name"] = ListItemText(h, i, 0),
                ["bounds"] = ClientRect(b),
                ["label"] = ClientRect(ListItemRect(h, i, Msg.LVIR_LABEL)),
                ["icon"] = ClientRect(ListItemRect(h, i, Msg.LVIR_ICON)),
            });
        }

        int rowHeight = bounds.Count >= 2 ? bounds[1].Top - bounds[0].Top : 0;

        var columns = new JsonArray();
        int colCount = Send(header, 0x1200, IntPtr.Zero, IntPtr.Zero).ToInt32(); // HDM_GETITEMCOUNT
        for (int c = 0; c < colCount; c++)
        {
            columns.Add(new JsonObject
            {
                ["index"] = c,
                ["text"] = ListColumnText(h, c),
                ["width"] = Send(h, Msg.LVM_GETCOLUMNWIDTH, new IntPtr(c), IntPtr.Zero).ToInt32(),
            });
        }

        return new JsonObject
        {
            ["className"] = list.Class,
            ["rect"] = WindowRect(list.Rect),
            ["font"] = FontOf(h),
            ["rowHeight"] = rowHeight,
            ["itemCount"] = count,
            ["topIndex"] = top,
            ["header"] = new JsonObject
            {
                ["className"] = ClassName(header),
                ["height"] = headerScreen.Height,
                ["rect"] = new JsonObject
                {
                    ["x"] = headerScreen.Left - clientOrigin.X,
                    ["y"] = headerScreen.Top - clientOrigin.Y,
                    ["width"] = headerScreen.Width,
                    ["height"] = headerScreen.Height,
                },
                ["columns"] = columns,
            },
            ["firstRows"] = rows,
        };
    }

    internal JsonObject StatusBar(ChildInfo status)
    {
        IntPtr h = status.Handle;
        string text = StatusText(h, 0);
        int partCount = Send(h, Msg.SB_GETPARTS, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var parts = new JsonArray();
        for (int p = 0; p < partCount; p++)
        {
            using var rect = new RemoteBuffer(_process, 16);
            Send(h, Msg.SB_GETRECT, new IntPtr(p), rect.Address);
            parts.Add(ClientRect(ReadRect(rect.ReadAll())));
        }

        return new JsonObject
        {
            ["className"] = status.Class,
            ["rect"] = WindowRect(status.Rect),
            ["height"] = status.Rect.Height,
            ["text"] = text,
            ["parts"] = parts,
        };
    }

    internal JsonObject SystemMetrics()
    {
        var node = new JsonObject();
        (string Name, int Index)[] items =
        {
            ("SM_CYCAPTION", Msg.SM_CYCAPTION),
            ("SM_CYMENU", Msg.SM_CYMENU),
            ("SM_CXVSCROLL", Msg.SM_CXVSCROLL),
            ("SM_CYHSCROLL", Msg.SM_CYHSCROLL),
            ("SM_CXEDGE", Msg.SM_CXEDGE),
            ("SM_CYEDGE", Msg.SM_CYEDGE),
            ("SM_CXSMICON", Msg.SM_CXSMICON),
        };
        foreach ((string name, int index) in items)
        {
            node[name] = Win32.GetSystemMetrics(index);
        }

        return node;
    }

    internal static JsonObject NonClientFonts()
    {
        var ncm = new NonClientMetricsW { CbSize = (uint)Marshal.SizeOf<NonClientMetricsW>() };
        if (!Win32.SystemParametersInfoW(Msg.SPI_GETNONCLIENTMETRICS, ncm.CbSize, ref ncm, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETNONCLIENTMETRICS failed.");
        }

        return new JsonObject
        {
            ["menu"] = new JsonObject { ["font"] = FontJson(ncm.MenuFont), ["height"] = ncm.MenuHeight },
            ["status"] = new JsonObject { ["font"] = FontJson(ncm.StatusFont) },
            ["caption"] = new JsonObject { ["font"] = FontJson(ncm.CaptionFont), ["height"] = ncm.CaptionHeight },
            ["smallCaption"] = new JsonObject { ["font"] = FontJson(ncm.SmCaptionFont), ["height"] = ncm.SmCaptionHeight },
            ["message"] = new JsonObject { ["font"] = FontJson(ncm.MessageFont) },
            ["borderWidth"] = ncm.BorderWidth,
            ["paddedBorderWidth"] = ncm.PaddedBorderWidth,
        };
    }

    internal static JsonObject FontJson(LogFontW f) => new()
    {
        ["faceName"] = f.FaceName,
        ["height"] = f.Height,
        ["weight"] = f.Weight,
        ["italic"] = f.Italic != 0,
        ["charSet"] = f.CharSetId,
        ["quality"] = f.Quality,
        ["pitchAndFamily"] = f.PitchAndFamily,
    };

    // Reads the font that a control reports via WM_GETFONT, then GetObject(LOGFONTW) on that handle.
    private JsonObject? FontOf(IntPtr control)
    {
        IntPtr hfont = Send(control, Msg.WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        if (hfont == IntPtr.Zero)
        {
            return null;
        }

        int n = Win32.GetObjectW(hfont, Marshal.SizeOf<LogFontW>(), out LogFontW lf);
        if (n == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetObjectW on control font 0x{hfont:X} failed.");
        }

        return FontJson(lf);
    }

    private JsonObject TreeItem(IntPtr tree, IntPtr item)
    {
        return new JsonObject
        {
            ["text"] = TreeItemText(tree, item),
            ["rowRect"] = TreeItemRect(tree, item, textOnly: false),
            ["textRect"] = TreeItemRect(tree, item, textOnly: true),
        };
    }

    private JsonObject? TreeItemRect(IntPtr tree, IntPtr item, bool textOnly)
    {
        using var buf = new RemoteBuffer(_process, 16);
        byte[] init = new byte[16];
        // HTREEITEM is pointer-sized on 64-bit; comctl32 reads the full handle from the first 8 bytes of the RECT.
        BinaryPrimitives.WriteInt64LittleEndian(init.AsSpan(0, 8), item.ToInt64());
        buf.Write(init);
        IntPtr ok = Send(tree, Msg.TVM_GETITEMRECT, new IntPtr(textOnly ? 1 : 0), buf.Address);
        return ok == IntPtr.Zero ? null : ClientRect(ReadRect(buf.ReadAll()));
    }

    private string TreeItemText(IntPtr tree, IntPtr item)
    {
        const int chars = 256;
        using var text = new RemoteBuffer(_process, chars * 2);
        using var tvi = new RemoteBuffer(_process, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0, 4), Msg.TVIF_HANDLE | Msg.TVIF_TEXT); // mask
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(8, 8), item.ToInt64());                  // hItem
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(24, 8), text.Address.ToInt64());         // pszText
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(32, 4), chars - 1);                      // cchTextMax
        tvi.Write(s);
        Send(tree, Msg.TVM_GETITEMW, IntPtr.Zero, tvi.Address);
        return ReadWString(text.ReadAll());
    }

    private NativeRect ListItemRect(IntPtr list, int index, int which)
    {
        using var buf = new RemoteBuffer(_process, 16);
        byte[] init = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(init.AsSpan(0, 4), which); // LVIR_* goes in RECT.left
        buf.Write(init);
        Send(list, Msg.LVM_GETITEMRECT, new IntPtr(index), buf.Address);
        return ReadRect(buf.ReadAll());
    }

    private string ListItemText(IntPtr list, int index, int subItem)
    {
        const int chars = 256;
        using var text = new RemoteBuffer(_process, chars * 2);
        using var lvi = new RemoteBuffer(_process, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(4, 4), index);      // iItem
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(8, 4), subItem);    // iSubItem
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(24, 8), text.Address.ToInt64()); // pszText
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(32, 4), chars - 1); // cchTextMax
        lvi.Write(s);
        Send(list, Msg.LVM_GETITEMTEXTW, new IntPtr(index), lvi.Address);
        return ReadWString(text.ReadAll());
    }

    private string ListColumnText(IntPtr list, int column)
    {
        const int chars = 256;
        using var text = new RemoteBuffer(_process, chars * 2);
        using var lvc = new RemoteBuffer(_process, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0, 4), Msg.LVCF_TEXT);            // mask
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(16, 8), text.Address.ToInt64());   // pszText
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(24, 4), chars - 1);                // cchTextMax
        lvc.Write(s);
        Send(list, Msg.LVM_GETCOLUMNW, new IntPtr(column), lvc.Address);
        return ReadWString(text.ReadAll());
    }

    private string StatusText(IntPtr status, int part)
    {
        const int chars = 512;
        using var text = new RemoteBuffer(_process, chars * 2);
        IntPtr r = Send(status, Msg.SB_GETTEXTW, new IntPtr(part), text.Address);
        int length = (int)(r.ToInt64() & 0xFFFF);
        byte[] raw = text.ReadAll();
        return length == 0 ? string.Empty : ReadWString(raw);
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

    private static IntPtr Send(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (!Win32.SendMessageTimeoutW(hwnd, msg, w, l, Msg.SMTO_ABORTIFHUNG, 5000, out IntPtr result))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SendMessageTimeout 0x{msg:X4} to 0x{hwnd:X} failed or timed out.");
        }

        return result;
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(512);
        Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static NativeRect ScreenRect(IntPtr h)
    {
        if (!Win32.GetWindowRect(h, out NativeRect r))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        }

        return r;
    }

    // Screen rect expressed relative to the main window's top-left corner.
    private JsonObject WindowRect(NativeRect screen) => ClientRect(ShiftBy(screen, -_origin.Left, -_origin.Top));

    private static NativeRect ShiftBy(NativeRect r, int dx, int dy) => new()
    {
        Left = r.Left + dx,
        Top = r.Top + dy,
        Right = r.Right + dx,
        Bottom = r.Bottom + dy,
    };

    private static JsonObject ClientRect(NativeRect r) => new()
    {
        ["x"] = r.Left,
        ["y"] = r.Top,
        ["width"] = r.Width,
        ["height"] = r.Height,
    };
}
