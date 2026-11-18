using Bwc.E2E.Controls;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.List;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Menus;

/// <summary>
/// The menu keyboard runs a leaf's command through MenuItemActions. The action must survive the collection of the managed
/// wrapper of the item, an item removed and inserted again, and two menus with an item of the same text. The failure modes
/// are in docs/failure-modes/menus.md (section "action lookup by item identity"). The host is the Local Group Policy
/// Editor with the E2E-only seam BWC_E2E_MENU_GC, which forces the collection.
/// </summary>
[TestClass]
public sealed class MenuActionLookupTests
{
    private const ushort VkF = 0x46;
    private const ushort VkM = 0x4D;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_ActionLookupSurvivesWrapperCollection()
    {
        var checks = new ShellChecks("actionlookup", "menus");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BWC_E2E_MENU_GC"] = "1" };
        using ListRun run = ListRun.Open("menu-gc", 8, environment);
        run.FocusList();
        checks.Record("startFocus", run.Shell.FocusedPane() == "result", $"focused pane before the menu={run.Shell.FocusedPane()}.");

        // The seam entry (View, access key P) removes and re-inserts the List radio, drops the wrappers and collects.
        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        run.Shell.Chord(RawKeyboard.Alt, ShellRun.VkV);
        _ = ShellRun.WaitUntil(() => run.Shell.Find("MenuCommand_1202") is not null, TimeSpan.FromSeconds(5));
        run.Shell.Tap(0x50);
        bool collectRan = ShellRun.WaitUntil(() => run.Shell.StatusText().StartsWith("Wrapper GC:", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        string gcStatus = run.Shell.StatusText();
        checks.Record("collectRan", collectRan, $"status after the seam entry='{gcStatus}'.");
        int collected = Parse(gcStatus, "collected=");
        int registered = Parse(gcStatus, " of ");
        int resolved = Parse(gcStatus, "resolved=");
        int resolvedBefore = Parse(gcStatus, "resolvedBefore=");
        int leaves = Parse(gcStatus, "; resolved=" + resolved.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of ");
        run.Measure("gcStatus", gcStatus);
        run.Measure("wrappersCollected", collected);
        run.Measure("wrappersRegistered", registered);
        run.Measure("resolvedBeforeCollection", resolvedBefore);
        run.Measure("resolvedAfterCollection", resolved);

        // Every enabled leaf of the menu must still find its action after the forced collection.
        checks.Record(
            "allLeavesResolve",
            leaves > 0 && resolved == leaves,
            $"leaves that find their action by the lookup the menu keyboard uses: {resolved} of {leaves}; wrappers that registered an action and were collected: {collected} of {registered} ({gcStatus}).");
        checks.Record("focusAfterGcCommand", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result"), $"focused pane after the seam command={run.Shell.FocusedPane()}; it was the result pane before Alt+V.");
        run.FocusList();

        // Every view mode by its access key, after the collection, one of them the re-inserted item.
        (string Mode, char Key)[] modes = [("LargeIcons", 'g'), ("SmallIcons", 'm'), ("List", 'l'), ("Detail", 'd')];
        foreach ((string mode, char key) in modes)
        {
            bool switched = run.SwitchView(key, mode);
            checks.Record(mode + "Mode", switched, $"View menu access key '{key}' after the collection gives mode '{run.Mode}'; expected '{mode}'.");
            checks.Record(mode + "Focus", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result"), $"focused pane after the command={run.Shell.FocusedPane()}.");

            // A command that left the focus on the menu bar must not hide the next check: put it back by hand.
            run.FocusList();
        }

        // File and View each have S&mall Icons. File runs its own action and leaves the mode; View sets the mode only.
        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        run.Shell.Chord(RawKeyboard.Alt, VkF);
        _ = ShellRun.WaitUntil(() => run.Shell.Find("MenuCommand_1210") is not null, TimeSpan.FromSeconds(5));
        run.Shell.Tap(VkM);
        run.FocusList();
        bool fileRan = ShellRun.WaitUntil(() => run.Shell.StatusText() == "File SmallIcons ran", TimeSpan.FromSeconds(8));
        checks.Record("fileEntry", fileRan, $"status after File, M='{run.Shell.StatusText()}'.");
        checks.Record("fileEntryKeepsMode", run.Mode == "Detail", $"view mode after File, M={run.Mode}; the File entry must not change it.");

        bool viewSwitched = run.SwitchView('m', "SmallIcons");
        checks.Record("viewEntry", viewSwitched, $"mode after View, M={run.Mode}.");
        checks.Record("viewEntryKeepsStatus", run.Shell.StatusText() == "File SmallIcons ran", $"status after View, M='{run.Shell.StatusText()}'; the View entry must not run the File action.");

        run.Measure("modeAtEnd", run.Mode);
        run.Shell.WriteArtifacts("menus-actionlookup.png", "menus");
        checks.AssertAllPassed();
    }

    private static int Parse(string status, string marker)
    {
        int at = status.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return -1;
        }

        int start = at + marker.Length;
        int end = start;
        while (end < status.Length && char.IsAsciiDigit(status[end]))
        {
            end++;
        }

        return int.TryParse(status[start..end], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : -1;
    }
}
