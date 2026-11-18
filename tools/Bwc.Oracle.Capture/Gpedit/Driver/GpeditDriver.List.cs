using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Result list, status bar, Extended and Standard tabs, header sort. Failure modes 2, 18, 19, 20 and 22 of the driver document.
internal sealed partial class GpeditDriver
{
    private sealed record ListCandidate(AutomationElement List, IntPtr Hwnd, string Headers, bool SettingAndState, NativeRect Rect);

    // Visible list views only, found from the native child windows first (MMC keeps one per node it has shown, over a hundred after a
    // few navigations, and asking UI Automation about each is slow and returns stale ones). Setting and State headers rank first.
    private List<ListCandidate> ListCandidates(out int hiddenCount)
    {
        Win32.GetWindowRect(main, out NativeRect mainRect);
        var found = new List<ListCandidate>();
        hiddenCount = 0;
        foreach (WinInfo w in ProbeWindows.Children(main).Where(c => c.Class == "SysListView32"))
        {
            bool onScreen = w.Rect.Width > 0 && w.Rect.Height > 0 && w.Rect.Left < mainRect.Right && w.Rect.Right > mainRect.Left
                && w.Rect.Top < mainRect.Bottom && w.Rect.Bottom > mainRect.Top;
            if (!w.Visible || !onScreen)
            {
                hiddenCount++;
                continue;
            }

            try
            {
                AutomationElement list = AutomationElement.FromHandle(w.Hwnd);
                string headers = string.Join("|", ProbeUia.OfType(list, ControlType.HeaderItem).Select(h => ProbeUia.NameOf(h).Trim()));
                bool settingAndState = headers.Contains("Setting", StringComparison.OrdinalIgnoreCase) && headers.Contains("State", StringComparison.OrdinalIgnoreCase);
                found.Add(new ListCandidate(list, w.Hwnd, headers, settingAndState, w.Rect));
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
    private bool WaitListStable(TimeSpan limit)
    {
        DateTime end = DateTime.UtcNow + limit;
        string? previous = null;
        while (DateTime.UtcNow < end)
        {
            ctx.ThrowIfCancelled();
            string signature = ListSignature();
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
        var cache = new CacheRequest { TreeScope = namesOnly ? TreeScope.Element : TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        using (cache.Activate())
        {
            AutomationElementCollection found = list.List.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            total = found.Count;
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
        }

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
        try
        {
            foreach (AutomationElement bar in ProbeUia.OfType(Main(), ControlType.StatusBar))
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
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
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
        };
    }

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

    private List<AutomationElement> DistinctTabs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tabs = new List<AutomationElement>();
        foreach (AutomationElement tab in ProbeUia.OfType(Main(), ControlType.TabItem))
        {
            if (seen.Add(ProbeUia.NameOf(tab) + "|" + tab.Current.BoundingRectangle))
            {
                tabs.Add(tab);
            }
        }

        return tabs;
    }

    internal JsonObject ReadTabs()
    {
        List<AutomationElement> tabs = DistinctTabs();
        ListCandidate? list = VisibleList();
        return new JsonObject
        {
            ["tabs"] = new JsonArray(tabs.Select(t => (JsonNode?)ProbeUia.NameOf(t)).ToArray()),
            ["activeTabGuess"] = list is null ? null : ActiveTabGuess(list),
        };
    }

    // Invoke only (section 22 of the session document); a tab without Invoke is an error, never clicked.
    internal JsonObject SelectTab(string name)
    {
        AutomationElement tab = DistinctTabs().FirstOrDefault(t => string.Equals(ProbeUia.NameOf(t).Replace("&", string.Empty, StringComparison.Ordinal).Trim(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new DriverException($"no tab '{name}'. Tabs: " + string.Join(", ", DistinctTabs().Select(ProbeUia.NameOf)));
        if (!ProbeUia.Invoke(tab))
        {
            throw new DriverException($"the tab '{name}' has no InvokePattern, so it was not clicked");
        }

        Thread.Sleep(800);
        bool stable = WaitListStable(TimeSpan.FromSeconds(8));
        JsonObject tabs = ReadTabs();
        tabs["listStable"] = stable;
        tabs["selected"] = name;
        return tabs;
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
        if (!ProbeUia.Invoke(items[at]))
        {
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
            ["sortBefore"] = before?.DeepClone(),
            ["sortAfter"] = after["sort"]?.DeepClone(),
            ["listStable"] = stable,
            ["firstRows"] = after["rows"]?.DeepClone(),
        };
    }
}
