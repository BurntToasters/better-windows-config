using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Top menus (Alt and the access key), the result list context menu (the menu key), the items of an open popup read from its HMENU, and
// activating an item by its access key. Failure modes 21 and 22 of the driver document.
internal sealed partial class GpeditDriver
{
    private const ushort VkMenuKey = 0x12;

    private static readonly Dictionary<string, char> TopMenuKeys = new(StringComparer.Ordinal)
    {
        ["File"] = 'F',
        ["Action"] = 'A',
        ["View"] = 'V',
        ["Help"] = 'H',
    };

    // The access key of a menu text: the character after a single ampersand.
    internal static char? Mnemonic(string raw)
    {
        for (int i = 0; i < raw.Length - 1; i++)
        {
            if (raw[i] != '&')
            {
                continue;
            }

            if (raw[i + 1] == '&')
            {
                i++;
                continue;
            }

            return raw[i + 1];
        }

        return null;
    }

    private static string MenuClean(string raw)
    {
        int tab = raw.IndexOf('\t');
        string text = tab >= 0 ? raw[..tab] : raw;
        return text.Replace("&&", "\u0001", StringComparison.Ordinal).Replace("&", string.Empty, StringComparison.Ordinal).Replace("\u0001", "&", StringComparison.Ordinal).Trim();
    }

    private JsonArray ReadMenu(IntPtr hmenu, int depth)
    {
        var items = new JsonArray();
        int count = Math.Max(0, Win32.GetMenuItemCount(hmenu));
        for (int i = 0; i < count; i++)
        {
            var sb = new StringBuilder(512);
            Win32.GetMenuStringW(hmenu, (uint)i, sb, sb.Capacity, Wm.MF_BYPOSITION);
            string raw = sb.ToString();
            uint state = Win32.GetMenuState(hmenu, (uint)i, Wm.MF_BYPOSITION);
            bool separator = state != uint.MaxValue && (state & Wm.MF_SEPARATOR) != 0;
            bool popup = state != uint.MaxValue && (state & Wm.MF_POPUP) != 0;
            var item = new JsonObject
            {
                ["index"] = i,
                ["raw"] = raw,
                ["text"] = MenuClean(raw),
                ["accelerator"] = raw.Contains('\t', StringComparison.Ordinal) ? raw[(raw.IndexOf('\t') + 1)..] : null,
                ["mnemonic"] = Mnemonic(raw)?.ToString(),
                ["separator"] = separator,
                ["grayed"] = state != uint.MaxValue && (state & (Wm.MF_GRAYED | 0x0002)) != 0,
                ["checked"] = state != uint.MaxValue && (state & Wm.MF_CHECKED) != 0,
                ["popup"] = popup,
                ["id"] = popup || separator ? null : JsonValue.Create(Win32.GetMenuItemID(hmenu, i)),
            };
            if (popup && depth < 4)
            {
                IntPtr sub = Win32.GetSubMenu(hmenu, i);
                if (sub != IntPtr.Zero)
                {
                    item["items"] = ReadMenu(sub, depth + 1);
                }
            }

            items.Add(item);
        }

        return items;
    }

    private JsonObject DumpPopup(WinInfo popup, string shotName)
    {
        IntPtr hmenu = IntPtr.Zero;
        if (Win32.SendMessageTimeoutW(popup.Hwnd, Wm.MN_GETHMENU, IntPtr.Zero, IntPtr.Zero, Msg.SMTO_ABORTIFHUNG, 2000, out IntPtr result))
        {
            hmenu = result;
        }

        var uiaNames = new JsonArray();
        try
        {
            foreach (AutomationElement item in ProbeUia.OfType(AutomationElement.FromHandle(popup.Hwnd), ControlType.MenuItem))
            {
                uiaNames.Add(ProbeUia.NameOf(item));
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            uiaNames.Add("<uia unavailable: " + ex.Message + ">");
        }

        return new JsonObject
        {
            ["hmenu"] = hmenu == IntPtr.Zero ? null : "0x" + hmenu.ToString("X"),
            ["items"] = hmenu == IntPtr.Zero ? new JsonArray() : ReadMenu(hmenu, 0),
            ["uiaNames"] = uiaNames,
            ["screenshot"] = Shot(popup.Hwnd, shotName),
        };
    }

    private WinInfo WaitPopup(int countAtLeast, TimeSpan limit)
    {
        DateTime end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end)
        {
            List<WinInfo> popups = ProbeWindows.PopupMenus(Pid);
            if (popups.Count >= countAtLeast)
            {
                Thread.Sleep(250);
                return ProbeWindows.PopupMenus(Pid).Last();
            }

            Thread.Sleep(100);
        }

        throw new DriverException("no popup menu appeared within " + (int)limit.TotalSeconds + " s. Screenshot: " + Shot(main, "no-menu"));
    }

    private WinInfo OpenTopMenu(string menu)
    {
        if (!TopMenuKeys.TryGetValue(menu, out char key))
        {
            throw new DriverException($"no access key known for the menu '{menu}'");
        }

        CloseMenus();
        EnsureNoDialogs();
        DriverInput.RequireForeground(main, Pid);
        (ushort Vk, bool Shift)? vk = DriverNative.KeyFor(key);
        if (vk is null)
        {
            throw new DriverException($"no key for '{key}' on this keyboard layout");
        }

        DriverInput.Chord(new[] { VkMenuKey }, vk.Value.Vk);
        return WaitPopup(1, TimeSpan.FromSeconds(2));
    }

    // Opens a top menu, reads every item and submenu from the HMENU, closes the menu.
    internal JsonObject DumpTopMenu(string menu)
    {
        WinInfo popup = OpenTopMenu(menu);
        try
        {
            JsonObject dump = DumpPopup(popup, "menu-" + menu);
            dump["menu"] = menu;
            return dump;
        }
        finally
        {
            CloseMenus();
        }
    }

    // Selects the policy row (or leaves the selection as it is when none is named), presses the menu key on the list, dumps the popup.
    internal JsonObject DumpContextMenu(string? policy, int? occurrence)
    {
        CloseMenus();
        EnsureNoDialogs();
        ListCandidate list = VisibleList() ?? throw new DriverException("no visible result list. " + DescribeLists());
        string selected = "(selection unchanged)";
        if (policy is not null)
        {
            AutomationElement row = FindPolicyRow(policy, occurrence);
            if (!ProbeUia.Select(row))
            {
                ProbeUia.Click(row, doubleClick: false);
            }

            selected = policy;
        }

        ProbeUia.Focus(list.List);
        Thread.Sleep(300);
        DriverInput.RequireForeground(main, Pid);
        DriverInput.Key(DriverInput.VkApps);
        WinInfo popup = WaitPopup(1, TimeSpan.FromSeconds(2));
        try
        {
            JsonObject dump = DumpPopup(popup, "menu-context");
            dump["menu"] = "Context";
            dump["selectedRow"] = selected;
            return dump;
        }
        finally
        {
            CloseMenus();
        }
    }

    private static List<JsonObject>? FindItemPath(JsonArray items, string name)
    {
        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item || item["separator"]?.GetValue<bool>() == true)
            {
                continue;
            }

            string text = item["text"]?.GetValue<string>() ?? string.Empty;
            if (string.Equals(text, name, StringComparison.Ordinal) || string.Equals(text.TrimEnd('.'), name.TrimEnd('.'), StringComparison.Ordinal))
            {
                return new List<JsonObject> { item };
            }

            if (item["items"] is JsonArray nested && FindItemPath(nested, name) is { } inner)
            {
                inner.Insert(0, item);
                return inner;
            }
        }

        return null;
    }

    private static JsonArray SiblingsOf(JsonArray root, List<JsonObject> path, int level)
    {
        JsonArray current = root;
        for (int i = 0; i < level; i++)
        {
            current = path[i]["items"]!.AsArray();
        }

        return current;
    }

    // Reads one item of a top menu (checked, grayed, id) without activating anything.
    internal JsonObject MenuItemInfo(string menu, string itemName)
    {
        JsonObject dump = DumpTopMenu(menu);
        List<JsonObject>? path = FindItemPath(dump["items"]!.AsArray(), itemName);
        if (path is null)
        {
            throw new DriverException($"no item '{itemName}' in the {menu} menu", evidence: dump);
        }

        return path[^1];
    }

    // Activates an item of a top menu by its access key; a submenu on the way is opened by its key. An item without a unique key, or
    // one that is grayed, is not activated.
    internal JsonObject ActivateMenuItem(string menu, string itemName)
    {
        WinInfo popup = OpenTopMenu(menu);
        try
        {
            JsonObject dump = DumpPopup(popup, "menu-" + menu + "-before-" + SafeName(itemName));
            JsonArray root = dump["items"]!.AsArray();
            List<JsonObject> path = FindItemPath(root, itemName)
                ?? throw new DriverException($"no item '{itemName}' in the {menu} menu", evidence: dump);
            var keys = new JsonArray();
            for (int level = 0; level < path.Count; level++)
            {
                JsonObject item = path[level];
                string text = item["text"]!.GetValue<string>();
                if (item["grayed"]!.GetValue<bool>())
                {
                    throw new DriverException($"the item '{text}' is grayed, so it was not activated");
                }

                string? mnemonic = item["mnemonic"]?.GetValue<string>();
                if (string.IsNullOrEmpty(mnemonic))
                {
                    throw new DriverException($"the item '{text}' has no access key, so it was not activated", evidence: dump);
                }

                int same = SiblingsOf(root, path, level).Count(s => s is JsonObject o && o["separator"]?.GetValue<bool>() != true
                    && !o["grayed"]!.GetValue<bool>() && string.Equals(o["mnemonic"]?.GetValue<string>(), mnemonic, StringComparison.OrdinalIgnoreCase));
                if (same != 1)
                {
                    throw new DriverException($"the access key '{mnemonic}' is used by {same} enabled items next to '{text}', so it was not pressed", evidence: dump);
                }

                int popups = ProbeWindows.PopupMenus(Pid).Count;
                DriverInput.RequireForeground(main, Pid);
                DriverInput.CharKey(mnemonic[0]);
                keys.Add(mnemonic);
                if (level < path.Count - 1)
                {
                    WaitPopup(popups + 1, TimeSpan.FromSeconds(2));
                }
            }

            DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (ProbeWindows.PopupMenus(Pid).Count > 0 && DateTime.UtcNow < end)
            {
                Thread.Sleep(100);
            }

            return new JsonObject
            {
                ["menu"] = menu,
                ["item"] = itemName,
                ["path"] = new JsonArray(path.Select(p => (JsonNode?)p["text"]!.GetValue<string>()).ToArray()),
                ["keys"] = keys,
                ["menuBefore"] = dump,
            };
        }
        finally
        {
            CloseMenus();
        }
    }
}
