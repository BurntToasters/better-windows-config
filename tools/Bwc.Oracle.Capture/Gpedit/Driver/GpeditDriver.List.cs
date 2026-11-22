using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Result list, status bar, Extended and Standard tabs, header sort. Failure modes 2, 18, 19, 20 and 22 of the driver document.
internal sealed partial class GpeditDriver
{
    private sealed record ListCandidate(AutomationElement List, IntPtr Hwnd, string Headers, bool SettingAndState, NativeRect Rect, int Rows);

    private static bool IsOnScreen(WinInfo w, NativeRect mainRect) =>
        w.Rect.Width > 0 && w.Rect.Height > 0 && w.Rect.Left < mainRect.Right && w.Rect.Right > mainRect.Left
        && w.Rect.Top < mainRect.Bottom && w.Rect.Bottom > mainRect.Top;

    // The row count of a list view by LVM_GETITEMCOUNT, or -1 when it did not answer.
    private static int ListRowCount(IntPtr hwnd)
    {
        try
        {
            return DriverNative.Send(hwnd, Msg.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }
        catch (DriverException)
        {
            return -1;
        }
    }

    // The rows of the largest visible, on-screen result list, by Win32 messages only; 0 when there is none or nothing can be read. It decides
    // between the old UI Automation search and the narrow lookups (failure mode 40).
    private int VisibleListRows()
    {
        try
        {
            Win32.GetWindowRect(main, out NativeRect mainRect);
            return UiaScope.LargestVisibleList(ProbeWindows.Children(main).Where(c => c.Class == "SysListView32").Select(w =>
            {
                bool onScreen = IsOnScreen(w, mainRect);
                return new ListSize(w.Visible, onScreen, w.Visible && onScreen ? ListRowCount(w.Hwnd) : -1);
            }));
        }
        catch (Exception ex) when (ex is DriverException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    // The header items of a list. A list with LargeListRows rows or more asks for the Header children and then their HeaderItem children, because
    // the search below the list walks every row; when that finds nothing the old search runs (failure mode 40).
    private static List<AutomationElement> HeaderItems(AutomationElement list, int rows)
    {
        if (rows >= UiaScope.LargeListRows)
        {
            var narrow = new List<AutomationElement>();
            foreach (AutomationElement header in ProbeUia.OfType(list, ControlType.Header, TreeScope.Children))
            {
                narrow.AddRange(ProbeUia.OfType(header, ControlType.HeaderItem, TreeScope.Children));
            }

            if (narrow.Count > 0)
            {
                return narrow;
            }
        }

        return ProbeUia.OfType(list, ControlType.HeaderItem);
    }

    // Visible list views only, found from the native child windows first (MMC keeps one per node it has shown, over a hundred after a
    // few navigations, and asking UI Automation about each is slow and returns stale ones). Setting and State headers rank first.
    private List<ListCandidate> ListCandidates(out int hiddenCount)
    {
        Win32.GetWindowRect(main, out NativeRect mainRect);
        var found = new List<ListCandidate>();
        hiddenCount = 0;
        foreach (WinInfo w in ProbeWindows.Children(main).Where(c => c.Class == "SysListView32"))
        {
            bool onScreen = IsOnScreen(w, mainRect);
            if (!w.Visible || !onScreen)
            {
                hiddenCount++;
                continue;
            }

            try
            {
                AutomationElement list = AutomationElement.FromHandle(w.Hwnd);
                int rows = ListRowCount(w.Hwnd);
                string headers = string.Join("|", HeaderItems(list, rows).Select(h => ProbeUia.NameOf(h).Trim()));
                bool settingAndState = headers.Contains("Setting", StringComparison.OrdinalIgnoreCase) && headers.Contains("State", StringComparison.OrdinalIgnoreCase);
                found.Add(new ListCandidate(list, w.Hwnd, headers, settingAndState, w.Rect, rows));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                hiddenCount++;
            }
        }

        return found.OrderByDescending(c => c.SettingAndState).ToList();
    }

    private ListCandidate? VisibleList()
    {
        ListCandidate? best = ListCandidates(out _).FirstOrDefault();
        lastListHwnd = best?.Hwnd ?? IntPtr.Zero;
        return best;
    }

    private string DescribeLists()
    {
        List<ListCandidate> all = ListCandidates(out int hidden);
        return $"{all.Count} visible lists, {hidden} hidden: " + string.Join(" ; ", all.Select(c => $"hwnd 0x{c.Hwnd:X} headers=[{c.Headers}]"));
    }

    private string ListSignature()
    {
        ListCandidate? list = VisibleList();
        if (list is null)
        {
            return "none";
        }

        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.NameProperty);
        using (cache.Activate())
        {
            AutomationElementCollection rows = list.List.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            return list.Hwnd.ToString("X") + "|" + list.Headers + "|" + rows.Count + "|" + string.Join("\n", rows.Cast<AutomationElement>().Take(4000).Select(r => r.Cached.Name));
        }
    }

    // True when two reads of the visible list's row names 400 ms apart agree before the limit.
    private bool WaitListStable(TimeSpan limit, HopTimes? times = null)
    {
        DateTime end = DateTime.UtcNow + limit;
        string? previous = null;
        while (DateTime.UtcNow < end)
        {
            ctx.ThrowIfCancelled();
            string signature = times is null ? ListSignature() : times.Time("listSignature", () => ListSignature());
            if (signature == previous)
            {
                return true;
            }

            previous = signature;
            Thread.Sleep(400);
        }

        return false;
    }

    // Columns, rows (setting, state, comment, every cell), status bar and sort of the visible list. Rows come from one cached UI
    // Automation request; maxRows caps a huge list (All Settings) and the result says truncated.
    internal JsonObject ReadList(int maxRows = 6000, bool namesOnly = false)
    {
        ListCandidate list = ProbeUia.Wait(VisibleList, TimeSpan.FromSeconds(10))
            ?? throw new DriverException("no visible result list. " + DescribeLists());
        string[] headers = list.Headers.Length == 0 ? Array.Empty<string>() : list.Headers.Split('|');
        int settingAt = Array.FindIndex(headers, h => string.Equals(h, "Setting", StringComparison.OrdinalIgnoreCase));
        int stateAt = Array.FindIndex(headers, h => string.Equals(h, "State", StringComparison.OrdinalIgnoreCase));
        int commentAt = Array.FindIndex(headers, h => string.Equals(h, "Comment", StringComparison.OrdinalIgnoreCase));

        var rows = new JsonArray();
        int total;

        // A names-only read of a list with LargeListRows rows or more, asking for fewer rows than it has, takes the first rows with a walker that
        // stops there, and the row count from the list view (failure mode 40). An empty walk falls back to the read below.
        bool capped = namesOnly && list.Rows >= UiaScope.LargeListRows && list.Rows > maxRows;
        if (capped)
        {
            rows = FirstRowNames(list.List, maxRows);
            capped = rows.Count > 0;
        }

        total = capped ? list.Rows : ReadRowsCached(list.List, maxRows, namesOnly, settingAt, stateAt, commentAt, rows);

        JsonObject status = ReadStatus();
        return new JsonObject
        {
            ["listHwnd"] = "0x" + list.Hwnd.ToString("X"),
            ["settingStateList"] = list.SettingAndState,
            ["headers"] = new JsonArray(headers.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["sort"] = HeaderSort(list.Hwnd, headers.Length),
            ["rowCount"] = total,
            ["truncated"] = total > rows.Count,
            ["rows"] = rows,
            ["status"] = status,
            ["filtered"] = status["filtered"]?.DeepClone(),
            ["selectedNode"] = SelectedPath() is { } p ? string.Join("/", p.Skip(1)) : null,
            ["activeTabGuess"] = ActiveTabGuess(list),
        };
    }

    // All rows of the list from one cached request (the read of every list below LargeListRows rows, and of a list that is not capped). Adds at
    // most maxRows rows to rows and returns the number of rows the list has.
    private static int ReadRowsCached(AutomationElement list, int maxRows, bool namesOnly, int settingAt, int stateAt, int commentAt, JsonArray rows)
    {
        var cache = new CacheRequest { TreeScope = namesOnly ? TreeScope.Element : TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        using (cache.Activate())
        {
            AutomationElementCollection found = list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            int index = 0;
            foreach (AutomationElement row in found)
            {
                if (index >= maxRows)
                {
                    break;
                }

                var node = new JsonObject { ["index"] = index, ["name"] = row.Cached.Name };
                if (!namesOnly)
                {
                    var cells = row.CachedChildren.Cast<AutomationElement>().Select(c => c.Cached.Name).ToList();
                    node["cells"] = new JsonArray(cells.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
                    node["setting"] = settingAt >= 0 && settingAt < cells.Count ? cells[settingAt] : null;
                    node["state"] = stateAt >= 0 && stateAt < cells.Count ? cells[stateAt] : null;
                    node["comment"] = commentAt >= 0 && commentAt < cells.Count ? cells[commentAt] : null;
                }

                rows.Add(node);
                index++;
            }

            return found.Count;
        }
    }

    // The first rows of a list by name, from a walker over the children of the list that stops after max list items (name and control type cached).
    private static JsonArray FirstRowNames(AutomationElement list, int max)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        var rows = new JsonArray();
        TreeWalker walker = TreeWalker.ControlViewWalker;
        AutomationElement? child = walker.GetFirstChild(list, cache);
        while (child is not null && rows.Count < max)
        {
            if (child.Cached.ControlType == ControlType.ListItem)
            {
                rows.Add(new JsonObject { ["index"] = rows.Count, ["name"] = child.Cached.Name });
            }

            child = walker.GetNextSibling(child, cache);
        }

        return rows;
    }

    // The sort arrow of each column header as the header control reports it (none, up, down). Null when it cannot be read.
    private JsonNode? HeaderSort(IntPtr listHwnd, int columns)
    {
        try
        {
            IntPtr header = DriverNative.Send(listHwnd, Msg.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero)
            {
                return null;
            }

            var result = new JsonArray();
            for (int i = 0; i < columns; i++)
            {
                using var buffer = new RemoteBuffer(mmc.Handle, 72);
                byte[] item = new byte[72];
                BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(0, 4), DriverNative.HdiFormat);
                buffer.Write(item);
                DriverNative.Send(header, DriverNative.HdmGetItemW, new IntPtr(i), buffer.Address);
                uint format = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Read(72).AsSpan(28, 4));
                result.Add((format & DriverNative.HdfSortUp) != 0 ? "up" : (format & DriverNative.HdfSortDown) != 0 ? "down" : "none");
            }

            return result;
        }
        catch (Exception ex) when (ex is DriverException or System.ComponentModel.Win32Exception)
        {
            return JsonValue.Create("unreadable: " + ex.Message);
        }
    }

    // The status bar text of each part, read from the status bar window (text lives in the mmc process, so through remote memory),
    // with UI Automation names as a second source.
    internal JsonObject ReadStatus()
    {
        var parts = new JsonArray();
        string text = string.Empty;
        string? error = null;
        try
        {
            foreach (WinInfo bar in ProbeWindows.Children(main).Where(c => c.Class == "msctls_statusbar32" && c.Visible))
            {
                int count = Math.Max(1, DriverNative.Send(bar.Hwnd, Msg.SB_GETPARTS, IntPtr.Zero, IntPtr.Zero).ToInt32());
                for (int part = 0; part < count; part++)
                {
                    using var buffer = new RemoteBuffer(mmc.Handle, 1024);
                    DriverNative.Send(bar.Hwnd, Msg.SB_GETTEXTW, new IntPtr(part), buffer.Address);
                    string value = System.Text.Encoding.Unicode.GetString(buffer.ReadAll());
                    int nul = value.IndexOf('\0');
                    value = nul >= 0 ? value[..nul] : value;
                    parts.Add(value);
                    if (text.Length == 0 && value.Length > 0)
                    {
                        text = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DriverException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
        }

        var uia = new JsonArray();
        string route = UiaScope.Route(VisibleListRows());
        try
        {
            foreach (AutomationElement bar in StatusBarElements(route))
            {
                foreach (AutomationElement child in ProbeUia.Find(bar, TreeScope.Descendants, Condition.TrueCondition))
                {
                    string name = ProbeUia.NameOf(child);
                    if (name.Length > 0)
                    {
                        uia.Add(name);
                        if (text.Length == 0)
                        {
                            text = name;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException)
        {
            error ??= ex.Message;
        }

        return new JsonObject
        {
            ["text"] = text,
            ["parts"] = parts,
            ["uiaTexts"] = uia,
            ["filtered"] = text.StartsWith("Filtered", StringComparison.OrdinalIgnoreCase),
            ["error"] = error,
            ["uiaRoute"] = route,
        };
    }

    // The elements of the type given, taken from the window handle of their own control on the narrow route (failure mode 40), or by the old
    // search over the main window on the other. A window that has no element any more is skipped.
    private List<AutomationElement> ElementsOfWindows(string className, ControlType? mustBe)
    {
        var elements = new List<AutomationElement>();
        foreach (WinInfo window in UiaScope.VisibleOfClass(ProbeWindows.Children(main), className))
        {
            try
            {
                AutomationElement element = AutomationElement.FromHandle(window.Hwnd);
                if (mustBe is null || element.Current.ControlType == mustBe)
                {
                    elements.Add(element);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException or InvalidOperationException)
            {
                // The window went away between the enumeration and the lookup: it is not an element of this read.
            }
        }

        return elements;
    }

    private List<AutomationElement> StatusBarElements(string route) =>
        route == UiaScope.RouteNarrow ? ElementsOfWindows(UiaScope.StatusBarClass, null) : ProbeUia.OfType(Main(), ControlType.StatusBar);

    // The tabs are HTML elements of the view; they offer Invoke only and no selection state, so the active one is a guess from the
    // layout: Extended leaves room for the description pane between the tree and the list.
    private JsonObject ActiveTabGuess(ListCandidate list)
    {
        Win32.GetWindowRect(FindTreeWindow(), out NativeRect tree);
        int gap = list.Rect.Left - tree.Right;
        return new JsonObject { ["guess"] = gap > 60 ? "Extended" : "Standard", ["gapPixels"] = gap };
    }

    private IntPtr FindTreeWindow() =>
        ProbeWindows.Children(main).FirstOrDefault(c => c.Class == "SysTreeView32" && c.Visible)?.Hwnd ?? main;

    private List<AutomationElement> DistinctTabs() => DistinctTabs(UiaScope.Route(VisibleListRows()));

    // The tab items of the view. On the narrow route they come from the tab strip window (class AMCCustomTab) and not from a search of the
    // whole main window; a missing strip gives no tab, so the caller fails at once (failure mode 40).
    private List<AutomationElement> DistinctTabs(string route)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tabs = new List<AutomationElement>();
        IEnumerable<AutomationElement> items = route == UiaScope.RouteNarrow
            ? ElementsOfWindows(UiaScope.TabClass, null).SelectMany(strip => ProbeUia.OfType(strip, ControlType.TabItem))
            : ProbeUia.OfType(Main(), ControlType.TabItem);
        foreach (AutomationElement tab in items)
        {
            if (seen.Add(ProbeUia.NameOf(tab) + "|" + tab.Current.BoundingRectangle))
            {
                tabs.Add(tab);
            }
        }

        return tabs;
    }

    internal JsonObject ReadTabs() => ReadTabs(UiaScope.Route(VisibleListRows()));

    private JsonObject ReadTabs(string route)
    {
        List<AutomationElement> tabs = DistinctTabs(route);
        ListCandidate? list = VisibleList();
        return new JsonObject
        {
            ["tabs"] = new JsonArray(tabs.Select(t => (JsonNode?)ProbeUia.NameOf(t)).ToArray()),
            ["activeTabGuess"] = list is null ? null : ActiveTabGuess(list),
            ["uiaRoute"] = route,
        };
    }

    // Invoke only (section 22 of the session document); a tab without Invoke is an error, never clicked.
    internal JsonObject SelectTab(string name)
    {
        string route = UiaScope.Route(VisibleListRows());
        AutomationElement tab = DistinctTabs(route).FirstOrDefault(t => string.Equals(ProbeUia.NameOf(t).Replace("&", string.Empty, StringComparison.Ordinal).Trim(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new DriverException($"no tab '{name}'. Tabs: " + string.Join(", ", DistinctTabs(route).Select(ProbeUia.NameOf)));
        if (!ProbeUia.Invoke(tab))
        {
            throw new DriverException($"the tab '{name}' has no InvokePattern, so it was not clicked");
        }

        Thread.Sleep(800);
        bool stable = WaitListStable(TimeSpan.FromSeconds(8));
        JsonObject tabs = ReadTabs(route);
        tabs["listStable"] = stable;
        tabs["selected"] = name;
        return tabs;
    }

    // Brings a column header into view when UI Automation gives it no clickable rectangle, by the first route that works: ScrollIntoView on the
    // header item, the horizontal scroll of the list, then LVM_SCROLL by a computed distance. The rectangle is read again after each route. The
    // window is never resized. Failure mode 41 in docs/failure-modes/oracle-gpedit-driver.md.
    private JsonObject BringHeaderIntoView(ListCandidate list, AutomationElement item, int column)
    {
        var tried = new JsonArray();
        string by = "none needed";
        int steps = 0;

        bool Clickable()
        {
            try
            {
                return HeaderScroll.Clickable(item.Current.BoundingRectangle, list.Rect);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        bool ok = Clickable();
        if (!ok)
        {
            tried.Add("ScrollItem");
            try
            {
                if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object? scrollItem))
                {
                    ((ScrollItemPattern)scrollItem).ScrollIntoView();
                    Thread.Sleep(200);
                    steps++;
                    ok = Clickable();
                    by = ok ? "ScrollItem" : by;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or System.Runtime.InteropServices.COMException)
            {
                tried.Add("ScrollItem threw " + ex.GetType().Name);
            }
        }

        if (!ok)
        {
            tried.Add("ScrollPattern");
            try
            {
                if (list.List.TryGetCurrentPattern(ScrollPattern.Pattern, out object? scrollPattern) && ((ScrollPattern)scrollPattern).Current.HorizontallyScrollable)
                {
                    var pattern = (ScrollPattern)scrollPattern;
                    for (int i = 0; i < 20 && !ok; i++)
                    {
                        pattern.Scroll(ScrollAmount.LargeIncrement, ScrollAmount.NoAmount);
                        Thread.Sleep(150);
                        steps++;
                        ok = Clickable();
                    }

                    by = ok ? "ScrollPattern" : by;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or System.Runtime.InteropServices.COMException)
            {
                tried.Add("ScrollPattern threw " + ex.GetType().Name);
            }
        }

        if (!ok)
        {
            tried.Add("LVM_SCROLL");
            try
            {
                int WidthOf(int index) => DriverNative.Send(list.Hwnd, Msg.LVM_GETCOLUMNWIDTH, new IntPtr(index), IntPtr.Zero).ToInt32();
                int left = Enumerable.Range(0, column).Sum(WidthOf);
                int width = WidthOf(column);
                Win32.GetClientRect(list.Hwnd, out NativeRect client);
                for (int i = 0; i < 8 && !ok; i++)
                {
                    int position = DriverNative.GetScrollPos(list.Hwnd, DriverNative.SbHorz);
                    int distance = HeaderScroll.Distance(left, width, client.Width, position);
                    if (distance == 0)
                    {
                        // The widths and the position disagree with what UI Automation shows: step by half a view instead of not moving.
                        distance = Math.Max(1, client.Width / 2);
                    }

                    DriverNative.Send(list.Hwnd, DriverNative.LvmScroll, new IntPtr(distance), IntPtr.Zero);
                    Thread.Sleep(150);
                    steps++;
                    ok = Clickable();
                }

                by = ok ? "LVM_SCROLL" : by;
            }
            catch (DriverException ex)
            {
                tried.Add("LVM_SCROLL threw " + ex.Message);
            }
        }

        string rect;
        try
        {
            rect = item.Current.BoundingRectangle.ToString();
        }
        catch (ElementNotAvailableException)
        {
            rect = "(no element)";
        }

        return new JsonObject
        {
            ["clickable"] = ok,
            ["scrolledBy"] = by,
            ["scrollSteps"] = steps,
            ["tried"] = tried,
            ["rectangle"] = rect,
            ["listRectangle"] = $"{list.Rect.Left},{list.Rect.Top},{list.Rect.Right},{list.Rect.Bottom}",
        };
    }

    // One click on a column header, by Invoke on the header item; a guarded click only when the item has no Invoke.
    internal JsonObject SortColumn(string column)
    {
        ListCandidate list = VisibleList() ?? throw new DriverException("no visible result list. " + DescribeLists());
        List<AutomationElement> items = ProbeUia.OfType(list.List, ControlType.HeaderItem);
        List<string> names = items.Select(i => ProbeUia.NameOf(i).Trim()).ToList();
        int at = names.FindIndex(n => string.Equals(n, column, StringComparison.Ordinal));
        if (at < 0)
        {
            throw new DriverException($"no column header '{column}'. Headers: [{string.Join(" | ", names)}]");
        }

        JsonNode? before = HeaderSort(list.Hwnd, names.Count);
        string via = "Invoke";
        var scroll = new JsonObject { ["scrolledBy"] = "none needed", ["scrollSteps"] = 0 };
        if (!ProbeUia.Invoke(items[at]))
        {
            // A header that is scrolled out of the list has no rectangle to click: bring it into view first (failure mode 41).
            scroll = BringHeaderIntoView(list, items[at], at);
            if (scroll["clickable"]!.GetValue<bool>() == false)
            {
                throw new DriverException($"the column header '{column}' is not clickable after scrolling the list", evidence: scroll);
            }

            ProbeUia.Click(items[at], doubleClick: false);
            via = "guarded click";
        }

        Thread.Sleep(800);
        bool stable = WaitListStable(TimeSpan.FromSeconds(8));
        JsonObject after = ReadList(maxRows: 12, namesOnly: true);
        return new JsonObject
        {
            ["column"] = column,
            ["via"] = via,
            ["scrolledBy"] = scroll["scrolledBy"]?.DeepClone(),
            ["scrollSteps"] = scroll["scrollSteps"]?.DeepClone(),
            ["sortBefore"] = before?.DeepClone(),
            ["sortAfter"] = after["sort"]?.DeepClone(),
            ["listStable"] = stable,
            ["firstRows"] = after["rows"]?.DeepClone(),
        };
    }
}
