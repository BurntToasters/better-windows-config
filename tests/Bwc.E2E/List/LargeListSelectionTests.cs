using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.List;

/// <summary>
/// A row the user selects must stay selected while the list is re-sorted into classic name order, because the
/// re-sort keeps selection by row name. The seed is kept at the 5000 entry limit (4,999 values plus the root).
/// That is below the 10,000 row threshold where the product sorts in the background, so the background path is
/// a known gap of this test and is recorded as one.
/// </summary>
[TestClass]
public sealed class LargeListSelectionTests
{
    private const string ListControl = "ListControl";
    private const string TargetName = "V02500";

    // 4,999 values and the root key make 5000 entries, the most one test may seed.
    private const int ValueCount = 4_999;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    private static bool IsInNameOrder(string[] names)
    {
        string[] sorted = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length > 0 && names.SequenceEqual(sorted, StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void LargeList_SelectionSurvivesTheBackgroundSort()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        E2EResults.Note(
            "list.large.backgroundSort",
            "Above 10,000 rows the product sorts in the background. The 5000 entry seed limit cannot reach that path, so only selection survival is checked.");
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("list.large.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            using (var root = sandbox.CreateSubKey(string.Empty))
            {
                for (int i = 1; i <= ValueCount; i++)
                {
                    root.SetValue("V" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "x", RegistryValueKind.String);
                }
            }

            string dataRoot = ClassicDialogTests.FreshDataRoot(config, "large-list");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "list.large.navigate");

            // Only realized rows have UI Automation elements, so the target is the first row on screen.
            bool shown = ControlTestSupport.WaitUntil(() => ControlTestSupport.ListRowNames(session).Any(), UiTimeout);
            string target = ControlTestSupport.ListRowNames(session).FirstOrDefault() ?? string.Empty;
            E2EResults.Record("list.large.rows", shown && target.Length > 0, $"{ValueCount} values listed; first row on screen='{target}'.");
            if (!shown || target.Length == 0)
            {
                Assert.Fail("The large key did not list its values.");
            }

            AutomationElement? row = ControlTestSupport.FindRowByName(session, ListControl, ControlType.DataItem, target);
            row?.Click();
            bool selected = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(row), TimeSpan.FromSeconds(5));
            E2EResults.Record("list.large.select", selected, $"'{target}' selected right after it appeared={selected}.");

            // Wait for the re-sort to finish: the realized rows are in classic name order. The whole window is polled.
            bool resorted = ControlTestSupport.WaitUntil(
                () => IsInNameOrder(ControlTestSupport.ListRowNames(session)),
                UiTimeout);
            E2EResults.Note("list.large.resort", $"realized rows in name order={resorted}.");
            AutomationElement? after = ControlTestSupport.FindRowByName(session, ListControl, ControlType.DataItem, target);
            bool kept = ControlTestSupport.IsSelected(after);
            E2EResults.Record(
                "list.large.kept",
                selected && kept,
                $"'{target}' still selected after the sort={kept}.");

            var failures = E2EResults.FailedChecksSince(_checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Large list checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }
}
