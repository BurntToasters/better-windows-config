using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Tree;

/// <summary>
/// Classic TreeView behavior of the dense tree, driven with real keyboard and mouse input against a
/// seeded HKCU sandbox. Each test records its checks in artifacts/e2e/result-rid-flavor.json and writes
/// its captures and timings to artifacts/e2e/visual/tree/. Only the rename test changes the registry,
/// and only inside the run sandbox.
/// </summary>
[TestClass]
public sealed class TreeBehaviorTests
{
    private const string TreeControl = ControlTestSupport.TreeControl;

    // The tree's type-ahead buffer resets after 1000 ms. The pause is longer so a new prefix starts clean.
    private const int TypeAheadPauseMs = 1100;
    private const int DeepLevels = 12;
    private const int HugeChildCount = 10_000;
    private const int FirstPageRows = 500;
    private const int FirstPageBudgetMs = 2000;
    private const int CollapseBudgetMs = 2000;

    // Row geometry in DIPs: a row at depth d starts its expander at d * 16 + 2.5 and its label at
    // d * 16 + 37. 27 DIPs sits in the icon zone, which is hit-test invisible, so a click there lands
    // on the row and never on the expander.
    private const int IndentDip = 16;
    private const int LabelZoneDip = 27;

    // Depth of the children of the run key. Computer 0, HKEY_CURRENT_USER 1, Software 2, BWC-E2E 3, run 4.
    private const int RunChildDepth = 5;

    private static readonly string[] LeafNames =
    [
        "Prefix-Alpha",
        "Prefix-Beta",
        "Prefix-Gamma",
        "Pre-Other",
        "Apple",
        "Apricot",
        "RenameSource",
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_DoubleClickTogglesExpansion()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunScenario("tree-dblclick", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, "Tree", "tree.dblclick.navigate");
            if (ControlTestSupport.StateOf(Row(session, "Tree")) == "Expanded")
            {
                Key(session, "LEFT");
            }

            bool collapsed = WaitForState(session, "Tree", "Collapsed");
            E2EResults.Record("tree.dblclick.initial", collapsed, $"Tree state={ControlTestSupport.StateOf(Row(session, "Tree"))}.");

            // A single click selects and should give the tree keyboard focus. Recorded so a gap shows up here.
            ClickRow(session, "Tree", RunChildDepth + 1);
            Thread.Sleep(300);
            bool clickFocus = ControlTestSupport.IsTreeKeyboardFocused(session, "Tree");
            E2EResults.Record("tree.click.keyboard-focus", clickFocus, $"Single click on Tree gave the tree keyboard focus={clickFocus}.");

            DoubleClickRow(session, "Tree", RunChildDepth + 1);
            bool expanded = WaitForRowPresent(session, "Level1") && WaitForState(session, "Tree", "Expanded");
            bool dblFocus = ControlTestSupport.IsTreeKeyboardFocused(session, "Tree");
            E2EResults.Record("tree.dblclick.expands", expanded, $"Double-click on Tree expanded it={expanded}; keyboard focus after it={dblFocus}.");
            SaveCapture(session, "double-click-expanded");

            DoubleClickRow(session, "Tree", RunChildDepth + 1);
            bool collapsedAgain = WaitForRowAbsent(session, "Level1") && WaitForState(session, "Tree", "Collapsed");
            E2EResults.Record("tree.dblclick.collapses", collapsedAgain, $"Second double-click collapsed it={collapsedAgain}.");

            // Leaves sit under the run key, which is collapsed here. Left moves to it and Add opens it.
            Key(session, "LEFT");
            Key(session, "ADD");
            _ = WaitForRowPresent(session, "Apple");
            TypeAhead(session, "Apple");
            bool appleSelected = WaitForSelected(session, "Apple");
            bool treeFocused = ControlTestSupport.IsTreeKeyboardFocused(session, "Apple");
            int rowsBefore = RowNames(session).Length;
            DoubleClickRow(session, "Apple", RunChildDepth);
            Thread.Sleep(300);
            bool leafUnchanged = ControlTestSupport.StateOf(Row(session, "Apple")) == "LeafNode" &&
                RowNames(session).Length == rowsBefore;
            E2EResults.Record(
                "tree.dblclick.leaf-noop",
                appleSelected && leafUnchanged,
                $"Apple selected={appleSelected}; tree keyboard focus after type-ahead={treeFocused}; double-click on a leaf left state and row count unchanged={leafUnchanged}.");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_NumpadKeysAndPageKeys()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunScenario("tree-numpad", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, null, "tree.numpad.navigate");

            Key(session, "ADD");
            bool added = WaitForRowPresent(session, "Tree") && WaitForRowPresent(session, "Sibling");
            E2EResults.Record("tree.numpad.add", added, $"Numpad + expanded the run key={added}.");

            Key(session, "SUBTRACT");
            bool subtracted = WaitForRowAbsent(session, "Tree") && WaitForSelected(session, config.RunId);
            E2EResults.Record("tree.numpad.subtract", subtracted, $"Numpad - collapsed the run key with selection kept={subtracted}.");

            Key(session, "ADD");
            bool readded = WaitForRowPresent(session, "Tree");
            TypeAhead(session, "Tree");
            bool treeSelected = WaitForSelected(session, "Tree");
            E2EResults.Record("tree.numpad.prepare", readded && treeSelected, $"Run key expanded={readded}; Tree selected={treeSelected}.");

            Key(session, "MULTIPLY");
            bool deepExpanded = WaitForRowPresent(session, "Level3");
            bool siblingUntouched = WaitForState(session, "Sibling", "Collapsed") && Row(session, "SibChild") is null;
            E2EResults.Record(
                "tree.numpad.multiply.descendants",
                deepExpanded,
                $"Numpad * expanded every level below Tree (Level3 visible={deepExpanded}).");
            E2EResults.Record(
                "tree.numpad.multiply.siblings",
                siblingUntouched,
                $"Numpad * left the sibling Sibling collapsed={siblingUntouched}.");
            SaveCapture(session, "numpad-multiply");

            Key(session, "SUBTRACT");
            bool treeCollapsed = WaitForRowAbsent(session, "Level1") && WaitForSelected(session, "Tree");
            E2EResults.Record("tree.numpad.subtract.selected", treeCollapsed, $"Numpad - on Tree collapsed its subtree={treeCollapsed}.");

            Key(session, "END");
            string[] names = RowNames(session);
            string last = names.Length > 0 ? names[^1] : string.Empty;
            bool endOk = last.Length > 0 && WaitForSelected(session, last);
            E2EResults.Record("tree.keys.end", endOk, $"End selected the last visible row '{last}'={endOk}.");

            Key(session, "HOME");
            bool homeOk = WaitForSelected(session, "Computer");
            E2EResults.Record("tree.keys.home", homeOk, $"Home selected Computer={homeOk}.");

            Key(session, "NEXT");
            bool pageDown = ControlTestSupport.WaitUntil(
                () => SelectedRowName(session) is { } name && name != "Computer",
                TimeSpan.FromSeconds(5));
            E2EResults.Record("tree.keys.pagedown", pageDown, $"PageDown moved the selection to '{SelectedRowName(session)}'.");

            Key(session, "PRIOR");
            bool pageUp = ControlTestSupport.WaitUntil(
                () => SelectedRowName(session) == "Computer",
                TimeSpan.FromSeconds(5));
            E2EResults.Record("tree.keys.pageup", pageUp, $"PageUp from the page returned the selection to '{SelectedRowName(session)}'.");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_RightAndLeftArrowSemantics()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunScenario("tree-arrows", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, "Tree", "tree.arrows.navigate");
            if (ControlTestSupport.StateOf(Row(session, "Tree")) == "Expanded")
            {
                Key(session, "LEFT");
            }

            Key(session, "RIGHT");
            bool expanded = WaitForRowPresent(session, "Level1") && WaitForSelected(session, "Tree");
            E2EResults.Record("tree.arrows.right.expands", expanded, $"Right on a collapsed Tree expanded it and kept the selection={expanded}.");

            Key(session, "RIGHT");
            bool toChild = WaitForSelected(session, "Level1");
            E2EResults.Record("tree.arrows.right.to-first-child", toChild, $"Right on an expanded Tree moved to Level1={toChild}.");

            Key(session, "RIGHT");
            bool childExpanded = WaitForRowPresent(session, "Level2") && WaitForSelected(session, "Level1");
            E2EResults.Record("tree.arrows.right.expands-child", childExpanded, $"Right on a collapsed Level1 expanded it={childExpanded}.");

            Key(session, "RIGHT");
            bool intoLevel2 = WaitForSelected(session, "Level2");
            E2EResults.Record("tree.arrows.right.into-expanded", intoLevel2, $"Right on an expanded Level1 moved to Level2={intoLevel2}.");

            Key(session, "LEFT");
            bool toParent = WaitForSelected(session, "Level1");
            E2EResults.Record("tree.arrows.left.collapsed-to-parent", toParent, $"Left on collapsed Level2 moved to its parent={toParent}.");

            Key(session, "LEFT");
            bool collapsedKeeps = WaitForRowAbsent(session, "Level2") && WaitForSelected(session, "Level1");
            E2EResults.Record("tree.arrows.left.expanded-collapses", collapsedKeeps, $"Left on expanded Level1 collapsed it and kept the selection={collapsedKeeps}.");

            Key(session, "LEFT");
            bool toTree = WaitForSelected(session, "Tree");
            E2EResults.Record("tree.arrows.left.to-parent", toTree, $"Left on collapsed Level1 moved to Tree={toTree}.");

            Key(session, "LEFT");
            bool treeCollapsed = WaitForRowAbsent(session, "Level1") && WaitForSelected(session, "Tree");
            E2EResults.Record("tree.arrows.left.collapses-tree", treeCollapsed, $"Left on expanded Tree collapsed it={treeCollapsed}.");

            TypeAhead(session, "Apple");
            bool appleSelected = WaitForSelected(session, "Apple");
            Key(session, "RIGHT");
            Thread.Sleep(300);
            bool leafStays = appleSelected && SelectedRowName(session) == "Apple";
            E2EResults.Record("tree.arrows.right.leaf-noop", leafStays, $"Right on the leaf Apple kept the selection={leafStays}.");

            Key(session, "HOME");
            Key(session, "LEFT");
            Thread.Sleep(300);
            bool rootStays = SelectedRowName(session) == "Computer";
            E2EResults.Record("tree.arrows.left.root-noop", rootStays, $"Left on the root Computer kept the selection={rootStays}.");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_TypeAheadExtendsPrefixCyclesAndRestarts()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunScenario("tree-typeahead", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, null, "tree.typeahead.navigate");
            Key(session, "ADD");
            if (!WaitForRowPresent(session, "Apple"))
            {
                E2EResults.Record("tree.typeahead.expand", false, "The run key did not expand.");
                return;
            }

            string[] names = RowNames(session);
            int runIndex = IndexOfName(names, config.RunId);

            // Typed quickly, "pre" selects the first row starting with "pre" after the selection.
            Type(session, "pre");
            int first = FirstMatchFrom(names, runIndex, name => name.StartsWith("pre", StringComparison.OrdinalIgnoreCase));
            bool prefixOk = first >= 0 && WaitForSelected(session, names[first]);
            E2EResults.Record("tree.typeahead.prefix", prefixOk, $"'pre' selected '{(first >= 0 ? names[first] : "none")}'={prefixOk}.");

            // A longer prefix typed quickly extends the match instead of restarting from the top.
            TypeAhead(session, "prefix");
            int extended = FirstMatchFrom(names, first, name => name.StartsWith("prefix", StringComparison.OrdinalIgnoreCase));
            bool extendOk = extended >= 0 && WaitForSelected(session, names[extended]);
            E2EResults.Record("tree.typeahead.extend", extendOk, $"'prefix' extended the match to '{(extended >= 0 ? names[extended] : "none")}'={extendOk}.");

            // Three quick presses of one letter cycle three rows that start with that letter.
            TypeAhead(session, "ppp");
            int cycled = extended;
            for (var step = 0; step < 3 && cycled >= 0; step++)
            {
                cycled = FirstMatchFrom(names, cycled + 1, name => name.StartsWith("p", StringComparison.OrdinalIgnoreCase));
            }

            bool cycleOk = cycled >= 0 && WaitForSelected(session, names[cycled]);
            E2EResults.Record("tree.typeahead.cycle", cycleOk, $"'ppp' cycled three steps to '{(cycled >= 0 ? names[cycled] : "none")}'={cycleOk}.");

            // After the pause, a single letter moves exactly one row further through the same cycle.
            TypeAhead(session, "p");
            int single = FirstMatchFrom(names, cycled + 1, name => name.StartsWith("p", StringComparison.OrdinalIgnoreCase));
            bool singleOk = single >= 0 && WaitForSelected(session, names[single]);
            E2EResults.Record("tree.typeahead.single-step", singleOk, $"'p' after the pause moved to '{(single >= 0 ? names[single] : "none")}'={singleOk}.");

            // After the pause, a new prefix starts from the current row again.
            TypeAhead(session, "apple");
            int apple = FirstMatchFrom(names, single, name => name.StartsWith("apple", StringComparison.OrdinalIgnoreCase));
            bool restartOk = apple >= 0 && WaitForSelected(session, names[apple]);
            E2EResults.Record("tree.typeahead.restart", restartOk, $"'apple' after the pause selected '{(apple >= 0 ? names[apple] : "none")}'={restartOk}.");

            TypeAhead(session, "qx");
            Thread.Sleep(300);
            bool noMatchKept = SelectedRowName(session) == names[apple];
            E2EResults.Record("tree.typeahead.no-match", noMatchKept, $"'qx' with no match kept the selection={noMatchKept}.");
            SaveCapture(session, "type-ahead");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_HorizontalScrollAppearsForDeepPath()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunScenario("tree-hscroll", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, null, "tree.hscroll.navigate");
            Key(session, "ADD");
            bool deepVisible = WaitForRowPresent(session, "Deep");
            E2EResults.Record("tree.hscroll.setup", deepVisible, $"Deep chain parent visible={deepVisible}.");

            TypeAhead(session, "Deep");
            bool deepSelected = WaitForSelected(session, "Deep");

            bool walked = deepSelected;
            for (var level = 1; walked && level <= DeepLevels; level++)
            {
                string child = DeepName(level);
                Key(session, "RIGHT");
                walked = WaitForRowPresent(session, child);
                if (walked)
                {
                    Key(session, "RIGHT");
                    walked = WaitForSelected(session, child);
                }
            }

            E2EResults.Record("tree.hscroll.walk", walked, $"Right-arrow walk to the {DeepLevels}-level deep key={walked}.");

            bool bar = ControlTestSupport.WaitUntil(() => HorizontalScrollBarVisible(session), TimeSpan.FromSeconds(5));
            E2EResults.Record("tree.hscroll.appears", bar, $"Horizontal scrollbar visible for the deep path={bar}.");
            SaveCapture(session, "horizontal-scroll");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    [TestCategory("E2EOnly")]
    public void Tree_RenameCommitsOnEnterAndFocusLossAndCancelsOnEscape()
    {
        E2EGuards.RequireMutatingFlavor(GuiGate.Config);
        RunScenario("tree-rename", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, null, "tree.rename.navigate");
            Key(session, "ADD");
            _ = WaitForRowPresent(session, "RenameSource");
            TypeAhead(session, "RenameSource");
            bool selected = WaitForSelected(session, "RenameSource");
            E2EGuards.RequireSandboxSelection(session, sandbox, "tree.rename.guard.enter");
            if (!selected)
            {
                E2EResults.Record("tree.rename.select", false, "RenameSource was not selected.");
                return;
            }

            StartRename(session);
            Type(session, "RenamedByEnter");
            Key(session, "RETURN");
            bool byEnter = ControlTestSupport.WaitUntil(
                () => sandbox.Exists("RenamedByEnter") && !sandbox.Exists("RenameSource"),
                TimeSpan.FromSeconds(10));
            E2EResults.Record("tree.rename.enter-commits", byEnter, $"Enter committed the rename in the sandbox={byEnter}.");

            bool rowUpdated = WaitForRowPresent(session, "RenamedByEnter");
            TypeAhead(session, "RenamedByEnter");
            bool reselected = WaitForSelected(session, "RenamedByEnter");
            E2EGuards.RequireSandboxSelection(session, sandbox, "tree.rename.guard.focus");
            StartRename(session);
            Type(session, "RenamedByClick");
            ClickRow(session, "Prefix-Beta", RunChildDepth);
            bool byFocus = ControlTestSupport.WaitUntil(
                () => sandbox.Exists("RenamedByClick") && !sandbox.Exists("RenamedByEnter"),
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "tree.rename.focus-loss-commits",
                byFocus && rowUpdated && reselected,
                $"Row updated={rowUpdated}; selected={reselected}; clicking elsewhere committed the rename={byFocus}.");

            TypeAhead(session, "Prefix-Gamma");
            bool gammaSelected = WaitForSelected(session, "Prefix-Gamma");
            E2EGuards.RequireSandboxSelection(session, sandbox, "tree.rename.guard.escape");
            StartRename(session);
            Type(session, "DiscardMe");
            Key(session, "ESCAPE");
            Thread.Sleep(800);
            bool cancelled = ControlTestSupport.WaitUntil(() => session.Find("TreeRenameBox") is null, TimeSpan.FromSeconds(5)) &&
                !sandbox.Exists("DiscardMe") &&
                sandbox.Exists("Prefix-Gamma");
            E2EResults.Record(
                "tree.rename.escape-cancels",
                gammaSelected && cancelled,
                $"Escape closed the box and left the sandbox unchanged={cancelled}.");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_UiaExpandCollapseAndSelectionState()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunScenario("tree-uia", (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, null, "tree.uia.navigate");
            Key(session, "ADD");
            bool expanded = WaitForRowPresent(session, "Tree");

            var tree = session.Find(TreeControl);
            bool treeType = tree?.ControlType == ControlType.Tree;
            E2EResults.Record("tree.uia.control-type", treeType, $"The tree control reports ControlType={tree?.ControlType}.");

            var treeRow = Row(session, "Tree");
            bool patterns = treeRow is not null &&
                treeRow.Patterns.ExpandCollapse.IsSupported &&
                treeRow.Patterns.SelectionItem.IsSupported &&
                treeRow.Patterns.ScrollItem.IsSupported;
            E2EResults.Record("tree.uia.patterns", expanded && patterns, $"Tree item supports ExpandCollapse, SelectionItem and ScrollItem={patterns}.");

            bool startCollapsed = WaitForState(session, "Tree", "Collapsed");
            bool expandedByPattern = ExpandByPattern(session, "Tree", "Level1", TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "tree.uia.expand-pattern",
                startCollapsed && expandedByPattern,
                $"Start collapsed={startCollapsed}; Expand() shows Level1 and state Expanded={expandedByPattern}.");

            bool collapsedByPattern = CollapseByPattern(session, "Tree", "Level1", TimeSpan.FromSeconds(10));
            E2EResults.Record("tree.uia.collapse-pattern", collapsedByPattern, $"Collapse() removes Level1 and state Collapsed={collapsedByPattern}.");

            var apple = Row(session, "Apple");
            bool leaf = ControlTestSupport.StateOf(apple) == "LeafNode";
            ControlTestSupport.Select(apple);
            bool appleSelected = WaitForSelected(session, "Apple");
            bool idOk = ControlTestSupport.SafeId(Row(session, "Apple")).StartsWith(ControlTestSupport.TreeRowPrefix, StringComparison.Ordinal);
            E2EResults.Record(
                "tree.uia.leaf-and-selection",
                leaf && appleSelected && idOk,
                $"Apple state LeafNode={leaf}; SelectionItem.Select selected it={appleSelected}; automation id keeps TreeRow_ prefix={idOk}.");
        });
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("E2EOnly")]
    public void Tree_TenThousandChildrenFirstPageWithinBudget()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        long seedMs = 0;
        RunScenario(
            "tree-perf",
            (config, session, sandbox) =>
        {
            NavigateTo(session, config, sandbox, "Huge", "tree.perf.navigate");
            if (ControlTestSupport.StateOf(Row(session, "Huge")) == "Expanded")
            {
                Key(session, "LEFT");
            }

            bool collapsed = WaitForState(session, "Huge", "Collapsed");
            int before = ControlTestSupport.RowCountFromHelpText(session, TreeControl);

            var expand = Stopwatch.StartNew();
            Key(session, "RIGHT");
            bool firstPage = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.RowCountFromHelpText(session, TreeControl) >= before + FirstPageRows,
                TimeSpan.FromSeconds(30));
            long firstPageMs = expand.ElapsedMilliseconds;
            bool full = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.RowCountFromHelpText(session, TreeControl) >= before + HugeChildCount,
                TimeSpan.FromSeconds(60));
            long fullMs = expand.ElapsedMilliseconds;
            int afterFull = ControlTestSupport.RowCountFromHelpText(session, TreeControl);

            bool firstPageOk = collapsed && firstPage && firstPageMs <= FirstPageBudgetMs;
            E2EResults.Record(
                "tree.perf.first-page",
                firstPageOk,
                $"First {FirstPageRows} rows of {HugeChildCount} children in {firstPageMs} ms (budget {FirstPageBudgetMs} ms); seeded in {seedMs} ms.");
            E2EResults.Record(
                "tree.perf.full",
                full,
                $"All children visible in {fullMs} ms; rows={afterFull}, expected at least {before + HugeChildCount}.");
            SaveCapture(session, "perf-10k-expanded");

            var collapse = Stopwatch.StartNew();
            Key(session, "LEFT");
            bool collapsedAgain = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.RowCountFromHelpText(session, TreeControl) == before,
                TimeSpan.FromSeconds(30));
            long collapseMs = collapse.ElapsedMilliseconds;
            E2EResults.Record(
                "tree.perf.collapse",
                collapsedAgain && collapseMs <= CollapseBudgetMs,
                $"Collapsing the 10,000 child key took {collapseMs} ms (budget {CollapseBudgetMs} ms); rows back to {before}={collapsedAgain}.");

            WriteTimings(new
            {
                childCount = HugeChildCount,
                firstPageRows = FirstPageRows,
                seedMs,
                firstPageMs,
                firstPageBudgetMs = FirstPageBudgetMs,
                fullMs,
                collapseMs,
                collapseBudgetMs = CollapseBudgetMs,
                rowsAfterExpand = afterFull,
                rowsBeforeExpand = before,
                pass = firstPageOk && full && collapsedAgain && collapseMs <= CollapseBudgetMs,
            });
        },
        sandbox =>
        {
            var seed = Stopwatch.StartNew();
            using var huge = sandbox.CreateSubKey("Huge");
            for (var index = 1; index <= HugeChildCount; index++)
            {
                huge.CreateSubKey("Huge" + index.ToString("D5", CultureInfo.InvariantCulture))?.Dispose();
            }

            seedMs = seed.ElapsedMilliseconds;
        });
    }

    /// <summary>
    /// Seeds the sandbox, launches the Registry Editor, runs the body, and removes the run subtree
    /// afterwards. Everything is seeded before launch because the editor restores its last key at
    /// startup and loads that branch's children then. Each scenario gets its own settings scope so one
    /// test's last key never opens the next test's window. A failed check in the body fails the test
    /// with every failed check listed.
    /// </summary>
    private static void RunScenario(
        string scope,
        Action<E2EConfig, AppSession, SandboxRegistry> body,
        Action<SandboxRegistry>? extraSeed = null)
    {
        int start = E2EResults.CheckCount;
        var config = GuiGate.Config;
        config.EnsureExecutablesExist();
        var sandbox = SeedFixture(config);
        try
        {
            extraSeed?.Invoke(sandbox);
            using var session = Launch(config, sandbox, scope);
            body(config, session, sandbox);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Tree behavior checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static AppSession Launch(E2EConfig config, SandboxRegistry sandbox, string scope)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor(scope),
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    /// <summary>
    /// Seeds the run subtree: a three-level branch with a sibling, a set of leaves that share prefixes,
    /// and a twelve-level chain with long names for the horizontal scroll test.
    /// </summary>
    private static SandboxRegistry SeedFixture(E2EConfig config)
    {
        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();

        foreach (string leaf in LeafNames)
        {
            CreateKey(sandbox, leaf);
        }

        CreateKey(sandbox, @"Tree\Level1\Level2\Level3");
        CreateKey(sandbox, @"Sibling\SibChild");

        var chain = new List<string>(DeepLevels);
        for (var level = 1; level <= DeepLevels; level++)
        {
            chain.Add(DeepName(level));
        }

        CreateKey(sandbox, @"Deep\" + string.Join('\\', chain));
        return sandbox;
    }

    private static void CreateKey(SandboxRegistry sandbox, string relativePath) =>
        sandbox.CreateSubKey(relativePath).Dispose();

    private static string DeepName(int level) =>
        "DeepLevel" + level.ToString("D2", CultureInfo.InvariantCulture) + "-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef";

    private static void NavigateTo(AppSession session, E2EConfig config, SandboxRegistry sandbox, string? childKey, string checkId)
    {
        bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey);
        E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, checkId);
    }

    private static AutomationElement? Row(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, TreeControl, ControlType.TreeItem, name);

    private static string[] RowNames(AppSession session) =>
        ControlTestSupport.Rows(session, TreeControl, ControlType.TreeItem)
            .Select(ControlTestSupport.SafeName)
            .ToArray();

    private static string? SelectedRowName(AppSession session) =>
        ControlTestSupport.Rows(session, TreeControl, ControlType.TreeItem)
            .FirstOrDefault(ControlTestSupport.IsSelected) is { } row
            ? ControlTestSupport.SafeName(row)
            : null;

    private static bool WaitForRowPresent(AppSession session, string name) =>
        ControlTestSupport.WaitUntil(() => Row(session, name) is not null, ControlTestSupport.ExpandTimeout);

    private static bool WaitForRowAbsent(AppSession session, string name) =>
        ControlTestSupport.WaitUntil(() => Row(session, name) is null, TimeSpan.FromSeconds(5));

    private static bool WaitForState(AppSession session, string name, string state) =>
        ControlTestSupport.WaitUntil(
            () => ControlTestSupport.StateOf(Row(session, name)) == state,
            TimeSpan.FromSeconds(5));

    private static bool WaitForSelected(AppSession session, string name) =>
        ControlTestSupport.WaitForTreeSelection(session, name);

    /// <summary>
    /// Expands a row with the UI Automation ExpandCollapse pattern until the child row is present and
    /// the state is Expanded. The row is re-read for every attempt, and an Expand that the provider
    /// ignored while the row was still animating is sent again. Returns false after the timeout.
    /// </summary>
    private static bool ExpandByPattern(AppSession session, string rowName, string childName, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            _ = ControlTestSupport.EnsureForeground(session);
            AutomationElement? row = Row(session, rowName);
            if (row is not null && Row(session, childName) is null)
            {
                _ = ControlTestSupport.Expand(row);
            }

            if (Row(session, childName) is not null && ControlTestSupport.StateOf(Row(session, rowName)) == "Expanded")
            {
                return true;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    /// <summary>
    /// Collapses a row with the UI Automation ExpandCollapse pattern until the child row is gone and
    /// the state is Collapsed. A Collapse sent while the expand animation was still running can be
    /// ignored, so the row is re-read and the pattern is sent again. Returns false after the timeout.
    /// </summary>
    private static bool CollapseByPattern(AppSession session, string rowName, string childName, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            _ = ControlTestSupport.EnsureForeground(session);
            AutomationElement? row = Row(session, rowName);
            if (row is not null && (Row(session, childName) is not null || ControlTestSupport.StateOf(row) == "Expanded"))
            {
                _ = ControlTestSupport.Collapse(row);
            }

            if (Row(session, childName) is null && ControlTestSupport.StateOf(Row(session, rowName)) == "Collapsed")
            {
                return true;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static void Key(AppSession session, string key) => ControlTestSupport.SendKeys(session, key);

    /// <summary>Types text as characters. The tree's type-ahead buffer sees them as one prefix.</summary>
    private static void Type(AppSession session, string text)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        Keyboard.Type(text);
    }

    private static void TypeAhead(AppSession session, string text)
    {
        Thread.Sleep(TypeAheadPauseMs);
        Type(session, text);
    }

    private static void StartRename(AppSession session)
    {
        Key(session, "F2");
        _ = ControlTestSupport.WaitUntil(() => session.Find("TreeRenameBox") is not null, TimeSpan.FromSeconds(5));
    }

    private static System.Drawing.Point LabelPoint(AppSession session, AutomationElement row, int depth)
    {
        var rect = row.BoundingRectangle;
        double scale = ControlTestSupport.WindowDpi(session) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        int x = rect.Left + (int)Math.Round((depth * IndentDip + LabelZoneDip) * scale);
        int y = rect.Top + (rect.Height / 2);
        return new System.Drawing.Point(x, y);
    }

    private static void DoubleClickRow(AppSession session, string name, int depth)
    {
        var row = Row(session, name);
        if (row is null)
        {
            return;
        }

        _ = ControlTestSupport.EnsureForeground(session);
        Mouse.LeftDoubleClick(LabelPoint(session, row, depth));
    }

    private static void ClickRow(AppSession session, string name, int depth)
    {
        var row = Row(session, name);
        if (row is null)
        {
            return;
        }

        _ = ControlTestSupport.EnsureForeground(session);
        Mouse.LeftClick(LabelPoint(session, row, depth));
    }

    /// <summary>
    /// True when a horizontal scroll bar inside the tree is on screen. The bar is wider than it is tall.
    /// </summary>
    private static bool HorizontalScrollBarVisible(AppSession session)
    {
        var tree = session.Find(TreeControl);
        if (tree is null)
        {
            return false;
        }

        try
        {
            foreach (var bar in tree.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar)))
            {
                var rect = bar.BoundingRectangle;
                if (!bar.IsOffscreen && rect.Height > 0 && rect.Width > rect.Height)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // A tree that is closing has no scroll bars to report.
        }

        return false;
    }

    /// <summary>
    /// The first index at or after <paramref name="start"/> (wrapping once) whose name matches. The same
    /// order the tree uses for type-ahead. Returns -1 when nothing matches.
    /// </summary>
    private static int FirstMatchFrom(string[] names, int start, Func<string, bool> matches)
    {
        if (names.Length == 0)
        {
            return -1;
        }

        for (var step = 0; step < names.Length; step++)
        {
            int index = (start + step) % names.Length;
            if (matches(names[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfName(string[] names, string name) =>
        Array.FindIndex(names, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

    private static string SaveCapture(AppSession session, string name)
    {
        string directory = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", "tree");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".png");
        using var image = Capture.Element(session.Window);
        image.ToFile(path);
        return path;
    }

    private static void WriteTimings(object timings)
    {
        string directory = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", "tree");
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "perf-10k.json"), json + Environment.NewLine);
    }
}
