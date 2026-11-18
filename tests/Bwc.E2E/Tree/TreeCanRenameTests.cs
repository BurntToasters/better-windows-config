using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Tree;

/// <summary>
/// TreeNode.CanRename in the shipped tree. The E2E flavor of TreeNode marks the node named in
/// BWC_E2E_TREE_NO_RENAME_NAMES as not renamable (the regedit tree has none of its own). The test
/// drives real keyboard and mouse input against a seeded HKCU sandbox: on a normal sibling the slow
/// second click and F2 open the rename box (so the test cannot pass because rename is broken
/// everywhere), and on the marked node neither does, and the key is not renamed.
/// </summary>
[TestClass]
public sealed class TreeCanRenameTests
{
    private const string Locked = "NoRename-Locked";
    private const string Free = "Rename-Free";
    private const string RenameBoxId = "TreeRenameBox";

    // Computer 0, HKEY_CURRENT_USER 1, Software 2, BWC-E2E 3, run 4: the children of the run key are depth 5.
    private const int ChildDepth = 5;
    private const int IndentDip = 16;
    private const int LabelZoneDip = 27;

    // A slow second click needs the key selected for longer than the double-click time (500 ms by default).
    private static readonly TimeSpan SlowClickSettle = TimeSpan.FromMilliseconds(1200);

    // The rename starts one double-click time after a slow click, so absence is judged after more than that.
    private static readonly TimeSpan NoRenameWait = TimeSpan.FromMilliseconds(1800);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    [TestCategory("E2EOnly")]
    public void Tree_CanRenameFalseIgnoresF2AndSlowClick()
    {
        int start = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();
        try
        {
            sandbox.CreateSubKey(Locked).Dispose();
            sandbox.CreateSubKey(Free).Dispose();

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("tree-canrename"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                ["BWC_E2E_TREE_NO_RENAME_NAMES"] = Locked,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "tree.canrename.navigate");

            ControlTestSupport.SendKeys(session, "ADD");
            bool rows = ControlTestSupport.WaitUntil(
                () => Row(session, Locked) is not null && Row(session, Free) is not null,
                ControlTestSupport.ExpandTimeout);
            E2EResults.Record("tree.canrename.rows", rows, $"Both child rows are shown={rows}.");
            if (rows)
            {
                RunChecks(session, sandbox);
            }
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            $"CanRename checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void RunChecks(AppSession session, SandboxRegistry sandbox)
    {
        // Control: on a normal sibling the slow second click opens the rename box.
        ClickRow(session, Free);
        bool freeSelected = ControlTestSupport.WaitForTreeSelection(session, Free);
        E2EGuards.RequireSandboxSelection(session, sandbox, "tree.canrename.guard.free");
        Thread.Sleep(SlowClickSettle);
        ClickRow(session, Free);
        bool freeSlow = WaitForBox(session, TimeSpan.FromSeconds(4));
        E2EResults.Record("tree.canrename.control.slowclick", freeSelected && freeSlow, $"Free key selected={freeSelected}; slow click opened the rename box={freeSlow}.");
        ControlTestSupport.SendKeys(session, "ESCAPE");
        bool closed = ControlTestSupport.WaitUntil(() => session.Find(RenameBoxId) is null, TimeSpan.FromSeconds(5));
        E2EResults.Record("tree.canrename.control.closed", closed, $"Escape closed the rename box={closed}.");

        // Control: F2 on the normal sibling opens the box.
        ControlTestSupport.SendKeys(session, "F2");
        bool freeF2 = WaitForBox(session, TimeSpan.FromSeconds(4));
        E2EResults.Record("tree.canrename.control.f2", freeF2, $"F2 on the free key opened the rename box={freeF2}.");
        ControlTestSupport.SendKeys(session, "ESCAPE");
        _ = ControlTestSupport.WaitUntil(() => session.Find(RenameBoxId) is null, TimeSpan.FromSeconds(5));

        // CanRename false: F2 does nothing.
        Thread.Sleep(SlowClickSettle);
        ClickRow(session, Locked);
        bool lockedSelected = ControlTestSupport.WaitForTreeSelection(session, Locked);
        E2EGuards.RequireSandboxSelection(session, sandbox, "tree.canrename.guard.locked.f2");
        ControlTestSupport.SendKeys(session, "F2");
        Thread.Sleep(NoRenameWait);
        bool f2Box = session.Find(RenameBoxId) is not null;
        E2EResults.Record(
            "tree.canrename.locked.f2",
            lockedSelected && !f2Box,
            $"Locked key selected={lockedSelected}; F2 opened the rename box={f2Box} (required false).");

        // CanRename false: the slow second click does nothing, however long it waits.
        Thread.Sleep(SlowClickSettle);
        ClickRow(session, Locked);
        Thread.Sleep(NoRenameWait);
        bool slowBox = session.Find(RenameBoxId) is not null;
        bool stillSelected = ControlTestSupport.IsSelected(Row(session, Locked));
        E2EResults.Record(
            "tree.canrename.locked.slowclick",
            !slowBox && stillSelected,
            $"Slow click opened the rename box={slowBox} (required false); Locked still selected={stillSelected}.");

        // The key itself is untouched.
        bool unchanged = sandbox.Exists(Locked) && sandbox.Exists(Free);
        E2EResults.Record("tree.canrename.registry", unchanged, $"Both sandbox keys keep their names={unchanged}.");
    }

    private static AutomationElement? Row(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name);

    private static bool WaitForBox(AppSession session, TimeSpan timeout) =>
        ControlTestSupport.WaitUntil(() => session.Find(RenameBoxId) is not null, timeout);

    /// <summary>Clicks the label zone of a child row. The icon zone is hit-test invisible, so the row gets the click.</summary>
    private static void ClickRow(AppSession session, string name)
    {
        AutomationElement? row = Row(session, name);
        if (row is null)
        {
            return;
        }

        _ = ControlTestSupport.EnsureForeground(session);
        var rect = row.BoundingRectangle;
        double scale = Math.Max(1, ControlTestSupport.WindowDpi(session)) / 96.0;
        int x = rect.Left + (int)Math.Round((ChildDepth * IndentDip + LabelZoneDip) * scale);
        int y = rect.Top + (rect.Height / 2);
        Mouse.LeftClick(new System.Drawing.Point(x, y));
    }
}
