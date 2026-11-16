using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Navigation;

/// <summary>
/// Sibling key order in the tree for non-ASCII and symbol names (VIEW-009, OQ-25). The same eleven names are seeded under
/// the run key here and under BWC-Oracle\TreeOrder by the oracle capture `tree-order`. The classic order is read from
/// docs/parity/oracle/tree-order.json, which the capture wrote from classic regedit's own tree. BWC's tree rows must
/// list the names in that order.
/// </summary>
[TestClass]
public sealed class TreeOrderTests
{
    // The names, inserted in this order. Their classic display order is not this order.
    private static readonly string[] SeedNames = ["Z", "10", "a", "é", "2", "B", "ä", "_x", "1", "-x", "~x"];

    private const string TreeControl = ControlTestSupport.TreeControl;
    private const string OracleFile = "tree-order.json";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Navigation")]
    [TestCategory("Mutating")]
    public void Navigation_TreeSiblingOrderMatchesClassic()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        string oraclePath = Path.Combine(config.RepoRoot, "docs", "parity", "oracle", OracleFile);
        string[] classicOrder = ReadClassicOrder(oraclePath, out string[] measuredInsertion);
        E2EResults.Record(
            "treeorder.oracle",
            classicOrder.Length == SeedNames.Length && measuredInsertion.SequenceEqual(SeedNames, StringComparer.Ordinal),
            $"oracle file '{oraclePath}' holds {classicOrder.Length} classic names, insertion order matches the seeds={measuredInsertion.SequenceEqual(SeedNames, StringComparer.Ordinal)}.");
        if (classicOrder.Length != SeedNames.Length)
        {
            Assert.Fail($"The oracle order is missing or does not cover the seeds. Run 'npm run oracle -- --only tree-order' ({oraclePath}).");
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            foreach (string name in SeedNames)
            {
                sandbox.CreateKey(name);
            }

            using var session = StartApp(config, sandbox);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "treeorder.navigate");

            ExpandRunKey(session, config.RunId);
            bool listed = ControlTestSupport.WaitUntil(
                () => SeedNames.All(name => ControlTestSupport.FindRowByName(session, TreeControl, ControlType.TreeItem, name) is not null),
                ControlTestSupport.UiTimeout);
            E2EResults.Record(
                "treeorder.listed",
                listed,
                $"tree rows with the seeded names are shown={listed}.");

            string[] bwcOrder = ControlTestSupport.Rows(session, TreeControl, ControlType.TreeItem)
                .Select(ControlTestSupport.SafeName)
                .Where(name => SeedNames.Contains(name, StringComparer.Ordinal))
                .ToArray();
            bool same = bwcOrder.SequenceEqual(classicOrder, StringComparer.Ordinal);
            E2EResults.Record(
                "treeorder.matches-classic",
                listed && same,
                $"BWC order=[{string.Join(",", bwcOrder)}], classic order=[{string.Join(",", classicOrder)}].");

            var failures = E2EResults.FailedChecksSince(checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Tree order checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void ExpandRunKey(AppSession session, string runId)
    {
        var run = ControlTestSupport.FindRowByName(session, TreeControl, ControlType.TreeItem, runId);
        _ = ControlTestSupport.Expand(run);
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox)
    {
        string dataRoot = config.DataRootFor("navigation-treeorder");
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }

        Directory.CreateDirectory(dataRoot);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    private static string[] ReadClassicOrder(string path, out string[] insertionOrder)
    {
        insertionOrder = [];
        if (!File.Exists(path))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        insertionOrder = document.RootElement.GetProperty("insertionOrder").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        return document.RootElement.GetProperty("classicOrder").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
    }
}
