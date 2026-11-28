using System.Drawing;
using System.Globalization;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Persistence tests of the Local Group Policy Editor: the window rectangle, the tree width, a column width, the view mode
/// and the Extended or Standard tab survive a restart on the same data root; the last node does not (classic opens on the
/// root); a settings file that is corrupt or holds absurd numbers never stops the window. The failure modes are in
/// docs/failure-modes/gpedit-tree-list.md, section 11. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture; the restart test also records the settings file
/// the first run wrote.
/// </summary>
[TestClass]
public sealed class GpeditPersistenceTests
{
    private const string Root = GpeditRun.RootName;
    private const string Computer = "Computer Configuration";
    private const string Admin = "Administrative Templates";
    private const string Oracle = "BWC Oracle";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Persistence_Restart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("persistence", "restart");
        const string name = "gpedit-persist-restart";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        Rectangle windowBefore;
        double treeBefore;
        double settingBefore;
        string tabBefore;
        string modeBefore;
        double scale;
        using (GpeditRun first = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            scale = first.Scale;
            first.Shell.PlaceWindow(140, 90, Math.Min(1700, ShellRun.ScreenWidth - 200), 800);
            first.GoTo(Root, Computer, Admin, Oracle, "Basics");

            // The Standard tab first: it gives the list the whole pane, so the divider after the Setting column is on screen.
            first.Shell.Find("ConsoleExtendedTab_Standard")?.Click();
            checks.Record("tabChanged", ShellRun.WaitUntil(() => TabOf(first) == "Standard"), $"tab={TabOf(first)}.");
            tabBefore = TabOf(first);

            // A column width: drag the divider after the Setting column to the right.
            Rectangle? divider = first.Rect(GpeditRun.DividerId("setting"));
            checks.Record("dividerFound", divider is not null, "the divider after the Setting column has a rectangle.");
            if (divider is { } d)
            {
                var start = new Point(d.Left + (d.Width / 2), d.Top + (d.Height / 2));
                ShellRun.DragStart(start, (int)Math.Round(60 * scale));
                ShellRun.DragEnd();
            }

            settingBefore = HeaderWidth(first, "setting");

            // The tree width: drag the splitter to the right.
            Rectangle? splitter = first.Rect(ShellRun.SplitterId);
            Rectangle? tree = first.Rect(ShellRun.TreePaneId);
            checks.Record("splitterFound", splitter is not null && tree is not null, "the splitter and the tree pane have rectangles.");
            if (splitter is { } s)
            {
                var start = new Point(s.Left + (s.Width / 2), s.Top + (s.Height / 2));
                ShellRun.DragStart(start, (int)Math.Round(70 * scale));
                ShellRun.DragEnd();
            }

            treeBefore = first.Rect(ShellRun.TreePaneId) is { } treeRect ? treeRect.Width / scale : -1;

            // The view mode: Small Icons from the View menu.
            _ = ControlTestSupport.EnsureForeground(first.Shell.Session);
            first.Shell.Chord(RawKeyboard.Alt, ShellRun.VkV);
            _ = ShellRun.WaitUntil(() => first.Shell.Find("MenuCommand_1004") is not null, TimeSpan.FromSeconds(5));
            first.Shell.Tap((ushort)'M');
            checks.Record("modeChanged", ShellRun.WaitUntil(() => ModeOf(first) == "SmallIcons"), $"mode={ModeOf(first)}.");
            modeBefore = ModeOf(first);
            windowBefore = first.Shell.WindowRect();
            first.Measure("treeWidthDipBefore", treeBefore);
            first.Measure("settingWidthDipBefore", settingBefore);
            first.Measure("windowBefore", windowBefore.ToString());
            first.WriteArtifacts("gpedit-persist-restart-first.png");
        }

        string? file = sandbox.SettingsFilePath();
        checks.Record("settingsFileWritten", file is not null && new FileInfo(file).Length > 0, $"settings file='{file}'.");
        string settingsText = file is null ? string.Empty : File.ReadAllText(file);
        checks.Record("settingsHoldView", settingsText.Contains("SmallIcons", StringComparison.Ordinal) && settingsText.Contains("Standard", StringComparison.Ordinal), "the file names the view mode and the tab.");

        using (GpeditRun second = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            checks.Record("startsOnRoot", second.NodePath().SequenceEqual([Root]), $"node={second.Str("Node")}; the last node is not restored.");
            checks.Record("tabRestored", ShellRun.WaitUntil(() => TabOf(second) == tabBefore), $"tab={TabOf(second)}; expected {tabBefore}.");
            checks.Record("modeRestored", ShellRun.WaitUntil(() => ModeOf(second) == modeBefore), $"mode={ModeOf(second)}; expected {modeBefore}.");

            Rectangle windowAfter = default;
            bool placed = ShellRun.WaitUntil(
                () =>
                {
                    windowAfter = second.Shell.WindowRect();
                    return Math.Abs(windowAfter.Left - windowBefore.Left) <= 4 && Math.Abs(windowAfter.Top - windowBefore.Top) <= 4 &&
                        Math.Abs(windowAfter.Width - windowBefore.Width) <= 6 && Math.Abs(windowAfter.Height - windowBefore.Height) <= 6;
                },
                TimeSpan.FromSeconds(10));
            checks.Record("windowRestored", placed, $"window before={windowBefore}, after={windowAfter}.");

            double treeAfter = second.Rect(ShellRun.TreePaneId) is { } treeRect ? treeRect.Width / second.Scale : -1;
            checks.Record("treeWidthRestored", Math.Abs(treeAfter - treeBefore) <= 3, $"tree width before={treeBefore:0.#} DIP, after={treeAfter:0.#} DIP.");

            // The Setting column width shows in the list header, which Small Icons hides: switch back to Detail first.
            _ = ControlTestSupport.EnsureForeground(second.Shell.Session);
            second.GoTo(Root, Computer, Admin, Oracle, "Basics");
            second.Shell.Chord(RawKeyboard.Alt, ShellRun.VkV);
            _ = ShellRun.WaitUntil(() => second.Shell.Find("MenuCommand_1004") is not null, TimeSpan.FromSeconds(5));
            second.Shell.Tap((ushort)'D');
            _ = ShellRun.WaitUntil(() => ModeOf(second) == "Detail");
            double settingAfter = HeaderWidth(second, "setting");
            checks.Record("columnWidthRestored", Math.Abs(settingAfter - settingBefore) <= 3 && settingAfter > 380, $"Setting width before={settingBefore:0.#} DIP, after={settingAfter:0.#} DIP; default 350.");

            second.Measure("treeWidthDipAfter", treeAfter);
            second.Measure("settingWidthDipAfter", settingAfter);
            second.Measure("windowAfter", windowAfter.ToString());
            second.Measure("settingsFile", settingsText);
            second.WriteArtifacts("gpedit-persist-restart.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Persistence_BadSettingsFile()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("persistence", "badfile");
        const string name = "gpedit-persist-bad";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        // Absurd numbers and unknown names, as a hand edited file could hold them.
        sandbox.WriteSettingsFile("""
            {
              "WindowX": -32000,
              "WindowY": -32000,
              "WindowWidth": 0,
              "WindowHeight": 99999999,
              "WindowMaximized": false,
              "TreePaneWidth": 1000000000,
              "ColumnWidths": { "setting": -5, "state": 0, "comment": 1e12 },
              "ViewMode": "Nope",
              "ExtendedTab": "Nope"
            }
            """);
        using (GpeditRun run = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            Rectangle window = run.Shell.WindowRect();
            checks.Record("windowOnScreen", window.Width >= 300 && window.Height >= 200 && window.Right > 100 && window.Left < ShellRun.ScreenWidth - 100, $"window={window}.");
            checks.Record("defaultMode", ModeOf(run) == "Detail", $"mode={ModeOf(run)}; the unknown name is ignored.");
            checks.Record("defaultTab", TabOf(run) == "Extended", $"tab={TabOf(run)}; the unknown name is ignored.");
            double tree = run.Rect(ShellRun.TreePaneId) is { } rect ? rect.Width / run.Scale : -1;
            checks.Record("treeUsable", tree > 20 && tree < 4000, $"tree width={tree:0.#} DIP.");
            run.GoTo(Root, Computer, Admin, Oracle, "Basics");
            double setting = HeaderWidth(run, "setting");
            checks.Record("columnsUsable", setting >= 20 && setting < 2000, $"Setting width={setting:0.#} DIP; the bad numbers fall back to the default.");
            run.Measure("window", window.ToString());
            run.WriteArtifacts("gpedit-persist-badfile.png");
        }

        // A file that is not JSON at all.
        GpeditSandbox corrupt = GpeditSandbox.Create("gpedit-persist-corrupt");
        corrupt.SeedStandard();
        corrupt.WriteSettingsFile("{ this is not json");
        using (GpeditRun run = GpeditRun.Open("gpedit-persist-corrupt", corrupt))
        {
            checks.Record("corruptFileStarts", run.Int("Loaded") == 1 && run.NodePath().SequenceEqual([Root]), $"readout='{run.RawReadout()}'.");
            run.WriteArtifacts("gpedit-persist-corrupt.png");
        }

        // A map that is null in the file (hand edited): the same as no entry, not a crash at start-up.
        GpeditSandbox nulls = GpeditSandbox.Create("gpedit-persist-nulls");
        nulls.SeedStandard();
        nulls.WriteSettingsFile("""{ "ColumnWidths": null, "Sorts": null, "HiddenColumns": null, "ColumnOrder": null }""");
        using (GpeditRun run = GpeditRun.Open("gpedit-persist-nulls", nulls))
        {
            checks.Record("nullMapsStart", run.Int("Loaded") == 1 && run.NodePath().SequenceEqual([Root]), $"readout='{run.RawReadout()}'.");
            run.WriteArtifacts("gpedit-persist-nulls.png");
        }

        checks.AssertAllPassed();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static string TabOf(GpeditRun run) => ReadKey(run.Shell.HelpTextOf("ConsoleExtendedView"), "Tab");

    private static string ModeOf(GpeditRun run) => ReadKey(run.Shell.HelpTextOf(GpeditRun.ListId), "Mode");

    private static string ReadKey(string readout, string key)
    {
        foreach (string part in readout.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && part[..at] == key)
            {
                return part[(at + 1)..];
            }
        }

        return string.Empty;
    }

    private static double HeaderWidth(GpeditRun run, string column) =>
        run.Rect(GpeditRun.ListRun_HeaderItemId(column)) is { } rect ? Math.Round(rect.Width / run.Scale, 1) : -1;
}
