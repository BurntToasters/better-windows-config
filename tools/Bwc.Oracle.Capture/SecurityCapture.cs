using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `security`: opens Edit > Permissions on the seeded key BWC-Oracle\Mixed, reads the sheet's tabs, buttons and
// controls, presses Advanced (BM_CLICK on that one button, as PermissionsCapture does), then reads the Advanced
// window's tabs, Owner line and its link, the buttons (Add, Remove, View, Disable inheritance) and the check box that
// replaces child entries. Both windows are cancelled with IDCANCEL. No button other than Advanced is pressed, and
// nothing is changed. Tracked: regedit-security.json. Text: local/security-text.json.
internal static class SecurityCapture
{
    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Mixed"), waitForList: true);
        uint windowDpi = Win32.GetDpiForWindow(w.Main);

        IntPtr sheet = PermissionsCapture.OpenPermissions(w);
        (JsonObject sheetTracked, JsonObject sheetText) = Page(w, sheet, "permissions-sheet");
        try
        {
            IntPtr advanced = PermissionsCapture.OpenAdvanced(w, sheet);
            try
            {
                (JsonObject advancedTracked, JsonObject advancedText) = Page(w, advanced, "advanced-security-settings");
                Report.WriteJson(paths.SecurityJson, new JsonObject
                {
                    ["schema"] = 1,
                    ["generator"] = "Bwc.Oracle.Capture security",
                    ["os"] = Report.OsInfo(),
                    ["regedit"] = Report.RegeditInfo(),
                    ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
                    ["note"] = "Both windows were cancelled with IDCANCEL. No button except Advanced was pressed. Titles and control text are in local/security-text.json.",
                    ["permissionsSheet"] = sheetTracked,
                    ["advancedSecuritySettings"] = advancedTracked,
                });
                Report.WriteJson(paths.LocalText("security-text.json"), new JsonObject
                {
                    ["permissionsSheet"] = sheetText,
                    ["advancedSecuritySettings"] = advancedText,
                });
            }
            finally
            {
                w.CloseDialog(advanced);
            }
        }
        finally
        {
            w.CloseDialog(sheet);
        }
    }

    // Describes one window: its tabs (texts), every descendant control (ids, classes, state), and its buttons.
    private static (JsonObject Tracked, JsonObject Text) Page(RegeditWindow w, IntPtr dlg, string name)
    {
        List<UiProbe.Ctl> controls = UiProbe.Controls(w, dlg, deep: true);
        IntPtr tab = w.ChildrenOf(dlg).FirstOrDefault(c => RegeditWindow.ClassOf(c) == "SysTabControl32");
        List<string> tabs = UiProbe.TabTexts(w, tab);
        List<PermissionsCapture.Button> buttons = PermissionsCapture.Buttons(w, dlg);

        // Geometry is left out: the Advanced window's entry list is scrolled, so positions and the order by position change
        // between runs. Each control is identified by class, id and state, and the list is sorted by those.
        var stable = controls
            .Select(c => new JsonObject
            {
                ["class"] = c.Class,
                ["id"] = c.Id,
                ["visible"] = c.Visible,
                ["enabled"] = c.Enabled,
                ["style"] = $"0x{(uint)c.Style:X8}",
                ["checked"] = c.Checked < 0 ? null : JsonValue.Create(c.Checked == 1),
            })
            .OrderBy(n => n["class"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(n => n["id"]!.GetValue<int>())
            .ThenBy(n => n["enabled"]!.GetValue<bool>())
            .ThenBy(n => n["visible"]!.GetValue<bool>())
            .ThenBy(n => n["checked"]?.GetValue<bool>() == true ? 1 : 0)
            .ToList();

        var tracked = new JsonObject
        {
            ["name"] = name,
            ["class"] = RegeditWindow.ClassOf(dlg),
            ["measured"] = true,
            ["tabCount"] = tabs.Count,
            ["buttonCount"] = buttons.Count,
            ["buttonsEnabled"] = new JsonArray(buttons.Select(b => (JsonNode?)JsonValue.Create(b.Enabled)).ToArray()),
            ["controls"] = new JsonArray(stable.Select(n => (JsonNode?)n).ToArray()),
        };
        var text = new JsonObject
        {
            ["title"] = UiProbe.TextOf(dlg),
            ["tabs"] = new JsonArray(tabs.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["buttons"] = new JsonArray(buttons.Select(b => (JsonNode?)new JsonObject { ["text"] = b.Text, ["enabled"] = b.Enabled }).ToArray()),
            ["controls"] = UiProbe.ControlsLocal(controls),
        };
        return (tracked, text);
    }
}
