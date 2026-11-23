using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.Gpedit.Filter;

/// <summary>
/// The ids of the filter and sort features that the tests use. The contract is docs/failure-modes/gpedit-filter.md: the
/// command ids continue the numbers of the G2 commands (Edit 2001, Refresh 2002, Help 2003), the toolbar button follows the
/// ConsoleToolbar_ naming, the status bar flag is the second pane, and the dialog parts are named after the dialog.
/// </summary>
internal static class FilterIds
{
    internal const int Toggle = 2004;
    internal const int Options = 2005;
    internal const int ReApply = 2006;
    internal const int ViewMenu = 59003;

    /// <summary>
    /// The View submenu of the list context menu (CTX-005, CTX-006): at the nodes that have the filter, its entries hold the
    /// filter entries between the view modes and Customize View. The entries inside it have the automation id
    /// <see cref="ViewSubmenuPrefix"/> plus the command id, so that the same command in two places stays tellable.
    /// </summary>
    internal const int ViewSubmenu = 2100;

    /// <summary>
    /// The tasks submenu (MENU-008 to MENU-010, MENU-016, CTX-007): a copy of the policy and filter entries in the Action
    /// popup and in the policy row menu. Its entries have the automation id <see cref="TasksPrefix"/> plus the command id.
    /// </summary>
    internal const int TasksSubmenu = 2101;

    internal const string TasksPrefix = "TasksCommand_";
    internal const string ViewSubmenuPrefix = "ViewSubmenuCommand_";

    /// <summary>The View menu command ids of the console frame: the four list modes and Customize View (MENU-021).</summary>
    internal static readonly int[] ViewModes = [1004, 1005, 1006, 1007];

    internal const int CustomizeView = 13231;
    internal const string ToolbarButton = "Filter";
    internal const string StatusFlag = "StatusBar.Pane1";

    internal const string Dialog = "GpeditFilterOptions";
    internal const string Managed = "GpeditFilterOptions.Managed";
    internal const string Configured = "GpeditFilterOptions.Configured";
    internal const string Commented = "GpeditFilterOptions.Commented";
    internal const string KeywordsEnabled = "GpeditFilterOptions.KeywordsEnabled";
    internal const string KeywordText = "GpeditFilterOptions.KeywordText";
    internal const string KeywordMatch = "GpeditFilterOptions.KeywordMatch";
    internal const string InTitle = "GpeditFilterOptions.InTitle";
    internal const string InHelp = "GpeditFilterOptions.InHelp";
    internal const string InComment = "GpeditFilterOptions.InComment";
    internal const string RequirementsEnabled = "GpeditFilterOptions.RequirementsEnabled";
    internal const string PlatformMatch = "GpeditFilterOptions.PlatformMatch";
    internal const string PlatformList = "GpeditFilterOptions.PlatformList";
    internal const string SelectAll = "GpeditFilterOptions.SelectAll";
    internal const string ClearAll = "GpeditFilterOptions.ClearAll";
    internal const string Ok = "GpeditFilterOptions.Ok";
    internal const string Cancel = "GpeditFilterOptions.Cancel";
    internal const string Message = "GpeditFilterOptions.Message";

    /// <summary>The parts in the tab order of the classic dialog (DLG-035), leaving out the static controls.</summary>
    internal static readonly string[] TabOrder =
    [
        Managed, Configured, Commented, KeywordsEnabled, KeywordText, KeywordMatch, InTitle, InHelp, InComment,
        RequirementsEnabled, PlatformMatch, PlatformList, SelectAll, ClearAll, Ok, Cancel,
    ];

    /// <summary>The supported-on definitions of the fixture, by the text of their ADML strings.</summary>
    internal const string PlatformAny = "BWC oracle fixture: any system";
    internal const string PlatformNewer = "BWC oracle fixture: newer systems only";
    internal const string PlatformOlder = "BWC oracle fixture: older systems only";
    internal const string PlatformLocal = "BWC oracle fixture: defined in the same file as the policy";
}

/// <summary>The values of the Filter Options dialog as one value. The defaults are Any and everything off.</summary>
internal sealed record FilterValues
{
    internal string Managed { get; init; } = "Any";

    internal string Configured { get; init; } = "Any";

    internal string Commented { get; init; } = "Any";

    internal bool Keywords { get; init; }

    internal string Text { get; init; } = string.Empty;

    internal string Match { get; init; } = "Any";

    internal bool InTitle { get; init; } = true;

    internal bool InHelp { get; init; } = true;

    internal bool InComment { get; init; }

    internal bool Requirements { get; init; }

    internal string PlatformMatch { get; init; } = "Any";

    /// <summary>The platform texts to tick; empty ticks none (Clear All).</summary>
    internal string[] Platforms { get; init; } = [];

    /// <summary>True presses Select All instead of ticking single platforms.</summary>
    internal bool SelectAllPlatforms { get; init; }
}

/// <summary>The names the filter tests use, on top of <see cref="GpeditNames"/>.</summary>
internal static class FilterNames
{
    internal const string SortCases = "Sort cases";
    internal const string Suffix = " setting";

    internal static readonly string[] SortCasesPath = [GpeditNames.Root, GpeditNames.Computer, GpeditNames.Admin, GpeditNames.Oracle, SortCases];
    internal static readonly string[] AllSettingsComputer = [GpeditNames.Root, GpeditNames.Computer, GpeditNames.Admin, GpeditNames.AllSettings];
    internal static readonly string[] AllSettingsUser = [GpeditNames.Root, GpeditNames.User, GpeditNames.Admin, GpeditNames.AllSettings];

    internal static string Row(string policy) => policy + Suffix;

    internal static string[] Rows(params string[] policies) => policies.Select(Row).ToArray();
}

/// <summary>The sandbox seed of the capture case F/Filter_Options and F/Sort_Columns.</summary>
internal static class FilterSeed
{
    private const string SortKey = @"Software\Policies\BWC-Oracle\Sort";

    /// <summary>
    /// The four saves of the capture on the Sort cases node: Zulu sort case Enabled, alpha policy Disabled, Policy 10 Not
    /// Configured with a comment, Policy 2 Enabled with a comment. The state of a plain flag is a DWORD 1 (Enabled) or 0
    /// (Disabled).
    /// </summary>
    internal static void SortCases(GpeditSandbox sandbox)
    {
        _ = sandbox.Store.Save(GpoSide.Machine, session =>
        {
            session.Pol.SetValue(SortKey, "SortZulu", PolValueType.DWord, BitConverter.GetBytes(1u));
            session.Pol.SetValue(SortKey, "SortLowerAlpha", PolValueType.DWord, BitConverter.GetBytes(0u));
            session.Pol.SetValue(SortKey, "SortDigit2", PolValueType.DWord, BitConverter.GetBytes(1u));
            session.Comments.SetComment(GpeditSandbox.Namespace, "Pol_Sort_Digit10", "bwc comment one");
            session.Comments.SetComment(GpeditSandbox.Namespace, "Pol_Sort_Digit2", "bwc comment two");
        });
    }
}

/// <summary>Helpers over a running editor for the filter and sort tests.</summary>
internal static class GpeditFilterRun
{
    internal static string[] RowNames(this GpeditRun run) => run.Rows().Select(r => r.Name).ToArray();

    /// <summary>Waits until the realized rows have exactly these names in this order, or the time is up.</summary>
    internal static string[] WaitRowsInOrder(this GpeditRun run, IReadOnlyList<string> expected, double seconds = 10)
    {
        string[] last = run.RowNames();
        _ = ShellRun.WaitUntil(() => (last = run.RowNames()).SequenceEqual(expected), TimeSpan.FromSeconds(seconds));
        return last;
    }

    /// <summary>Waits until the realized rows are these names in any order.</summary>
    internal static string[] WaitRowsAsSet(this GpeditRun run, IReadOnlyList<string> expected, double seconds = 10)
    {
        string[] last = run.RowNames();
        _ = ShellRun.WaitUntil(
            () => (last = run.RowNames()).Length == expected.Count && !last.Except(expected, StringComparer.Ordinal).Any(),
            TimeSpan.FromSeconds(seconds));
        return last;
    }

    internal static Dictionary<string, string> ListReadout(this GpeditRun run)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in run.Shell.HelpTextOf(GpeditRun.ListId).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                map[part[..at]] = part[(at + 1)..];
            }
        }

        return map;
    }

    /// <summary>The numbers in the status bar text, in order.</summary>
    internal static int[] StatusNumbers(this GpeditRun run) =>
        Regex.Matches(run.Shell.StatusText(), @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();

    internal static bool WaitStatusNumbers(this GpeditRun run, params int[] expected) =>
        ShellRun.WaitUntil(() => run.StatusNumbers().SequenceEqual(expected), TimeSpan.FromSeconds(8));

    /// <summary>True while the status bar flag pane exists and has text.</summary>
    internal static bool StatusFlagShown(this GpeditRun run)
    {
        AutomationElement? flag = run.Shell.Find(FilterIds.StatusFlag);
        return flag is not null && ShellRun.SafeName(flag).Length > 0 && run.Shell.Rect(FilterIds.StatusFlag) is not null;
    }

    internal static bool WaitFilterOn(this GpeditRun run, bool on) =>
        ShellRun.WaitUntil(() => run.Count("FilterOn") == (on ? 1 : 0), TimeSpan.FromSeconds(10));

    /// <summary>The Filter toolbar button when it is on screen, or null.</summary>
    internal static AutomationElement? FilterButton(this GpeditRun run)
    {
        AutomationElement? button = run.Shell.Find(ShellRun.ButtonId(FilterIds.ToolbarButton));
        return button is not null && run.Shell.Rect(ShellRun.ButtonId(FilterIds.ToolbarButton)) is not null ? button : null;
    }

    internal static bool WaitFilterButton(this GpeditRun run, bool present) =>
        ShellRun.WaitUntil(() => (run.FilterButton() is not null) == present, TimeSpan.FromSeconds(3));

    /// <summary>The toggle state of an element: true when On, false when Off, null when it has no toggle pattern.</summary>
    internal static bool? ToggledOn(AutomationElement? element)
    {
        try
        {
            FlaUI.Core.Patterns.ITogglePattern? toggle = element?.Patterns.Toggle.PatternOrDefault;
            return toggle is null ? null : toggle.ToggleState.Value == ToggleState.On;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Waits until the Filter Options window is open (or closed).</summary>
    internal static bool WaitDialog(this GpeditRun run, bool open, double seconds = 8)
    {
        using var driver = new EditingDriver(run.Shell.Session);
        return ShellRun.WaitUntil(
            () => (driver.WaitForElement(FilterIds.Dialog, TimeSpan.FromMilliseconds(200)) is not null) == open,
            TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Presses Cancel of an open Filter Options window.</summary>
    internal static void CancelDialog(this GpeditRun run)
    {
        using var driver = new EditingDriver(run.Shell.Session);
        _ = driver.Invoke(FilterIds.Cancel);
        _ = run.WaitDialog(open: false);
    }

    /// <summary>Opens a submenu of the open Action or View popup by its command id and returns its entries.</summary>
    internal static List<GpeditMenuEntry> OpenPopupSubmenu(this GpeditRun run, int submenuId, string childPrefix)
    {
        try
        {
            using var driver = new EditingDriver(run.Shell.Session);
            AutomationElement? parent = driver.WaitForElement("MenuCommand_" + submenuId, TimeSpan.FromSeconds(3));
            if (parent is null || !parent.Patterns.ExpandCollapse.IsSupported)
            {
                return [];
            }

            parent.Patterns.ExpandCollapse.Pattern.Expand();
        }
        catch (Exception)
        {
            return [];
        }

        var entries = new List<GpeditMenuEntry>();
        _ = ShellRun.WaitUntil(() => (entries = run.MenuEntries(childPrefix)).Count > 0, TimeSpan.FromSeconds(4));
        return entries;
    }

    /// <summary>Opens a submenu of the open list context menu by its command id and returns its entries.</summary>
    internal static List<GpeditMenuEntry> OpenContextSubmenu(this GpeditRun run, ContextMenuDriver context, int submenuId, string childPrefix)
    {
        if (!context.OpenSubmenu(submenuId))
        {
            return [];
        }

        var entries = new List<GpeditMenuEntry>();
        _ = ShellRun.WaitUntil(() => (entries = run.MenuEntries(childPrefix)).Count > 0, TimeSpan.FromSeconds(4));
        return entries;
    }

    /// <summary>
    /// Records the checks of a submenu that must exist: its entry is among the entries of the menu (and after the flat entries
    /// when <paramref name="afterFlat"/>), and the entries inside have the filter entries in order, and Edit first when the
    /// submenu belongs to a policy row.
    /// </summary>
    internal static void CheckSubmenu(
        GpeditChecks checks,
        string id,
        IReadOnlyList<GpeditMenuEntry> menu,
        int submenuId,
        IReadOnlyList<GpeditMenuEntry> children,
        bool filterOn,
        bool withEdit,
        bool afterFlat,
        string where)
    {
        GpeditMenuEntry? parent = menu.FirstOrDefault(e => e.Id == submenuId);
        string listing = string.Join("|", menu.Select(e => $"{e.Id}:{e.Name}:{(e.Enabled ? "on" : "off")}"));
        checks.Record(id + ".submenuPresent", parent is { Enabled: true }, $"{where}: submenu {submenuId} is present and enabled; entries={listing}.");
        if (afterFlat)
        {
            GpeditMenuEntry? last = menu.FirstOrDefault(e => e.Id == FilterIds.ReApply);
            checks.Record(id + ".submenuAfterFlat", parent is not null && last is not null && parent.Top > last.Top, $"{where}: the submenu follows the flat filter entries; submenu top {parent?.Top}, re-apply top {last?.Top}.");
        }

        CheckFilterEntries(checks, id + ".inside", children, expected: true, filterOn, where + " (inside the submenu)");
        if (withEdit)
        {
            checks.Record(id + ".inside.edit", children.Count > 0 && children[0].Id == GpeditCommandIds.Edit && children[0].Enabled, $"{where}: the submenu starts with Edit; entries={string.Join("|", children.Select(e => e.Id + ":" + e.Name))}.");
        }
    }

    /// <summary>Records that a submenu entry is absent from a menu.</summary>
    internal static void CheckSubmenuAbsent(GpeditChecks checks, string id, IReadOnlyList<GpeditMenuEntry> menu, int submenuId, string where) =>
        checks.Record(id + ".submenuAbsent", menu.All(e => e.Id != submenuId), $"{where}: no submenu {submenuId}; entries={string.Join("|", menu.Select(e => e.Id + ":" + e.Name))}.");

    /// <summary>Runs Filter On from the Action menu and waits for the state to change.</summary>
    internal static bool ToggleFilterByMenu(this GpeditRun run)
    {
        int before = run.Count("FilterOn");
        return run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FilterIds.Toggle) &&
            ShellRun.WaitUntil(() => run.Count("FilterOn") != before, TimeSpan.FromSeconds(8));
    }

    /// <summary>Sets the filter by the dialog and presses OK; false when the dialog did not open or close.</summary>
    internal static bool ApplyFilter(this GpeditRun run, FilterValues values)
    {
        using GpeditFilterDialog? dialog = GpeditFilterDialog.Open(run);
        if (dialog is null)
        {
            return false;
        }

        dialog.Set(values);
        return dialog.PressOk() && dialog.WaitClosed();
    }

    /// <summary>The ids of the filter entries of the entries, in screen order.</summary>
    internal static int[] FilterEntryIds(IReadOnlyList<GpeditMenuEntry> entries) =>
        entries.Where(e => e.Id is FilterIds.Toggle or FilterIds.Options or FilterIds.ReApply).Select(e => e.Id).ToArray();

    /// <summary>
    /// Records the checks of the filter entries of one menu: present in the order toggle, options, re-apply, or all absent;
    /// when present, toggle and options enabled and re-apply enabled as the filter state says; labelled with the BWC words.
    /// </summary>
    internal static void CheckFilterEntries(
        GpeditChecks checks,
        string id,
        IReadOnlyList<GpeditMenuEntry> entries,
        bool expected,
        bool filterOn,
        string where)
    {
        int[] ids = FilterEntryIds(entries);
        string listing = string.Join("|", entries.Select(e => $"{e.Id}:{e.Name}:{(e.Enabled ? "on" : "off")}"));
        if (!expected)
        {
            checks.Record(id + ".absent", ids.Length == 0, $"{where}: no filter entry; entries={listing}.");
            return;
        }

        checks.Record(id + ".present", ids.SequenceEqual([FilterIds.Toggle, FilterIds.Options, FilterIds.ReApply]), $"{where}: toggle, options and re-apply in this order; entries={listing}.");
        GpeditMenuEntry? toggle = entries.FirstOrDefault(e => e.Id == FilterIds.Toggle);
        GpeditMenuEntry? options = entries.FirstOrDefault(e => e.Id == FilterIds.Options);
        GpeditMenuEntry? reapply = entries.FirstOrDefault(e => e.Id == FilterIds.ReApply);
        checks.Record(id + ".enabled", toggle is { Enabled: true } && options is { Enabled: true }, $"{where}: Filter On and Filter Options are enabled; entries={listing}.");
        checks.Record(
            id + ".reapply",
            reapply is not null && reapply.Enabled == filterOn,
            $"{where}: Re-Apply Filter is {(filterOn ? "enabled" : "grayed")} while the filter is {(filterOn ? "on" : "off")}; entries={listing}.");
        checks.Record(
            id + ".labels",
            (toggle?.Name.StartsWith("Filter On", StringComparison.Ordinal) ?? false) &&
            (options?.Name.StartsWith("Filter Options", StringComparison.Ordinal) ?? false) &&
            (reapply?.Name.StartsWith("Re-Apply Filter", StringComparison.Ordinal) ?? false),
            $"{where}: the entries are labelled Filter On, Filter Options and Re-Apply Filter; entries={listing}.");
    }
}

/// <summary>
/// The Filter Options dialog of a running editor, seen through UI Automation. It is a separate top level window of the
/// editor process, found by the automation ids of <see cref="FilterIds"/>.
/// </summary>
internal sealed class GpeditFilterDialog : IDisposable
{
    private const ushort VkTab = 0x09;

    private readonly GpeditRun _run;
    private readonly EditingDriver _driver;

    private GpeditFilterDialog(GpeditRun run, EditingDriver driver)
    {
        _run = run;
        _driver = driver;
    }

    internal GpeditRun Run => _run;

    /// <summary>Opens the dialog by the Action menu entry Filter Options. Null when no dialog appeared.</summary>
    internal static GpeditFilterDialog? Open(GpeditRun run, double seconds = 10)
    {
        var driver = new EditingDriver(run.Shell.Session);
        var dialog = new GpeditFilterDialog(run, driver);
        if (!run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FilterIds.Options) || driver.WaitForElement(FilterIds.Dialog, TimeSpan.FromSeconds(seconds)) is null)
        {
            dialog.Dispose();
            return null;
        }

        return dialog;
    }

    public void Dispose() => _driver.Dispose();

    internal bool IsOpen => _driver.WaitForElement(FilterIds.Dialog, TimeSpan.FromMilliseconds(300)) is not null;

    internal AutomationElement? Root => _driver.WaitForElement(FilterIds.Dialog, TimeSpan.FromSeconds(3));

    internal AutomationElement? Find(string id, double seconds = 3) => _driver.WaitForElement(id, TimeSpan.FromSeconds(seconds));

    internal bool Has(string id) => Find(id, 1) is not null;

    internal bool IsEnabled(string id) => Find(id, 1)?.IsEnabled ?? false;

    internal bool WaitClosed() => ShellRun.WaitUntil(() => !IsOpen, TimeSpan.FromSeconds(10));

    internal Rectangle? RectOf(string id)
    {
        try
        {
            Rectangle box = Find(id, 1)?.BoundingRectangle ?? Rectangle.Empty;
            return box.Width > 0 && box.Height > 0 ? box : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal string Combo(string id)
    {
        try
        {
            return Find(id, 2)?.AsComboBox().SelectedItem?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal string[] ComboItems(string id)
    {
        try
        {
            return Find(id, 2)?.AsComboBox().Items.Select(i => i.Name).ToArray() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal bool Checked(string id) => FilterChecked(Find(id, 2));

    internal string Text(string id) => _driver.TextOf(id);

    internal bool Choose(string id, string item)
    {
        try
        {
            Find(id, 2)?.AsComboBox().Select(item);
            Thread.Sleep(250);
            return Combo(id) == item;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool SetChecked(string id, bool wanted)
    {
        try
        {
            AutomationElement? box = Find(id, 2);
            if (box is null)
            {
                return false;
            }

            if (FilterChecked(box) != wanted)
            {
                box.Patterns.Toggle.Pattern.Toggle();
                Thread.Sleep(200);
            }

            return FilterChecked(box) == wanted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool SetText(string id, string text)
    {
        bool done = _driver.SetText(id, text);
        Thread.Sleep(200);
        return done;
    }

    internal bool Press(string id)
    {
        bool done = _driver.Invoke(id);
        Thread.Sleep(300);
        return done;
    }

    internal bool PressOk() => Press(FilterIds.Ok);

    internal bool PressCancel() => Press(FilterIds.Cancel) && WaitClosed();

    /// <summary>The platform check boxes: UIA name and ticked state, in list order.</summary>
    internal List<(string Name, bool Ticked)> Platforms()
    {
        var items = new List<(string, bool)>();
        try
        {
            foreach (AutomationElement item in Find(FilterIds.PlatformList, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)) ?? [])
            {
                items.Add((ShellRun.SafeName(item), FilterChecked(item)));
            }
        }
        catch (Exception)
        {
            // A list that is rebuilding returns what was read; the caller polls.
        }

        return items;
    }

    internal bool SetPlatform(string name, bool ticked)
    {
        try
        {
            AutomationElement? item = Find(FilterIds.PlatformList, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(i => ShellRun.SafeName(i) == name);
            if (item is null)
            {
                return false;
            }

            if (FilterChecked(item) != ticked)
            {
                item.Patterns.Toggle.Pattern.Toggle();
                Thread.Sleep(200);
            }

            return FilterChecked(item) == ticked;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sets every control to the values. A part that is switched off keeps its detail controls untouched.</summary>
    internal bool Set(FilterValues v)
    {
        bool ok = Choose(FilterIds.Managed, v.Managed) & Choose(FilterIds.Configured, v.Configured) & Choose(FilterIds.Commented, v.Commented);
        ok &= SetChecked(FilterIds.KeywordsEnabled, v.Keywords);
        if (v.Keywords)
        {
            ok &= SetText(FilterIds.KeywordText, v.Text) & Choose(FilterIds.KeywordMatch, v.Match);
            ok &= SetChecked(FilterIds.InTitle, v.InTitle) & SetChecked(FilterIds.InHelp, v.InHelp) & SetChecked(FilterIds.InComment, v.InComment);
        }

        ok &= SetChecked(FilterIds.RequirementsEnabled, v.Requirements);
        if (v.Requirements)
        {
            ok &= Choose(FilterIds.PlatformMatch, v.PlatformMatch);
            ok &= Press(v.SelectAllPlatforms ? FilterIds.SelectAll : FilterIds.ClearAll);
            foreach (string platform in v.Platforms)
            {
                ok &= SetPlatform(platform, true);
            }
        }

        return ok;
    }

    /// <summary>The part ids that hold the keyboard focus after each of <paramref name="count"/> Tab presses.</summary>
    internal string[] TabSequence(int count)
    {
        var ids = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string before = _run.Shell.FocusedId();

            // The keys go to the dialog window, so the dialog is the input target (the main window is not foreground).
            _ = ClassicDialogTestsBridge.EnsureForeground(DialogHandle());
            Bwc.E2E.List.RawKeyboard.Tap(VkTab);
            Thread.Sleep(150);
            _ = ShellRun.WaitUntil(() => _run.Shell.FocusedId() != before, TimeSpan.FromSeconds(1));
            ids.Add(PartOf(_run.Shell.FocusedId()));
        }

        return ids.ToArray();
    }

    internal string FocusedPart() => PartOf(_run.Shell.FocusedId());

    internal bool Focus(string id)
    {
        try
        {
            _ = ClassicDialogTestsBridge.EnsureForeground(DialogHandle());
            Find(id, 2)?.Focus();
            Thread.Sleep(200);
            return FocusedPart() == id;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal IntPtr DialogHandle() => Root is { } root ? ClassicDialogTestsBridge.HandleOf(root) : IntPtr.Zero;

    /// <summary>The text of the message box over the dialog, or null when none shows.</summary>
    internal string? WaitMessage(double seconds = 6)
    {
        AutomationElement? message = Find(FilterIds.Message, seconds);
        return message is null ? null : ShellRun.SafeName(message);
    }

    /// <summary>Closes the message box with Enter and waits until it is gone.</summary>
    internal bool DismissMessage()
    {
        // The message box is its own window over the dialog: it is the input target, not the dialog and not the main window.
        AutomationElement? message = Find(FilterIds.Message, 2);
        IntPtr target = message is null ? DialogHandle() : ClassicDialogTestsBridge.HandleOf(message);
        _ = ClassicDialogTestsBridge.EnsureForeground(target);
        Bwc.E2E.List.RawKeyboard.Tap(GpeditKeys.Enter);
        Thread.Sleep(300);
        return ShellRun.WaitUntil(() => Find(FilterIds.Message, 0.3) is null, TimeSpan.FromSeconds(8));
    }

    internal string Screenshot(string name)
    {
        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", GpeditRun.Area);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        try
        {
            return Root is { } root ? ClassicDialogTestsBridge.SaveScreenshot(root, path) : string.Empty;
        }
        catch (Exception ex)
        {
            E2EResults.Note("gpedit.filter.capture." + name, $"picture not written: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool FilterChecked(AutomationElement? element)
    {
        try
        {
            return element?.Patterns.Toggle.PatternOrDefault?.ToggleState.Value == ToggleState.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string PartOf(string focusedId)
    {
        string best = string.Empty;
        foreach (string part in FilterIds.TabOrder)
        {
            if (focusedId.StartsWith(part, StringComparison.Ordinal) && part.Length > best.Length)
            {
                best = part;
            }
        }

        return best.Length > 0 ? best : focusedId;
    }
}
