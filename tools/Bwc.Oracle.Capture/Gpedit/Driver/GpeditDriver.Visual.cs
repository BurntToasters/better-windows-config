using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Read-only layout operations for matrix group F (readLayout, dialogLayout, pressKey). Added for the matrix runner; nothing here writes
// to classic except key presses in pressKey, which are limited to the names in KeySpecs. Failure modes 18 of the matrix document.
internal sealed partial class GpeditDriver
{
    private const uint TbButtonCount = 0x0418;
    private const uint TbGetButton = 0x0417;
    private const uint TbGetItemRect = 0x041D;
    private const uint TbGetButtonTextW = 0x044B;
    private const uint TvmGetNextItem = 0x110A;
    private const uint TvmGetItemW = 0x113E;
    private const int TvgnRoot = 0, TvgnNext = 1, TvgnChild = 4;
    private const uint TvifText = 0x1, TvifImage = 0x2, TvifState = 0x8, TvifHandle = 0x10, TvifSelectedImage = 0x20, TvifChildren = 0x40;

    // Key names a pressKey operation may use. Nothing that saves, prints, deletes, renames or opens another program (F1, Ctrl+S, F2, Delete).
    internal static readonly Dictionary<string, (ushort[] Modifiers, ushort Key)> KeySpecs = new(StringComparer.Ordinal)
    {
        ["Up"] = (Array.Empty<ushort>(), 0x26),
        ["Down"] = (Array.Empty<ushort>(), 0x28),
        ["Left"] = (Array.Empty<ushort>(), 0x25),
        ["Right"] = (Array.Empty<ushort>(), 0x27),
        ["Home"] = (Array.Empty<ushort>(), 0x24),
        ["End"] = (Array.Empty<ushort>(), 0x23),
        ["PageUp"] = (Array.Empty<ushort>(), 0x21),
        ["PageDown"] = (Array.Empty<ushort>(), 0x22),
        ["Enter"] = (Array.Empty<ushort>(), 0x0D),
        ["Space"] = (Array.Empty<ushort>(), 0x20),
        ["Tab"] = (Array.Empty<ushort>(), 0x09),
        ["Shift+Tab"] = (new ushort[] { 0x10 }, 0x09),
        ["Esc"] = (Array.Empty<ushort>(), 0x1B),
        ["Backspace"] = (Array.Empty<ushort>(), 0x08),
        ["F5"] = (Array.Empty<ushort>(), 0x74),
        ["F6"] = (Array.Empty<ushort>(), 0x75),
        ["Apps"] = (Array.Empty<ushort>(), 0x5D),
        ["Plus"] = (Array.Empty<ushort>(), 0x6B),
        ["Minus"] = (Array.Empty<ushort>(), 0x6D),
        ["Asterisk"] = (Array.Empty<ushort>(), 0x6A),
        ["Alt+Left"] = (new ushort[] { 0x12 }, 0x25),
        ["Alt+Right"] = (new ushort[] { 0x12 }, 0x27),
        ["Alt+Up"] = (new ushort[] { 0x12 }, 0x26),
        ["Ctrl+Tab"] = (new ushort[] { 0x11 }, 0x09),
        ["Ctrl+A"] = (new ushort[] { 0x11 }, 0x41),
    };

    private static JsonObject RectRelative(System.Windows.Rect rect, NativeRect origin)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.X))
        {
            return new JsonObject { ["x"] = null, ["y"] = null, ["width"] = null, ["height"] = null };
        }

        return new JsonObject { ["x"] = (int)rect.X - origin.Left, ["y"] = (int)rect.Y - origin.Top, ["width"] = (int)rect.Width, ["height"] = (int)rect.Height };
    }

    private static JsonObject RectRelative(NativeRect rect, NativeRect origin) =>
        new() { ["x"] = rect.Left - origin.Left, ["y"] = rect.Top - origin.Top, ["width"] = rect.Width, ["height"] = rect.Height };

    // Main window size, toolbar buttons (native toolbar messages and UI Automation), tree nodes with image indexes, list header widths.
    internal JsonObject ReadLayout()
    {
        ThrowIfDead();
        ProbeInput.Foreground(main);
        Win32.GetWindowRect(main, out NativeRect mainRect);
        var result = new JsonObject
        {
            ["window"] = new JsonObject { ["width"] = mainRect.Width, ["height"] = mainRect.Height },
            ["perMonitorV2"] = GpeditDpi.IsPerMonitorV2,
        };

        var toolbars = new JsonArray();
        foreach (WinInfo bar in ProbeWindows.Children(main).Where(c => c.Class == "ToolbarWindow32"))
        {
            toolbars.Add(ReadNativeToolbar(bar, mainRect));
        }

        result["toolbars"] = toolbars;
        var uiaBars = new JsonArray();
        string route = UiaScope.Route(VisibleListRows());
        result["uiaRoute"] = route;
        try
        {
            // The search over the main window walks every row of a big list (failure mode 40): from LargeListRows rows the toolbar windows are
            // taken by their handles instead.
            IEnumerable<AutomationElement> bars = route == UiaScope.RouteNarrow
                ? ElementsOfWindows(UiaScope.ToolbarClass, ControlType.ToolBar)
                : ProbeUia.OfType(Main(), ControlType.ToolBar);
            foreach (AutomationElement bar in bars)
            {
                var buttons = new JsonArray();
                foreach (AutomationElement button in ProbeUia.OfType(bar, ControlType.Button))
                {
                    JsonObject node = RectRelative(button.Current.BoundingRectangle, mainRect);
                    node["name"] = ProbeUia.NameOf(button);
                    node["enabled"] = button.Current.IsEnabled;
                    buttons.Add(node);
                }

                uiaBars.Add(new JsonObject { ["class"] = bar.Current.ClassName, ["buttons"] = buttons });
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            uiaBars.Add(new JsonObject { ["error"] = ex.Message });
        }

        result["uiaToolbars"] = uiaBars;
        result["tree"] = ReadTreeImages(mainRect);

        var headers = new JsonArray();
        ListCandidate? list = VisibleList();
        if (list is not null)
        {
            foreach (AutomationElement header in HeaderItems(list.List, list.Rows))
            {
                JsonObject node = RectRelative(header.Current.BoundingRectangle, mainRect);
                node["name"] = ProbeUia.NameOf(header).Trim();
                headers.Add(node);
            }

            result["list"] = RectRelative(list.Rect, mainRect);
        }

        result["headers"] = headers;
        Win32.GetWindowRect(FindTreeWindow(), out NativeRect treeRect);
        result["treeWindow"] = RectRelative(treeRect, mainRect);
        foreach (WinInfo bar in ProbeWindows.Children(main).Where(c => c.Class == "msctls_statusbar32" && c.Visible))
        {
            result["statusBar"] = RectRelative(bar.Rect, mainRect);
        }

        return result;
    }

    private JsonObject ReadNativeToolbar(WinInfo bar, NativeRect origin)
    {
        var buttons = new JsonArray();
        var node = new JsonObject { ["visible"] = bar.Visible, ["rect"] = RectRelative(bar.Rect, origin), ["buttons"] = buttons };
        try
        {
            int count = DriverNative.Send(bar.Hwnd, TbButtonCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
            node["count"] = count;
            for (int i = 0; i < Math.Min(count, 80); i++)
            {
                using var item = new RemoteBuffer(mmc.Handle, 64);
                IntPtr ok = DriverNative.Send(bar.Hwnd, TbGetButton, new IntPtr(i), item.Address);
                if (ok == IntPtr.Zero)
                {
                    buttons.Add(new JsonObject { ["index"] = i, ["error"] = "TB_GETBUTTON failed" });
                    continue;
                }

                byte[] raw = item.Read(32);
                int command = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4, 4));
                byte state = raw[8];
                byte style = raw[9];
                var button = new JsonObject
                {
                    ["index"] = i,
                    ["command"] = command,
                    ["separator"] = (style & 0x1) != 0,
                    ["enabled"] = (state & 0x4) != 0,
                    ["checked"] = (state & 0x1) != 0,
                    ["hidden"] = (state & 0x8) != 0,
                    ["style"] = style,
                };
                using var rect = new RemoteBuffer(mmc.Handle, 16);
                if (DriverNative.Send(bar.Hwnd, TbGetItemRect, new IntPtr(i), rect.Address) != IntPtr.Zero)
                {
                    byte[] r = rect.Read(16);
                    button["x"] = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(0, 4));
                    button["y"] = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(4, 4));
                    button["width"] = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(8, 4)) - BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(0, 4));
                    button["height"] = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(12, 4)) - BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(4, 4));
                }

                if ((style & 0x1) == 0)
                {
                    using var text = new RemoteBuffer(mmc.Handle, 512);
                    DriverNative.Send(bar.Hwnd, TbGetButtonTextW, new IntPtr(command), text.Address);
                    string value = Encoding.Unicode.GetString(text.ReadAll());
                    int nul = value.IndexOf('\0');
                    button["text"] = nul >= 0 ? value[..nul] : value;
                }

                buttons.Add(button);
            }
        }
        catch (Exception ex) when (ex is DriverException or System.ComponentModel.Win32Exception)
        {
            node["error"] = ex.Message;
        }

        return node;
    }

    // Every node that exists in the tree (depth first, at most 600), with its image index, selected image index, child flag and expanded flag.
    private JsonArray ReadTreeImages(NativeRect mainRect)
    {
        var nodes = new JsonArray();
        IntPtr tree = FindTreeWindow();
        if (tree == main)
        {
            nodes.Add(new JsonObject { ["error"] = "no tree window" });
            return nodes;
        }

        try
        {
            IntPtr first = DriverNative.Send(tree, TvmGetNextItem, new IntPtr(TvgnRoot), IntPtr.Zero);
            int budget = 600;
            WalkTree(tree, first, 0, nodes, ref budget);
            if (budget <= 0)
            {
                nodes.Add(new JsonObject { ["truncated"] = true });
            }
        }
        catch (Exception ex) when (ex is DriverException or System.ComponentModel.Win32Exception)
        {
            nodes.Add(new JsonObject { ["error"] = ex.Message });
        }

        return nodes;
    }

    private void WalkTree(IntPtr tree, IntPtr item, int depth, JsonArray into, ref int budget)
    {
        while (item != IntPtr.Zero && budget > 0)
        {
            budget--;
            using var tvi = new RemoteBuffer(mmc.Handle, 64);
            using var text = new RemoteBuffer(mmc.Handle, 1024);
            byte[] raw = new byte[64];
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0, 4), TvifHandle | TvifText | TvifImage | TvifSelectedImage | TvifChildren | TvifState);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(8, 8), item.ToInt64());
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(20, 4), 0x20 | 0x2 | 0x4000); // TVIS_EXPANDED, TVIS_SELECTED, state image mask
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(24, 8), text.Address.ToInt64());
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(32, 4), 512);
            tvi.Write(raw);
            DriverNative.Send(tree, TvmGetItemW, IntPtr.Zero, tvi.Address);
            byte[] done = tvi.Read(56);
            string name = Encoding.Unicode.GetString(text.ReadAll());
            int nul = name.IndexOf('\0');
            name = nul >= 0 ? name[..nul] : name;
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(done.AsSpan(16, 4));
            into.Add(new JsonObject
            {
                ["depth"] = depth,
                ["name"] = name,
                ["image"] = BinaryPrimitives.ReadInt32LittleEndian(done.AsSpan(36, 4)),
                ["selectedImage"] = BinaryPrimitives.ReadInt32LittleEndian(done.AsSpan(40, 4)),
                ["hasChildren"] = BinaryPrimitives.ReadInt32LittleEndian(done.AsSpan(44, 4)) != 0,
                ["expanded"] = (state & 0x20) != 0,
                ["selected"] = (state & 0x2) != 0,
            });
            IntPtr child = DriverNative.Send(tree, TvmGetNextItem, new IntPtr(TvgnChild), item);
            if (child != IntPtr.Zero)
            {
                WalkTree(tree, child, depth + 1, into, ref budget);
            }

            item = DriverNative.Send(tree, TvmGetNextItem, new IntPtr(TvgnNext), item);
        }
    }

    // Opens the policy dialog (state set first when given), reads every child window of the dialog in Z order (which is the order of
    // enumeration, top first) with class, automation id, rectangle relative to the dialog, enabled and tab stop, then cancels.
    internal JsonObject DialogLayout(string policy, int? occurrence, string? state)
    {
        PolicyDialog dialog = OpenPolicy(policy, occurrence);
        var result = new JsonObject { ["policy"] = policy };
        try
        {
            if (state is not null)
            {
                result["state"] = dialog.SetState(state)["observed"]?.DeepClone();
                Thread.Sleep(300);
            }

            Win32.GetWindowRect(dialog.Hwnd, out NativeRect rect);
            result["dialog"] = new JsonObject { ["width"] = rect.Width, ["height"] = rect.Height };
            var controls = new JsonArray();
            int order = 0;
            foreach (WinInfo child in ProbeWindows.Children(dialog.Hwnd))
            {
                uint style = DriverNative.Style(child.Hwnd);
                JsonObject node = RectRelative(child.Rect, rect);
                node["order"] = order++;
                node["class"] = child.Class;
                node["id"] = GpeditDriver.IdOf(child.Hwnd);
                node["dlgCtrlId"] = Win32.GetDlgCtrlID(child.Hwnd);
                node["visible"] = child.Visible;
                node["enabled"] = Win32Ext.IsWindowEnabled(child.Hwnd);
                node["tabStop"] = (style & 0x10000) != 0;
                node["text"] = child.Title.Length > 60 ? child.Title[..60] : child.Title;
                node["parentIsDialog"] = Win32.GetParent(child.Hwnd) == dialog.Hwnd;
                controls.Add(node);
            }

            result["controls"] = controls;
            result["title"] = ProbeWindows.TitleOf(dialog.Hwnd);
            return result;
        }
        finally
        {
            WinInfo? info = ProbeWindows.TopLevel(Pid).FirstOrDefault(w => w.Hwnd == dialog.Hwnd);
            if (info is not null)
            {
                result["closedBy"] = CancelWindow(info);
            }
        }
    }

    // Presses named keys one at a time after selecting the node, and (for target "list") a policy row. After every key: the selected tree
    // path, windows that opened (captured, then closed), popup menus (closed), the focused element, and the list row count.
    internal JsonObject PressKeys(IReadOnlyList<string> keys, string? target, string? policy, int? occurrence)
    {
        ThrowIfDead();
        EnsureNoDialogs();
        CloseMenus();
        ProbeInput.Foreground(main);
        AutomationElement focusElement;
        if (string.Equals(target, "list", StringComparison.Ordinal))
        {
            ListCandidate list = VisibleList() ?? throw new DriverException("no visible result list. " + DescribeLists());
            if (policy is not null)
            {
                AutomationElement row = FindPolicyRow(policy, occurrence);
                if (!ProbeUia.Select(row))
                {
                    ProbeUia.Click(row, doubleClick: false);
                }
            }

            focusElement = list.List;
        }
        else
        {
            focusElement = AutomationElement.FromHandle(FindTreeWindow());
        }

        ProbeUia.Focus(focusElement);
        Thread.Sleep(300);
        var steps = new JsonArray();
        foreach (string key in keys)
        {
            (ushort[] modifiers, ushort vk) = KeySpecs[key];
            string? before = SelectedPath() is { } pathBefore ? string.Join("/", pathBefore) : null;
            var known = Dialogs().Select(w => w.Hwnd).ToHashSet();
            DriverInput.RequireForeground(main, Pid);
            DriverInput.Chord(modifiers, vk);
            Thread.Sleep(900);
            var step = new JsonObject
            {
                ["key"] = key,
                ["selectedBefore"] = before,
                ["selectedAfter"] = SelectedPath() is { } pathAfter ? string.Join("/", pathAfter) : null,
            };
            var windows = new JsonArray();
            foreach (WinInfo window in NewDialogs(known))
            {
                DialogKind kind = Classify(window);
                windows.Add(new JsonObject { ["kind"] = kind.ToString(), ["title"] = window.Title });
            }

            step["newWindows"] = windows;
            step["popupMenus"] = ProbeWindows.PopupMenus(Pid).Count;
            try
            {
                AutomationElement? focused = AutomationElement.FocusedElement;
                step["focusedClass"] = focused?.Current.ClassName;
                step["focusedName"] = focused is null ? null : ProbeUia.NameOf(focused);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                step["focusedClass"] = null;
            }

            JsonArray closed = EnsureNoDialogs();
            step["dialogsClosed"] = closed.Count;
            step["menusClosed"] = CloseMenus();
            steps.Add(step);
        }

        return new JsonObject { ["target"] = target ?? "tree", ["steps"] = steps };
    }
}
