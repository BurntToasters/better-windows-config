using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Controls;

/// <summary>
/// Drives the dense tree in the shipped Registry Editor against a seeded HKCU sandbox: expand,
/// type-ahead navigation, real registry values, a 2,000 child chunked expand, and the F2 rename
/// cancel path. Every check is also written to artifacts/e2e/result-&lt;rid&gt;-&lt;flavor&gt;.json.
/// </summary>
[TestClass]
public sealed class TreeTests
{
    private const int RegeditWideChildCount = 2_000;

    private static int _checkStart;

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
    public void Tree_ExpandsNavigatesAndCancelsRename()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("controls.tree.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        try
        {
            var sandbox = ControlTestSupport.SeedRegistry(config, RegeditWideChildCount);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("controls-tree"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(
                config,
                config.RegeditExePath,
                environment: environment);

            var tree = session.Find(ControlTestSupport.TreeControl);
            bool found = tree is not null;
            E2EResults.Record("controls.tree.found", found, found ? "TreeControl is present." : "TreeControl is missing.");
            if (!found)
            {
                FailIfNeeded();
                return;
            }

            var rootRow = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                "Computer");
            bool rootOk = rootRow is not null &&
                string.Equals(ControlTestSupport.SafeId(rootRow), "TreeRow_0", StringComparison.Ordinal);
            E2EResults.Record(
                "controls.tree.root",
                rootOk,
                $"root name='{ControlTestSupport.SafeName(rootRow)}' id='{ControlTestSupport.SafeId(rootRow)}'.");

            var hkcu = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                "HKEY_CURRENT_USER");
            bool hkcuOk = hkcu is not null &&
                string.Equals(ControlTestSupport.StateOf(hkcu), "Collapsed", StringComparison.Ordinal);
            E2EResults.Record(
                "controls.tree.hkcu",
                hkcuOk,
                $"name='{ControlTestSupport.SafeName(hkcu)}' state={ControlTestSupport.StateOf(hkcu)}.");
            if (hkcu is null)
            {
                FailIfNeeded();
                return;
            }

            ControlTestSupport.Select(hkcu);
            _ = ControlTestSupport.WaitForTreeSelection(session, "HKEY_CURRENT_USER");
            ControlTestSupport.SendKeys(session, "Right");
            bool softwareVisible = WaitForRow(session, "Software");
            E2EResults.Record(
                "controls.tree.expand.hkcu",
                softwareVisible,
                $"state={ControlTestSupport.StateOf(hkcu)}; Software visible={softwareVisible}.");

            TypeAhead(session, "Software");
            bool softwareSelected = ControlTestSupport.WaitForTreeSelection(session, "Software");
            E2EResults.Record(
                "controls.tree.typeahead.software",
                softwareSelected,
                $"type-ahead selected Software={softwareSelected}.");

            ControlTestSupport.SendKeys(session, "Right");
            bool sandboxVisible = WaitForRow(session, "BWC-E2E");
            E2EResults.Record(
                "controls.tree.expand.software",
                sandboxVisible,
                $"Right expanded Software and BWC-E2E is visible={sandboxVisible}.");

            TypeAhead(session, "BWC-E2E");
            bool sandboxSelected = ControlTestSupport.WaitForTreeSelection(session, "BWC-E2E");
            E2EResults.Record(
                "controls.tree.typeahead.sandbox",
                sandboxSelected,
                $"type-ahead selected BWC-E2E={sandboxSelected}.");

            ControlTestSupport.SendKeys(session, "Right");
            bool runVisible = WaitForRow(session, config.RunId);
            E2EResults.Record(
                "controls.tree.expand.sandbox",
                runVisible,
                $"Right expanded BWC-E2E and run key '{config.RunId}' is visible={runVisible}.");

            TypeAhead(session, config.RunId);
            bool runSelected = ControlTestSupport.WaitForTreeSelection(session, config.RunId);
            E2EResults.Record(
                "controls.tree.typeahead.run",
                runSelected,
                $"type-ahead selected the full run id={runSelected}.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, runSelected, "controls.tree.sandbox");

            bool valuesOk = ControlTestSupport.WaitUntil(
                () => ContainsAll(
                    ControlTestSupport.ListRowNames(session),
                    "(Default)",
                    "AlphaString",
                    "EmptyString",
                    "MultiString",
                    "SampleBinary",
                    "SampleDword",
                    "ZebraString"),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "controls.tree.values.real",
                valuesOk,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            ControlTestSupport.SendKeys(session, "Left");
            bool parentSelected = ControlTestSupport.WaitForTreeSelection(session, "BWC-E2E");
            E2EResults.Record("controls.tree.keyboard.left.parent", parentSelected, "Left moves to BWC-E2E.");

            ControlTestSupport.SendKeys(session, "Left");
            bool runCollapsed = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    config.RunId) is null,
                TimeSpan.FromSeconds(5));
            E2EResults.Record("controls.tree.keyboard.left.collapse", runCollapsed, "Left collapses BWC-E2E.");

            ControlTestSupport.SendKeys(session, "Left");
            bool softwareParentSelected = ControlTestSupport.WaitForTreeSelection(session, "Software");
            E2EResults.Record(
                "controls.tree.keyboard.left.parent.software",
                softwareParentSelected,
                "Left moves from BWC-E2E to Software.");

            ControlTestSupport.SendKeys(session, "Left");
            bool softwareCollapsed = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    "BWC-E2E") is null,
                TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "controls.tree.keyboard.left.parent.collapse",
                softwareCollapsed,
                "Left collapses Software.");

            ControlTestSupport.SendKeys(session, "Right");
            _ = WaitForRow(session, "BWC-E2E");
            TypeAhead(session, "BWC-E2E");
            _ = ControlTestSupport.WaitForTreeSelection(session, "BWC-E2E");
            ControlTestSupport.SendKeys(session, "Right");
            _ = WaitForRow(session, config.RunId);
            TypeAhead(session, config.RunId);
            _ = ControlTestSupport.WaitForTreeSelection(session, config.RunId);

            ControlTestSupport.SendKeys(session, "Right");
            bool wideVisible = WaitForRow(session, "Wide");
            TypeAhead(session, "Wide");
            bool wideSelected = ControlTestSupport.WaitForTreeSelection(session, "Wide");

            int before = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.RowCountFromHelpText(session, ControlTestSupport.TreeControl) >= 0,
                TimeSpan.FromSeconds(5))
                ? ControlTestSupport.RowCountFromHelpText(session, ControlTestSupport.TreeControl)
                : -1;

            var wide = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                "Wide");
            bool wideExpand = wideVisible && wideSelected && ControlTestSupport.Expand(wide);
            bool wideCountOk = wideExpand && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.RowCountFromHelpText(session, ControlTestSupport.TreeControl) >= before + RegeditWideChildCount,
                ControlTestSupport.ExpandTimeout);
            int after = ControlTestSupport.RowCountFromHelpText(session, ControlTestSupport.TreeControl);
            E2EResults.Record(
                "controls.tree.expand.wide",
                wideCountOk,
                $"wideVisible={wideVisible} wideSelected={wideSelected} before={before} after={after} expectedAtLeast={before + RegeditWideChildCount}.");

            ControlTestSupport.SendKeys(session, "Home");
            bool computerSelected = ControlTestSupport.WaitForTreeSelection(session, "Computer");
            E2EResults.Record(
                "controls.tree.keyboard.home",
                computerSelected,
                $"Home selected Computer={computerSelected}.");

            // The rename starts on the run key inside the sandbox. The virtual Computer node is never a target.
            TypeAhead(session, config.RunId);
            bool renameTargetSelected = ControlTestSupport.WaitForTreeSelection(session, config.RunId);
            E2EGuards.RequireSandboxNavigation(session, sandbox, renameTargetSelected, "controls.tree.sandbox.rename");

            ControlTestSupport.SendKeys(session, "F2");
            bool renameOpened = ControlTestSupport.WaitUntil(
                () => session.Find("TreeRenameBox") is not null,
                TimeSpan.FromSeconds(5));
            E2EResults.Record("controls.tree.rename.open", renameOpened, "F2 opened TreeRenameBox.");

            ControlTestSupport.SendKeys(session, "Escape");
            bool renameClosed = ControlTestSupport.WaitUntil(
                () => session.Find("TreeRenameBox") is null,
                TimeSpan.FromSeconds(5));
            E2EResults.Record("controls.tree.rename.cancel", renameClosed, "Escape closed TreeRenameBox.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// Waits past the tree's 1000 ms type-ahead window before typing, so the prefix starts a new search.
    /// </summary>
    private static void TypeAhead(AppSession session, string text)
    {
        Thread.Sleep(1100);
        ControlTestSupport.SendKeys(session, text);
    }

    private static bool WaitForRow(AppSession session, string name)
    {
        return ControlTestSupport.WaitUntil(
            () => ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                name) is not null,
            ControlTestSupport.ExpandTimeout);
    }

    private static bool ContainsAll(IReadOnlyList<string> names, params string[] expected)
    {
        foreach (string name in expected)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Control checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
