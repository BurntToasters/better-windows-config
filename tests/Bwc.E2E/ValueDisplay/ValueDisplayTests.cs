using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ValueDisplay;

/// <summary>
/// Reads the Data column of the details list through UI Automation for a sandbox REG_DWORD of
/// 5000. Classic regedit shows it as 0x00001388 (5000) (oracle string 29). The check records a
/// known gap under E1 when the difference is still present, and a pass once it is gone. The run
/// key is removed in finally.
/// </summary>
[TestClass]
public sealed class ValueDisplayTests
{
    private const string DwordName = "Dword5000";
    private const string ExpectedDataText = "0x00001388 (5000)";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ValueDisplay")]
    [TestCategory("Mutating")]
    public void ValueDisplay_DwordDataColumnShowsOracleHexAndDecimal()
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
            E2EResults.Record("valuedisplay.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        try
        {
            var sandbox = new SandboxRegistry(config);
            sandbox.Cleanup();
            sandbox.SetDword(string.Empty, DwordName, 5000);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("value-display"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(
                config,
                config.RegeditExePath,
                environment: environment);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record(
                "valuedisplay.navigate",
                navigated,
                $"navigated to '{config.RunId}' through expand and type-ahead={navigated}.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "valuedisplay.sandbox");

            bool listed = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(DwordName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EGuards.RequireSandboxNavigation(session, sandbox, listed, "valuedisplay.list");

            AutomationElement? row = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.ListControl,
                ControlType.DataItem,
                DwordName);
            IReadOnlyList<string> texts = row is null ? Array.Empty<string>() : TextsUnder(row);

            // The Data column is not exposed as its own element in every build. When it is not
            // among the texts, the observed list says so and the gap stays visible in the report.
            bool matches = texts.Contains(ExpectedDataText, StringComparer.Ordinal);
            E2EResults.RecordOrKnownGap(
                "valuedisplay.dword.data",
                matchesOracle: matches,
                workPackage: "E1",
                expectedOracleBehavior: $"The Data column shows '{ExpectedDataText}' for a REG_DWORD 5000 (oracle string 29).",
                observed: $"row texts=[{string.Join(", ", texts)}].");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    private static IReadOnlyList<string> TextsUnder(AutomationElement row)
    {
        var texts = new List<string>();
        foreach (AutomationElement element in row.FindAllDescendants())
        {
            string name = ControlTestSupport.SafeName(element);
            if (name.Length > 0)
            {
                texts.Add(name);
            }
        }

        return texts;
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Value display checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
