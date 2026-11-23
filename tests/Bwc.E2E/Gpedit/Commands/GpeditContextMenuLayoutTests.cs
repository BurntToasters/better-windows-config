using System.Drawing;
using System.Text.Json;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Gpedit.Filter;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// The layout of the result pane context menu per node kind and the Properties toolbar button (CTX-001 to CTX-006,
/// TOOL-009), compared with the captures F/Menus_Computer, F/Menus_User and F/Toolbar_States, which the tests read from
/// the golden files.
/// Structure is asserted by command id and by separator group, never by classic text. The Refresh entry is the DIFF-007
/// addition and is left out of the comparison with the capture. The failure modes are in
/// docs/failure-modes/gpedit-commands.md, section 11. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditContextMenuLayoutTests
{
    private const int ExportList = 13436;
    private const int ViewSubmenu = 2100;
    private const int ArrangeSubmenu = 2104;
    private const int AutoArrange = 2105;
    private const int LineUp = 2106;
    private const int Columns = 1000;
    private const int Customize = 13231;
    private const int Large = 1004;
    private const int Detail = 1007;
    private const string ArrangePrefix = "ArrangeSubmenuCommand_";

    private static readonly int[] ViewModeIds = [1004, 1005, 1006, 1007];

    /// <summary>The node kinds of the captures: capture label, check id, path below the side, Export List, filter.</summary>
    private static readonly (string Label, string Id, string[] Below, bool Export, bool Filter)[] Kinds =
    [
        ("context menu at root configuration node", "configuration", [], true, false),
        ("context menu at Administrative Templates", "administrativeTemplates", [Admin], true, true),
        ("context menu at BWC Oracle category", "category", [Admin, Oracle], true, true),
        ("context menu at category with policies", "categoryWithPolicies", [Admin, Oracle, BasicsName], true, true),
        ("context menu at All Settings", "allSettings", [Admin, AllSettings], true, true),
        ("context menu at Software Settings placeholder", "softwareSettings", ["Software Settings"], false, false),
        ("context menu at Windows Settings placeholder", "windowsSettings", ["Windows Settings"], true, false),
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ContextMenu_Layout_NodeKinds()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("contextlayout", "nodes");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-contextlayout-nodes");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-contextlayout-nodes", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);

        foreach ((string side, string sideName) in new[] { ("computer", Computer), ("user", User) })
        {
            Dictionary<string, List<CapturedItem>> capture = LoadMenus(side == "computer" ? "Computer" : "User");
            checks.Record(side + ".captureRead", capture.Count == Kinds.Length, $"{capture.Count} context menus read from F/Menus_{side}; expected {Kinds.Length}.");
            foreach ((string label, string kindId, string[] below, bool export, bool filter) in Kinds)
            {
                if (!capture.TryGetValue(label, out List<CapturedItem>? captured))
                {
                    checks.Record($"{side}.{kindId}.captured", false, $"the capture has no '{label}'.");
                    continue;
                }

                CheckKind(run, checks, context, $"{side}.{kindId}", [Root, sideName, .. below], export, filter, captured);
            }
        }

        // The context-menu key with no row selected gives the menu of the empty area (CTX-008).
        run.GoTo(Root, Computer, Admin);
        _ = run.FocusPane("result");
        run.Press(GpeditKeys.Apps);
        List<GpeditMenuEntry> keyed = GpeditCommandRun.WaitContextMenu(context);
        checks.Record(
            "key.ids",
            keyed.Select(e => e.Id).SequenceEqual(ExpectedTop(export: true)),
            $"the menu key at Administrative Templates: {FrameMenu.Describe(keyed)}.");
        run.WriteArtifacts("gpedit-contextlayout-key.png");
        _ = run.CloseMenus(ContextMenuDriver.ContextPrefix);
        _ = context.WaitForNoMenu();

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ContextMenu_Layout_Commands()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("contextlayout", "commands");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-contextlayout-commands");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-contextlayout-commands", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);
        using var driver = new EditingDriver(run.Shell.Session);

        run.GoTo(Root, Computer, Admin);
        checks.Record("start.mode", run.ListReadout().GetValueOrDefault("Mode") == "Detail", $"Mode={run.ListReadout().GetValueOrDefault("Mode")}.");

        // A view mode from the menu sets the mode of the list, the menu closes, and the menu built next shows the new mode.
        OpenEmptyAreaMenu(run, context);
        _ = run.OpenContextSubmenu(context, ViewSubmenu, FilterIds.ViewSubmenuPrefix);
        bool invoked = driver.Invoke(FilterIds.ViewSubmenuPrefix + Large);
        checks.Record(
            "mode.runs",
            invoked && ShellRun.WaitUntil(() => run.ListReadout().GetValueOrDefault("Mode") == "LargeIcons"),
            $"invoked={invoked}, Mode={run.ListReadout().GetValueOrDefault("Mode")}.");
        checks.Record("menuCloses", context.WaitForNoMenu(), "the menu closes after a command ran.");
        OpenEmptyAreaMenu(run, context);
        _ = run.OpenContextSubmenu(context, ViewSubmenu, FilterIds.ViewSubmenuPrefix);
        bool largeChecked = GpeditFilterRun.ToggledOn(run.Shell.Find(FilterIds.ViewSubmenuPrefix + Large)) == true;
        bool detailChecked = GpeditFilterRun.ToggledOn(run.Shell.Find(FilterIds.ViewSubmenuPrefix + Detail)) == true;
        checks.Record("afterMode.checked", largeChecked && !detailChecked, $"Large Icons checked={largeChecked}, Detail checked={detailChecked}.");
        invoked = driver.Invoke(FilterIds.ViewSubmenuPrefix + Detail);
        checks.Record(
            "mode.back",
            invoked && ShellRun.WaitUntil(() => run.ListReadout().GetValueOrDefault("Mode") == "Detail"),
            $"invoked={invoked}, Mode={run.ListReadout().GetValueOrDefault("Mode")}.");
        _ = context.WaitForNoMenu();

        // Line Up Icons counts, and the mode, the rows and the node stay.
        int lineUps = Math.Max(0, run.Count("LineUps"));
        int rows = run.Int("Rows");
        OpenEmptyAreaMenu(run, context);
        invoked = context.Invoke(LineUp);
        checks.Record("lineUp.runs", invoked && run.WaitCount("LineUps", lineUps + 1), $"invoked={invoked}, LineUps={run.Count("LineUps")}, expected {lineUps + 1}.");
        checks.Record(
            "lineUp.keepsMode",
            run.ListReadout().GetValueOrDefault("Mode") == "Detail" && run.Int("Rows") == rows && run.NodePath().SequenceEqual([Root, Computer, Admin]),
            $"Mode={run.ListReadout().GetValueOrDefault("Mode")}, Rows={run.Int("Rows")} (was {rows}), Node={run.Str("Node")}.");
        _ = context.WaitForNoMenu();

        // Auto Arrange is a check item: it starts unchecked, and each run flips it.
        checks.Record("autoArrange.start", run.Count("AutoArrange") == 0, $"AutoArrange={run.Str("AutoArrange")}.");
        OpenEmptyAreaMenu(run, context);
        _ = run.OpenContextSubmenu(context, ArrangeSubmenu, ArrangePrefix);
        invoked = driver.Invoke(ArrangePrefix + AutoArrange);
        checks.Record("autoArrange.runs", invoked && run.WaitCount("AutoArrange", 1), $"invoked={invoked}, AutoArrange={run.Str("AutoArrange")}.");
        _ = context.WaitForNoMenu();
        OpenEmptyAreaMenu(run, context);
        _ = run.OpenContextSubmenu(context, ArrangeSubmenu, ArrangePrefix);
        checks.Record(
            "autoArrange.checked",
            GpeditFilterRun.ToggledOn(run.Shell.Find(ArrangePrefix + AutoArrange)) == true,
            "the entry is checked while Auto Arrange is on.");
        invoked = driver.Invoke(ArrangePrefix + AutoArrange);
        checks.Record("autoArrange.off", invoked && run.WaitCount("AutoArrange", 0), $"invoked={invoked}, AutoArrange={run.Str("AutoArrange")}.");
        _ = context.WaitForNoMenu();

        // Add/Remove Columns and Customize View open the dialogs of the menu bar commands.
        OpenEmptyAreaMenu(run, context);
        _ = run.OpenContextSubmenu(context, ViewSubmenu, FilterIds.ViewSubmenuPrefix);
        invoked = driver.Invoke(FilterIds.ViewSubmenuPrefix + Columns);
        using (FrameDialog? columns = invoked ? FrameDialog.Attach(run.Shell, FrameIds.ColumnsDialog) : null)
        {
            checks.Record("columns.opens", columns is not null, $"invoked={invoked}; the Add/Remove Columns window opens.");
            if (columns is not null)
            {
                checks.Record("columns.closes", columns.PressAndClose(FrameIds.ColumnsCancel), "Cancel closes the window.");
            }
        }

        OpenEmptyAreaMenu(run, context);
        _ = run.OpenContextSubmenu(context, ViewSubmenu, FilterIds.ViewSubmenuPrefix);
        invoked = driver.Invoke(FilterIds.ViewSubmenuPrefix + Customize);
        using (FrameDialog? customize = invoked ? FrameDialog.Attach(run.Shell, FrameIds.CustomizeDialog) : null)
        {
            checks.Record("customize.opens", customize is not null, $"invoked={invoked}; the Customize View window opens.");
            if (customize is not null)
            {
                checks.Record("customize.closes", customize.PressAndClose(FrameIds.CustomizeOk), "OK closes the window.");
            }
        }

        // Export List opens the save dialog of the Action menu command; Cancel writes nothing.
        OpenEmptyAreaMenu(run, context);
        invoked = context.Invoke(ExportList);
        using var pickers = new SystemFileDialogDriver(run.Shell.Session);
        IntPtr dialog = invoked ? pickers.WaitForDialog(TimeSpan.FromSeconds(15)) : IntPtr.Zero;
        checks.Record("export.opens", dialog != IntPtr.Zero, $"invoked={invoked}; a save dialog opens.");
        if (dialog != IntPtr.Zero)
        {
            checks.Record("export.cancel", pickers.TryCancel(dialog), "Escape closes the save dialog.");
        }

        run.WriteArtifacts("gpedit-contextlayout-commands.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Toolbar_PropertiesHidden()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("toolbar", "properties");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-toolbar-properties");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-toolbar-properties", sandbox);

        // TOOL-009: the capture shows its Properties button hidden at every node and with a row selected, and seven visible
        // standard buttons (it has the action pane toggle where BWC has Refresh, DIFF-007 and TOOL-014).
        int capturedVisible = CapturedVisibleStandardButtons();
        checks.Record("capture.visible", capturedVisible == 7, $"the capture shows {capturedVisible} standard buttons; expected 7.");

        string[] expected = ["Back", "Forward", "UpOneLevel", "ShowHideConsoleTree", "Refresh", "ExportList", "Help"];
        (string[] Path, string Id)[] kinds =
        [
            ([Root], "root"),
            ([Root, Computer], "computerConfiguration"),
            ([Root, Computer, "Software Settings"], "softwarePlaceholder"),
            ([Root, Computer, Admin], "administrativeTemplates"),
            (OraclePath, "category"),
            (BasicsPath, "categoryWithPolicies"),
            ([Root, Computer, Admin, AllSettings], "allSettings"),
            ([Root, User], "userConfiguration"),
        ];
        foreach ((string[] path, string id) in kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            CheckToolbar(run, checks, id, expected, capturedVisible);
        }

        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        CheckToolbar(run, checks, "policyRow", expected, capturedVisible);
        _ = run.InvokeButton("UpOneLevel");
        _ = run.WaitNode(OraclePath);
        run.SelectRow(BasicsName);
        CheckToolbar(run, checks, "folderRow", expected, capturedVisible);

        // The Edit command is still reachable without the button: the Action menu entry edits the selected row.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        int edits = Math.Max(0, run.Count("Edits"));
        bool invoked = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, GpeditCommandIds.Edit);
        checks.Record("editViaMenu", invoked && run.WaitCount("Edits", edits + 1), $"invoked={invoked}, Edits={run.Count("Edits")}, expected {edits + 1}.");

        run.WriteArtifacts("gpedit-toolbar-properties.png");
        checks.AssertAllPassed();
    }

    // ---- the menu of one node ------------------------------------------------------------------------

    private static void CheckKind(
        GpeditRun run,
        GpeditChecks checks,
        ContextMenuDriver context,
        string id,
        string[] path,
        bool export,
        bool filter,
        List<CapturedItem> captured)
    {
        string where = "context menu on the empty area at " + string.Join(" > ", path);
        run.GoTo(path);
        _ = run.WaitNode(path);
        OpenEmptyAreaMenu(run, context);
        List<GpeditMenuEntry> top = GpeditCommandRun.WaitContextMenu(context);
        int[] topIds = top.Select(e => e.Id).ToArray();
        string listing = FrameMenu.Describe(top);

        checks.Record(id + ".top.ids", topIds.SequenceEqual(ExpectedTop(export)), $"{where}: [{string.Join(",", topIds)}], expected [{string.Join(",", ExpectedTop(export))}]; {listing}.");
        checks.Record(id + ".top.helpLast", top.Count > 0 && top[^1].Id == GpeditCommandIds.Help && top[^1].Enabled, $"{where}: Help is the last entry and enabled.");
        checks.Record(
            id + ".top.groups",
            GroupsWithout(top, GpeditCommandIds.Refresh).SequenceEqual(GroupsOf(captured)),
            $"{where}: groups [{string.Join(",", GroupsWithout(top, GpeditCommandIds.Refresh))}] without Refresh, capture [{string.Join(",", GroupsOf(captured))}].");

        List<GpeditMenuEntry> view = run.OpenContextSubmenu(context, ViewSubmenu, FilterIds.ViewSubmenuPrefix);
        int[] expectedView = [Columns, .. ViewModeIds, .. (filter ? new[] { FilterIds.Toggle, FilterIds.Options, FilterIds.ReApply } : []), Customize];
        string viewListing = FrameMenu.Describe(view);
        checks.Record(id + ".view.ids", view.Select(e => e.Id).SequenceEqual(expectedView), $"{where}: View [{string.Join(",", view.Select(e => e.Id))}], expected [{string.Join(",", expectedView)}]; {viewListing}.");
        CapturedItem? capturedView = captured.Where(c => c.Popup).ElementAtOrDefault(0);
        checks.Record(
            id + ".view.groups",
            capturedView is not null && FrameMenu.GroupSizes(view).SequenceEqual(GroupsOf(capturedView.Items)),
            $"{where}: View groups [{string.Join(",", FrameMenu.GroupSizes(view))}], capture [{string.Join(",", capturedView is null ? [] : GroupsOf(capturedView.Items))}].");
        bool[] grayed = view.Select(e => !e.Enabled).ToArray();
        bool[] capturedGrayed = capturedView is null ? [] : Leaves(capturedView.Items).Select(l => l.Grayed).ToArray();
        checks.Record(id + ".view.grayed", grayed.SequenceEqual(capturedGrayed), $"{where}: grayed [{string.Join(",", grayed)}], capture [{string.Join(",", capturedGrayed)}] (Re-Apply Filter is grayed while the filter is off).");
        bool[] isChecked = view.Select(e => GpeditFilterRun.ToggledOn(run.Shell.Find(FilterIds.ViewSubmenuPrefix + e.Id)) == true).ToArray();
        bool[] capturedChecked = capturedView is null ? [] : Leaves(capturedView.Items).Select(l => l.Checked).ToArray();
        checks.Record(id + ".view.checked", isChecked.SequenceEqual(capturedChecked), $"{where}: checked [{string.Join(",", isChecked)}], capture [{string.Join(",", capturedChecked)}] (Detail is the mode at the start).");

        // The icon arrangement submenu.
        List<GpeditMenuEntry> arrange = run.OpenContextSubmenu(context, ArrangeSubmenu, ArrangePrefix);
        CapturedItem? capturedArrange = captured.Where(c => c.Popup).ElementAtOrDefault(1);
        checks.Record(id + ".arrange.ids", arrange.Select(e => e.Id).SequenceEqual([AutoArrange]), $"{where}: Arrange [{string.Join(",", arrange.Select(e => e.Id))}], expected [{AutoArrange}].");
        checks.Record(
            id + ".arrange.groups",
            capturedArrange is not null && FrameMenu.GroupSizes(arrange).SequenceEqual(GroupsOf(capturedArrange.Items)),
            $"{where}: Arrange groups [{string.Join(",", FrameMenu.GroupSizes(arrange))}], capture [{string.Join(",", capturedArrange is null ? [] : GroupsOf(capturedArrange.Items))}].");
        checks.Record(id + ".arrange.enabled", arrange.All(e => e.Enabled), $"{where}: the arrangement entry is enabled.");

        // The words are BWC's, from the resource file: no label is a key name or empty.
        bool labels =
            NameOf(top, ExportList, "Export List", export) &&
            NameOf(top, ViewSubmenu, "View") &&
            NameOf(top, ArrangeSubmenu, "Arrange Icons") &&
            NameOf(top, LineUp, "Line Up Icons") &&
            NameOf(top, GpeditCommandIds.Refresh, "Refresh") &&
            NameOf(top, GpeditCommandIds.Help, "Help") &&
            NameOf(view, Columns, "Add/Remove Columns") &&
            NameOf(view, Large, "Large Icons") &&
            NameOf(view, 1005, "Small Icons") &&
            NameOf(view, 1006, "List") &&
            NameOf(view, Detail, "Detail") &&
            NameOf(view, Customize, "Customize View") &&
            NameOf(arrange, AutoArrange, "Auto Arrange");
        checks.Record(id + ".labels", labels, $"{where}: the labels are the BWC words; top {listing}; view {viewListing}; arrange {FrameMenu.Describe(arrange)}.");

        if (id == "computer.administrativeTemplates")
        {
            run.WriteArtifacts("gpedit-contextlayout-nodes.png");
        }

        _ = run.CloseMenus(ContextMenuDriver.ContextPrefix);
        _ = context.WaitForNoMenu();
    }

    private static int[] ExpectedTop(bool export) =>
        [.. (export ? new[] { ExportList } : []), ViewSubmenu, ArrangeSubmenu, LineUp, GpeditCommandIds.Refresh, GpeditCommandIds.Help];

    private static bool NameOf(IReadOnlyList<GpeditMenuEntry> entries, int id, string prefix, bool required = true)
    {
        GpeditMenuEntry? entry = entries.FirstOrDefault(e => e.Id == id);
        return entry is null ? !required : entry.Name.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Right clicks the empty area of the result pane (the list or a placeholder page) and waits for a menu.</summary>
    private static void OpenEmptyAreaMenu(GpeditRun run, ContextMenuDriver context)
    {
        // A placeholder page has no list (the tree tests pin that), so its empty area is the page itself.
        Rectangle? area = run.Rect(GpeditRun.ListId) ?? run.Rect("GpeditPlaceholderPage");
        if (area is { } rect)
        {
            _ = context.RightClick(rect.Left + (rect.Width / 2), rect.Bottom - 8);
        }

        _ = context.WaitForItem(GpeditCommandIds.Help);
    }

    /// <summary>The separator groups of a BWC menu with one entry left out (the DIFF-007 Refresh entry).</summary>
    private static int[] GroupsWithout(IReadOnlyList<GpeditMenuEntry> entries, int skipId)
    {
        int[] sizes = FrameMenu.GroupSizes(entries);
        int at = entries.ToList().FindIndex(e => e.Id == skipId);
        int start = 0;
        for (int group = 0; at >= 0 && group < sizes.Length; group++)
        {
            if (at < start + sizes[group])
            {
                sizes[group]--;
                break;
            }

            start += sizes[group];
        }

        return sizes.Where(size => size > 0).ToArray();
    }

    // ---- the toolbar -----------------------------------------------------------------------------------

    private static void CheckToolbar(GpeditRun run, GpeditChecks checks, string id, string[] expected, int capturedVisible)
    {
        string[] all = ["Back", "Forward", "UpOneLevel", "ShowHideConsoleTree", "Properties", "Refresh", "ExportList", "Help"];
        bool absent = ShellRun.WaitUntil(() => run.Shell.Rect(ShellRun.ButtonId("Properties")) is null, TimeSpan.FromSeconds(3));
        string[] visible = all
            .Select(name => (Name: name, Rect: run.Shell.Rect(ShellRun.ButtonId(name))))
            .Where(item => item.Rect is not null)
            .OrderBy(item => item.Rect!.Value.Left)
            .Select(item => item.Name)
            .ToArray();
        checks.Record(id + ".absent", absent, $"{id}: the Properties button is not on screen.");
        checks.Record(
            id + ".visible",
            visible.SequenceEqual(expected) && visible.Length == capturedVisible,
            $"{id}: visible standard buttons [{string.Join(",", visible)}], expected [{string.Join(",", expected)}] ({capturedVisible} in the capture).");
    }

    // ---- the captures ----------------------------------------------------------------------------------

    /// <summary>One entry of a captured menu dump: a separator, a leaf or a popup with its entries.</summary>
    private sealed record CapturedItem(bool Separator, bool Popup, bool Grayed, bool Checked, List<CapturedItem> Items);

    private static string GoldenPath(string caseName) =>
        Path.Combine(GuiGate.Config.RepoRoot, "docs", "parity", "oracle", "gpedit", "goldens", "F", caseName, "case.json");

    /// <summary>The context menu dumps of F/Menus_&lt;side&gt;, by the label of the dump op.</summary>
    private static Dictionary<string, List<CapturedItem>> LoadMenus(string side)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(GoldenPath("Menus_" + side)));
        JsonElement root = document.RootElement;
        var observed = new Dictionary<int, JsonElement>();
        foreach (JsonElement entry in root.GetProperty("observed").EnumerateArray())
        {
            observed[entry.GetProperty("index").GetInt32()] = entry.Clone();
        }

        var menus = new Dictionary<string, List<CapturedItem>>(StringComparer.Ordinal);
        foreach (JsonElement op in root.GetProperty("ops").EnumerateArray())
        {
            string label = op.GetProperty("label").GetString() ?? string.Empty;
            JsonElement inner = op.GetProperty("op");
            if (!label.StartsWith("context menu at ", StringComparison.Ordinal) ||
                !inner.TryGetProperty("menu", out JsonElement menu) ||
                menu.GetString() != "Context" ||
                !observed.TryGetValue(op.GetProperty("index").GetInt32(), out JsonElement result) ||
                !result.GetProperty("ok").GetBoolean())
            {
                continue;
            }

            menus[label] = ReadItems(result.GetProperty("data").GetProperty("items"));
        }

        return menus;
    }

    private static List<CapturedItem> ReadItems(JsonElement items)
    {
        var list = new List<CapturedItem>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            bool popup = item.GetProperty("popup").GetBoolean();
            list.Add(new CapturedItem(
                item.GetProperty("separator").GetBoolean() && !popup,
                popup,
                item.GetProperty("grayed").GetBoolean(),
                item.GetProperty("checked").GetBoolean(),
                popup && item.TryGetProperty("items", out JsonElement children) ? ReadItems(children) : []));
        }

        return list;
    }

    /// <summary>The sizes of the groups the separators of a captured menu make.</summary>
    private static int[] GroupsOf(IReadOnlyList<CapturedItem> items)
    {
        var sizes = new List<int> { 0 };
        foreach (CapturedItem item in items)
        {
            if (item.Separator)
            {
                sizes.Add(0);
            }
            else
            {
                sizes[^1]++;
            }
        }

        return sizes.Where(size => size > 0).ToArray();
    }

    private static IEnumerable<CapturedItem> Leaves(IEnumerable<CapturedItem> items) => items.Where(item => !item.Separator && !item.Popup);

    /// <summary>The visible standard buttons of F/Toolbar_States, without separators or the filter button.</summary>
    private static int CapturedVisibleStandardButtons()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(GoldenPath("Toolbar_States")));
        foreach (JsonElement entry in document.RootElement.GetProperty("observed").EnumerateArray())
        {
            if (entry.GetProperty("kind").GetString() != "readLayout" || !entry.GetProperty("ok").GetBoolean())
            {
                continue;
            }

            foreach (JsonElement toolbar in entry.GetProperty("data").GetProperty("toolbars").EnumerateArray())
            {
                JsonElement buttons = toolbar.GetProperty("buttons");
                if (!buttons.EnumerateArray().Any(button => button.GetProperty("command").GetInt32() == 1))
                {
                    continue;
                }

                return buttons.EnumerateArray().Count(button =>
                    !button.GetProperty("separator").GetBoolean() &&
                    !button.GetProperty("hidden").GetBoolean() &&
                    button.GetProperty("command").GetInt32() < 19);
            }
        }

        return -1;
    }
}
