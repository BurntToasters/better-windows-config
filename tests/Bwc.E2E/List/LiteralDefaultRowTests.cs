using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.ValueDisplay;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.List;

/// <summary>
/// A key can hold the unnamed default value and a value literally named (Default). Both rows show the text (Default),
/// so a list row must be identified by its raw name. F5 keeps the selection on the row that was selected, and the two
/// rows are restored separately. Each run key is removed in finally.
/// </summary>
[TestClass]
public sealed class LiteralDefaultRowTests
{
    private const string DefaultLabel = "(Default)";
    private const string ValueNotSet = "(value not set)";
    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void List_LiteralDefaultRowsKeepTheirIdentity()
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
            E2EResults.Record("list.identity.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedWithDefault(sandbox);
            SeedWithoutDefault(sandbox);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("list-identity"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            CheckKey(session, config, "Identity", ExpectedWithDefault(), "list.identity");
            CheckKey(session, config, "IdentityLiteral", ExpectedWithoutDefault(), "list.identityliteral");
            session.Close();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    private static void SeedWithDefault(SandboxRegistry sandbox)
    {
        sandbox.SetString("Identity", string.Empty, "default text");
        sandbox.SetString("Identity", DefaultLabel, "literal text");
    }

    private static void SeedWithoutDefault(SandboxRegistry sandbox)
    {
        sandbox.SetString("IdentityLiteral", DefaultLabel, "literal only");
    }

    /// <summary>Navigates to one key, checks its rows, then checks each row keeps its selection through F5.</summary>
    private static void CheckKey(AppSession session, E2EConfig config, string childKey, ValueRow[] expected, string checkPrefix)
    {
        bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey);
        E2EResults.Record($"{checkPrefix}.navigate", navigated, $"Navigated to '{childKey}': {navigated}.");
        if (!navigated)
        {
            return;
        }

        _ = ControlTestSupport.WaitUntil(() => ClassicValueCompare.ReadBwcRows(session).Length >= expected.Length, TimeSpan.FromSeconds(15));
        ValueRow[] rows = ClassicValueCompare.ReadBwcRows(session);
        bool match = rows.Length == expected.Length && expected.Zip(rows).All(pair => pair.First == pair.Second);
        E2EResults.Record(
            $"{checkPrefix}.rows",
            match,
            $"BWC rows [{Describe(rows)}], expected [{Describe(expected)}].");

        for (var index = 0; index < expected.Length; index += 1)
        {
            CheckSelectionSurvivesRefresh(session, index, $"{checkPrefix}.select{index}");
        }
    }

    private static void CheckSelectionSurvivesRefresh(AppSession session, int index, string checkId)
    {
        AutomationElement? target = RowAt(session, index);
        if (target is null)
        {
            E2EResults.Record(checkId, false, $"Row {index} is not in the list.");
            return;
        }

        ControlTestSupport.Select(target);
        bool selectedBefore = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(RowAt(session, index)), TimeSpan.FromSeconds(5));
        ControlTestSupport.SendKeys(session, "F5");
        bool selectedAfter = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(RowAt(session, index)) && OnlyRowSelected(session, index),
            TimeSpan.FromSeconds(8));
        E2EResults.Record(
            checkId,
            selectedBefore && selectedAfter,
            $"Row {index} selected before F5={selectedBefore}; after F5 it is the only selected row={selectedAfter}.");
    }

    private static bool OnlyRowSelected(AppSession session, int index)
    {
        string keep = ControlTestSupport.ListRowPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (AutomationElement row in ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem))
        {
            if (ControlTestSupport.SafeId(row) != keep && ControlTestSupport.IsSelected(row))
            {
                return false;
            }
        }

        return true;
    }

    private static AutomationElement? RowAt(AppSession session, int index)
    {
        string id = ControlTestSupport.ListRowPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .FirstOrDefault(row => ControlTestSupport.SafeId(row) == id);
    }

    private static string Describe(IEnumerable<ValueRow> rows) =>
        string.Join("; ", rows.Select(row => $"{row.Name}|{row.Type}|{row.Data}"));

    private static ValueRow[] ExpectedWithDefault() =>
    [
        new ValueRow(DefaultLabel, "REG_SZ", "default text"),
        new ValueRow(DefaultLabel, "REG_SZ", "literal text"),
    ];

    private static ValueRow[] ExpectedWithoutDefault() =>
    [
        new ValueRow(DefaultLabel, "REG_SZ", ValueNotSet),
        new ValueRow(DefaultLabel, "REG_SZ", "literal only"),
    ];

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"List identity checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
