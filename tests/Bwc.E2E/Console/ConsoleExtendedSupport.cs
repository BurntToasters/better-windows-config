using System.Drawing;
using System.Globalization;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// One running Local Group Policy Editor whose result pane is the Extended view around the console list, and the helpers
/// the Console_Extended tests share. The host selects a placeholder description for the selected row (title is the row
/// name, a link, a Requirements and a Description section) or, with no single row selected, for the selected tree node
/// (title is the node name and one hint). BWC_E2E_CONSOLE_HELP changes the help text (empty, long, markup, wide; see
/// GpeditRootView). The view reports its state in the UIA help text of ConsoleExtendedView (E2E flavor only).
/// </summary>
internal sealed class ExtendedRun : IDisposable
{
    internal const string ViewId = "ConsoleExtendedView";
    internal const string PaneId = "ConsoleDescriptionPane";
    internal const string ScrollId = "ConsoleDescriptionScroll";
    internal const string TitleId = "ConsoleDescriptionTitle";
    internal const string TabsId = "ConsoleExtendedTabs";
    internal const string ExtendedTabId = "ConsoleExtendedTab_Extended";
    internal const string StandardTabId = "ConsoleExtendedTab_Standard";
    internal const string LinkId = "ConsoleDescriptionLink_edit";
    internal const string HintText = "Select an item to view its description.";

    private ExtendedRun(ListRun list)
    {
        List = list;
    }

    internal ListRun List { get; }

    internal ShellRun Shell => List.Shell;

    internal double Scale => List.Scale;

    internal static ExtendedRun Open(string name, int rows, IReadOnlyDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BWC_E2E_CONSOLE_TAB"] = "extended" };
        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> pair in extra)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        ListRun list = ListRun.Open("ext-" + name, rows, environment);
        var run = new ExtendedRun(list);
        try
        {
            bool ready = ShellRun.WaitUntil(() => run.Shell.Find(ViewId) is not null && run.Str("Tab").Length > 0, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                throw new TimeoutException($"The Extended view did not report its state. Readout: '{run.RawReadout()}'.");
            }

            return run;
        }
        catch
        {
            list.Dispose();
            throw;
        }
    }

    public void Dispose() => List.Dispose();

    // ---- readout ----------------------------------------------------------------------------------

    internal string RawReadout() => Shell.HelpTextOf(ViewId);

    internal string Str(string key)
    {
        foreach (string part in RawReadout().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && part[..at] == key)
            {
                return part[(at + 1)..];
            }
        }

        return string.Empty;
    }

    internal double Number(string key) =>
        double.TryParse(Str(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    internal string Tab => Str("Tab");

    // ---- elements ---------------------------------------------------------------------------------

    internal AutomationElement? Find(string id) => Shell.Find(id);

    internal string NameOf(string id) => ShellRun.SafeName(Find(id));

    internal string Title => NameOf(TitleId);

    internal Rectangle? Rect(string id) => Shell.Rect(id);

    internal double Logical(double physical) => physical / Scale;

    /// <summary>The expected name of the title for list row index i (the seam names row i by a permutation).</summary>
    internal string RowTitle(int index) => ListRun.RowName(index, List.RowCount);

    /// <summary>True when the pane shows the placeholder description of a single row.</summary>
    internal bool ShowsRow(int index) => Title == RowTitle(index) && Find(LinkId) is not null;

    internal bool ShowsCategory() => Find(LinkId) is null && Find("ConsoleDescriptionText_hint") is not null;

    /// <summary>The descendants of the pane that UI Automation reports as Text, with their names.</summary>
    internal List<(string Id, string Name)> PaneTexts()
    {
        var texts = new List<(string, string)>();
        AutomationElement? pane = Find(PaneId);
        if (pane is null)
        {
            return texts;
        }

        foreach (AutomationElement element in pane.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
        {
            texts.Add((ShellRun.SafeId(element), ShellRun.SafeName(element)));
        }

        return texts;
    }

    // ---- keys -------------------------------------------------------------------------------------

    /// <summary>Focuses the list, goes to the first row and waits until the pane shows it.</summary>
    internal void SelectFirstRow()
    {
        List.FocusList();
        Shell.Tap(ListKeys.Home);
        bool shown = ShellRun.WaitUntil(() => ShowsRow(List.Focus < 0 ? 0 : List.Focus), TimeSpan.FromSeconds(8));
        if (!shown)
        {
            throw new InvalidOperationException($"The pane did not show the first row. Title='{Title}', readout='{List.RawReadout()}'.");
        }
    }

    /// <summary>Ctrl+Tab (or Ctrl+Shift+Tab) with the key state a real keyboard gives.</summary>
    internal void CtrlTab(bool shift = false)
    {
        if (shift)
        {
            RawKeyboard.WithHeld(RawKeyboard.Shift, () => Shell.Chord(RawKeyboard.Control, ShellRun.VkTab));
        }
        else
        {
            Shell.Chord(RawKeyboard.Control, ShellRun.VkTab);
        }
    }

    internal void WriteArtifacts(string picture) => Shell.WriteArtifacts(picture, "console-extended");

    internal void Measure(string key, object? value) => Shell.Measure(key, value);
}
