using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `menus`: opens the five menu bar popups (File, Edit, View, Favorites, Help) and the tree-key and
// list-value context menus, then measures each popup. Item geometry comes from GetMenuItemRect on the
// popup's own HMENU. Gutters (check mark, submenu arrow, text inset) are measured from screen pixels of
// the open popup, so they reflect what is drawn. Nothing is chosen from the menu; each popup is closed
// with Escape. Tracked output holds numbers and flags only. Item text goes to the untracked local/ folder.
internal static class MenuCapture
{
    private const string MixedLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Mixed";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    // Pixels whose color differs from the popup background by more than this sum of channel differences are ink.
    private const int InkThreshold = 90;

    private enum Kind
    {
        MenuBar,
        TreeContext,
        ListBackground,
    }

    private sealed record Spec(string Name, Kind Kind, char Accelerator = '\0');

    private static readonly Spec[] Specs =
    {
        new("file", Kind.MenuBar, 'F'),
        new("edit", Kind.MenuBar, 'E'),
        new("view", Kind.MenuBar, 'V'),
        new("favorites", Kind.MenuBar, 'A'),
        new("help", Kind.MenuBar, 'H'),
        new("tree-key-context", Kind.TreeContext),
        new("list-background-context", Kind.ListBackground),
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        var menus = new List<(string Name, JsonObject Json)>();
        var text = new JsonObject();
        var notCaptured = new JsonArray();
        uint windowDpi = 0;

        foreach (Spec spec in Specs)
        {
            try
            {
                (JsonObject json, JsonObject textJson, uint dpi) = CaptureOne(spec);
                menus.Add((spec.Name, json));
                text[spec.Name] = textJson;
                windowDpi = dpi;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"menus: {spec.Name} not captured: {ex.Message}");
                notCaptured.Add(new JsonObject { ["name"] = spec.Name, ["reason"] = Report.StableReason(ex.Message) });
            }
        }

        if (windowDpi == 0)
        {
            windowDpi = systemDpi;
        }

        notCaptured.Add(new JsonObject
        {
            ["name"] = "list-value-context",
            ["reason"] = "The value context menu (Modify, Modify Binary Data, Delete, Rename) did not open by message: the list showed the background menu even with the value selected and the list focused. Not captured.",
        });
        notCaptured.Add(new JsonObject
        {
            ["name"] = "remote-root-context",
            ["reason"] = "Needs a remote registry connection; not opened without network access.",
        });
        notCaptured.Add(new JsonObject
        {
            ["name"] = "text-edit-context",
            ["reason"] = "Attaches to an edit control that is only shown while a dialog or inline edit is open; not captured.",
        });

        var array = new JsonArray(menus.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (JsonNode)m.Json).ToArray());
        Report.WriteJson(paths.MenuMetricsJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture menus",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["note"] = "Positions are device pixels relative to the popup window's top-left corner. Gutters are measured from pixels of the open popup. Item text is untracked in docs/parity/oracle/local/menu-text.json.",
            ["menus"] = array,
            ["notCaptured"] = notCaptured,
        });

        Report.WriteJson(Path.Combine(paths.LocalDir, "menu-text.json"), text);
    }

    private static (JsonObject Json, JsonObject Text, uint Dpi) CaptureOne(Spec spec)
    {
        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(MixedLastKey));
        using RegeditWindow w = RegeditWindow.Start();
        var existing = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.PopupClass));

        IntPtr popup;
        switch (spec.Kind)
        {
            case Kind.MenuBar:
                w.OpenTopMenu(spec.Accelerator);
                popup = w.WaitForNewPopup(existing, OpenTimeout);
                break;

            case Kind.TreeContext:
            {
                // The tree's own right-click area is not reliable from its text rectangle, so the
                // keyboard form of the context menu request is used directly on the caret item.
                Win32.PostMessageW(w.Tree, Wm.CONTEXTMENU, w.Tree, new IntPtr(-1));
                popup = w.WaitForNewPopup(existing, OpenTimeout);
                break;
            }

            case Kind.ListBackground:
            {
                // Empty space below the last row, with no row selected, is the list background.
                w.WaitForListPopulated(OpenTimeout);
                w.ClearListSelection();
                w.FocusList();
                int rows = w.ListCount();
                NativeRect last = w.ListItemRect(rows - 1);
                popup = w.OpenContextMenu(w.ListView, 40, last.Bottom + 40, existing);
                break;
            }

            default:
                throw new InvalidOperationException($"unknown kind {spec.Kind}");
        }

        Thread.Sleep(600);
        try
        {
            return Measure(spec, w, popup);
        }
        finally
        {
            w.ClosePopup(popup);
        }
    }

    private static (JsonObject Json, JsonObject Text, uint Dpi) Measure(Spec spec, RegeditWindow w, IntPtr popup)
    {
        if (!Win32.GetWindowRect(popup, out NativeRect pr))
        {
            throw new InvalidOperationException("GetWindowRect failed for the popup.");
        }

        uint dpi = Win32.GetDpiForWindow(w.Main);
        IntPtr hmenu = w.PopupMenuHandle(popup);
        int count = Win32.GetMenuItemCount(hmenu);

        var rows = new List<Row>(count);
        for (int i = 0; i < count; i++)
        {
            uint flags = Win32.GetMenuState(hmenu, (uint)i, Wm.MF_BYPOSITION);
            bool separator = (flags & Wm.MF_SEPARATOR) != 0;
            // GetMenuState does not report MF_POPUP for items of extended menus, so ask for the submenu handle.
            bool hasSub = Win32.GetSubMenu(hmenu, i) != IntPtr.Zero;
            bool grayed = (flags & Wm.MF_GRAYED) != 0;
            bool checkedItem = (flags & Wm.MF_CHECKED) != 0;
            NativeRect r = new();
            Win32.GetMenuItemRect(w.Main, hmenu, (uint)i, out r);
            rows.Add(new Row(i, RegeditWindow.LabelAt(hmenu, i), separator, hasSub, grayed, checkedItem,
                r.Left - pr.Left, r.Top - pr.Top, r.Width, r.Height));
        }

        int width = pr.Width;
        int height = pr.Height;
        Ink ink = MeasureInk(pr, rows);

        var items = new JsonArray();
        foreach (Row row in rows)
        {
            var item = new JsonObject
            {
                ["index"] = row.Index,
                ["separator"] = row.Separator,
                ["submenu"] = row.HasSub,
                ["checked"] = row.Checked,
                ["grayed"] = row.Grayed,
                ["x"] = row.X,
                ["y"] = row.Y,
                ["width"] = row.Width,
                ["height"] = row.Height,
            };
            items.Add(item);
        }

        var metrics = Summarize(rows, ink, width, height);

        var json = new JsonObject
        {
            ["name"] = spec.Name,
            ["kind"] = spec.Kind == Kind.MenuBar ? "menubar" : "context",
            ["popupWidth"] = width,
            ["popupHeight"] = height,
            ["itemCount"] = count,
            ["metrics"] = metrics,
            ["items"] = items,
        };

        var textItems = new JsonArray();
        foreach (Row row in rows)
        {
            textItems.Add(new JsonObject { ["index"] = row.Index, ["text"] = row.Label });
        }

        var textJson = new JsonObject { ["items"] = textItems };
        return (json, textJson, dpi);
    }

    private sealed record Row(int Index, string Label, bool Separator, bool HasSub, bool Grayed, bool Checked, int X, int Y, int Width, int Height);

    private sealed record Ink(int CheckLeft, int TextLeft, int ArrowRightGutter, bool HasChecked, bool HasSub);

    // Scans the drawn popup pixels. The background is the color at a fixed inset from the top-left corner,
    // which lies in the popup padding for all of these menus.
    private static Ink MeasureInk(NativeRect pr, List<Row> rows)
    {
        int width = pr.Width;
        int height = pr.Height;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(pr.Left, pr.Top, 0, 0, new Size(width, height));
        }

        Color background = bitmap.GetPixel(2, 2);

        int checkLeft = int.MaxValue;
        int textLeft = int.MaxValue;
        int arrowGutter = int.MaxValue;
        bool hasChecked = false;
        bool hasSub = false;

        foreach (Row row in rows)
        {
            if (row.Separator || row.Height <= 4)
            {
                continue;
            }

            int top = Math.Clamp(row.Y + 2, 0, height - 1);
            int bottom = Math.Clamp(row.Y + row.Height - 3, 0, height - 1);
            int left = Math.Clamp(row.X, 0, width - 1);
            int right = Math.Clamp(row.X + row.Width - 1, 0, width - 1);

            int rowMin = int.MaxValue;
            int rowMax = int.MinValue;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    if (IsInk(bitmap.GetPixel(x, y), background))
                    {
                        rowMin = Math.Min(rowMin, x);
                        rowMax = Math.Max(rowMax, x);
                    }
                }
            }

            if (rowMin == int.MaxValue)
            {
                continue;
            }

            if (row.Checked)
            {
                hasChecked = true;
                checkLeft = Math.Min(checkLeft, rowMin - row.X);
            }
            else
            {
                textLeft = Math.Min(textLeft, rowMin - row.X);
            }

            if (row.HasSub)
            {
                hasSub = true;
                arrowGutter = Math.Min(arrowGutter, row.X + row.Width - rowMax - 1);
            }
        }

        return new Ink(
            hasChecked && checkLeft != int.MaxValue ? checkLeft : -1,
            textLeft == int.MaxValue ? -1 : textLeft,
            hasSub && arrowGutter != int.MaxValue ? arrowGutter : -1,
            hasChecked,
            hasSub);
    }

    private static bool IsInk(Color c, Color background)
    {
        int d = Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
        return d > InkThreshold;
    }

    private static JsonObject Summarize(List<Row> rows, Ink ink, int width, int height)
    {
        List<Row> normal = rows.Where(r => !r.Separator).ToList();
        List<Row> separators = rows.Where(r => r.Separator).ToList();
        Row? first = rows.FirstOrDefault();
        Row? last = rows.LastOrDefault();

        int itemHeight = normal.Count == 0 ? -1 : normal.GroupBy(r => r.Height).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        int separatorHeight = separators.Count == 0 ? -1 : separators.Max(r => r.Height);

        return new JsonObject
        {
            ["itemHeight"] = itemHeight,
            ["separatorHeight"] = separatorHeight,
            ["separatorCount"] = separators.Count,
            ["popupPaddingLeft"] = first is null ? -1 : first.X,
            ["popupPaddingTop"] = first is null ? -1 : first.Y,
            ["popupPaddingRight"] = first is null ? -1 : width - (first.X + first.Width),
            ["popupPaddingBottom"] = last is null ? -1 : height - (last.Y + last.Height),
            ["textLeftInset"] = ink.TextLeft,
            ["checkGutterLeft"] = ink.CheckLeft,
            ["submenuArrowGutter"] = ink.ArrowRightGutter,
            ["hasCheckedItem"] = ink.HasChecked,
            ["hasSubmenuItem"] = ink.HasSub,
        };
    }
}
