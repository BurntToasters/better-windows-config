using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Accessibility;

/// <summary>
/// UI Automation checks for the registry key tree in the shipped Registry Editor, on a seeded HKCU sandbox. The rows
/// must expose ExpandCollapse (the state follows Expand and Collapse), SelectionItem (IsSelected follows Select), and
/// ScrollItem. The tree and its rows must have names, the Computer root and the five hives must be reachable, and
/// F2 must open a rename box named "Rename key". Failure modes: docs/failure-modes/accessibility.md section 1.
/// </summary>
[TestClass]
public sealed class TreeAccessibilityTests
{
    private const string WideKey = "Wide";
    private const string FirstChild = "Wide0001";
    private const string RenameBoxId = "TreeRenameBox";
    private const int ChildCount = 3;
    private static readonly string[] Hives =
    [
        "HKEY_CLASSES_ROOT",
        "HKEY_CURRENT_USER",
        "HKEY_LOCAL_MACHINE",
        "HKEY_USERS",
        "HKEY_CURRENT_CONFIG",
    ];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Accessibility")]
    [TestCategory("Mutating")]
    public void Tree_ExposesExpandSelectionScrollNamesAndHives()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("a11y.tree.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        SandboxRegistry? sandbox = null;
        try
        {
            sandbox = ControlTestSupport.SeedRegistry(config, ChildCount);
            using AppSession session = AccessibilitySupport.Launch(config, sandbox, "a11y-tree");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "a11y.tree.navigate");
            CheckTree(session, config);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(0, failures.Count, $"Tree accessibility checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void CheckTree(AppSession session, E2EConfig config)
    {
        AutomationElement? tree = session.Find(ControlTestSupport.TreeControl);
        string treeName = AccessibilitySupport.NameOf(tree);
        E2EResults.Record(
            "a11y.tree.control",
            AccessibilitySupport.ControlTypeOf(tree) == "Tree" && treeName.Length > 0,
            $"control type={AccessibilitySupport.ControlTypeOf(tree)}, name='{treeName}'.");

        AutomationElement? computer = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "Computer");
        bool computerOk = computer is not null && AccessibilitySupport.IdOf(computer) == "TreeRow_0";
        E2EResults.Record(
            "a11y.tree.computer-root",
            computerOk,
            $"Computer row found={computer is not null}, id='{AccessibilitySupport.IdOf(computer)}'.");
        if (computer is null)
        {
            return;
        }

        CheckExpandCollapse(session, computer, "a11y.tree.computer");
        CheckHives(session);

        AutomationElement? run = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, config.RunId);
        E2EResults.Record(
            "a11y.tree.run-key-name",
            run is not null && AccessibilitySupport.NameOf(run) == config.RunId,
            $"run key row found={run is not null}, name='{AccessibilitySupport.NameOf(run)}'.");
        if (run is null)
        {
            return;
        }

        if (AccessibilitySupport.StateOf(run) != "Expanded")
        {
            _ = ControlTestSupport.Expand(run);
        }

        AutomationElement? wide = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, WideKey) is not null,
            AccessibilitySupport.UiTimeout)
            ? ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, WideKey)
            : null;
        E2EResults.Record("a11y.tree.wide-row", wide is not null, $"the '{WideKey}' key row is reachable={wide is not null}.");
        if (wide is null)
        {
            return;
        }

        CheckExpandCollapse(session, wide, "a11y.tree.wide");
        CheckSelection(session, computer, wide);
        CheckScrollItem(wide);
        CheckLeaf(session);
        CheckRenameName(session, wide);
    }

    /// <summary>Collapse, then Expand, and the ExpandCollapse state follows each call. The row is left expanded.</summary>
    private static void CheckExpandCollapse(AppSession session, AutomationElement row, string checkId)
    {
        string initial = AccessibilitySupport.StateOf(row);
        bool supported = initial is "Expanded" or "Collapsed";
        E2EResults.Record($"{checkId}.expand-supported", supported, $"ExpandCollapse state at start='{initial}'.");
        if (!supported)
        {
            return;
        }

        _ = ControlTestSupport.Collapse(row);
        bool collapsed = ControlTestSupport.WaitUntil(
            () => AccessibilitySupport.StateOf(FindAgain(session, row)) == "Collapsed",
            ControlTestSupport.ExpandTimeout);
        E2EResults.Record($"{checkId}.collapse", collapsed, $"after Collapse the state is '{AccessibilitySupport.StateOf(row)}'.");

        _ = ControlTestSupport.Expand(row);
        bool expanded = ControlTestSupport.WaitUntil(
            () => AccessibilitySupport.StateOf(FindAgain(session, row)) == "Expanded",
            ControlTestSupport.ExpandTimeout);
        E2EResults.Record($"{checkId}.expand", expanded, $"after Expand the state is '{AccessibilitySupport.StateOf(row)}'.");
    }

    /// <summary>The five hives are reachable as rows under Computer once it is expanded.</summary>
    private static void CheckHives(AppSession session)
    {
        var missing = new List<string>();
        foreach (string hive in Hives)
        {
            bool found = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, hive) is not null,
                ControlTestSupport.ExpandTimeout);
            if (!found)
            {
                missing.Add(hive);
            }
        }

        E2EResults.Record(
            "a11y.tree.hives",
            missing.Count == 0,
            missing.Count == 0 ? "all five hives are tree items." : $"missing hives: {string.Join(", ", missing)}.");
    }

    /// <summary>SelectionItem: Select moves IsSelected to that row and clears it on the other row.</summary>
    private static void CheckSelection(AppSession session, AutomationElement computer, AutomationElement wide)
    {
        bool supported = AccessibilitySupport.HasPattern(wide, e => e.Patterns.SelectionItem.IsSupported);
        E2EResults.Record("a11y.tree.selection-supported", supported, "the tree row supports SelectionItem.");
        if (!supported)
        {
            return;
        }

        ControlTestSupport.Select(wide);
        bool wideSelected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindAgain(session, wide)),
            ControlTestSupport.ExpandTimeout);
        E2EResults.Record("a11y.tree.select-wide", wideSelected, $"Select on '{AccessibilitySupport.NameOf(wide)}' made IsSelected true={wideSelected}.");

        ControlTestSupport.Select(computer);
        bool moved = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindAgain(session, computer)) &&
                !ControlTestSupport.IsSelected(FindAgain(session, wide)),
            ControlTestSupport.ExpandTimeout);
        E2EResults.Record(
            "a11y.tree.select-moves",
            moved,
            $"Select on Computer moved the selection (Computer={ControlTestSupport.IsSelected(computer)}, {AccessibilitySupport.NameOf(wide)}={ControlTestSupport.IsSelected(wide)}).");

        ControlTestSupport.Select(wide);
        _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(FindAgain(session, wide)), ControlTestSupport.ExpandTimeout);
    }

    private static void CheckScrollItem(AutomationElement wide)
    {
        bool supported = AccessibilitySupport.HasPattern(wide, e => e.Patterns.ScrollItem.IsSupported);
        bool scrolled = false;
        if (supported)
        {
            try
            {
                wide.Patterns.ScrollItem.Pattern.ScrollIntoView();
                scrolled = true;
            }
            catch (Exception)
            {
                scrolled = false;
            }
        }

        E2EResults.Record("a11y.tree.scroll-item", supported && scrolled, $"ScrollItem supported={supported}, ScrollIntoView ran={scrolled}.");
    }

    /// <summary>A key with no children reports the LeafNode state, not Expanded or Collapsed.</summary>
    private static void CheckLeaf(AppSession session)
    {
        AutomationElement? child = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, FirstChild) is not null,
            ControlTestSupport.ExpandTimeout)
            ? ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, FirstChild)
            : null;
        string state = AccessibilitySupport.StateOf(child);
        E2EResults.Record("a11y.tree.leaf-state", state == "LeafNode", $"'{FirstChild}' ExpandCollapse state='{state}'.");
    }

    /// <summary>F2 on a selected key opens the inline rename box named "Rename key", and Escape cancels it.</summary>
    private static void CheckRenameName(AppSession session, AutomationElement wide)
    {
        ControlTestSupport.Select(wide);
        _ = ControlTestSupport.EnsureForeground(session);
        Keyboard.Type(VirtualKeyShort.F2);
        AutomationElement? box = AccessibilitySupport.WaitForId(session, RenameBoxId, TimeSpan.FromSeconds(3));
        string name = AccessibilitySupport.NameOf(box);
        E2EResults.Record("a11y.tree.rename-name", name == "Rename key", $"rename box found={box is not null}, name='{name}'.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        _ = ControlTestSupport.WaitUntil(() => session.Find(RenameBoxId) is null, TimeSpan.FromSeconds(3));
    }

    /// <summary>Finds the row again by its name, so a state read after a re-layout does not use a stale element.</summary>
    private static AutomationElement? FindAgain(AppSession session, AutomationElement row)
    {
        string name = AccessibilitySupport.NameOf(row);
        return name.Length == 0
            ? row
            : ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name) ?? row;
    }
}
