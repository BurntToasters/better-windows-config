using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.List;

/// <summary>
/// A key with more than 10,000 values is sorted in the background. The rows are shown at once in their
/// input order and the classic name order is applied when the sort ends. A row the user selected while
/// the sort ran must still be selected afterwards, because the re-sort keeps selection by row name.
/// </summary>
[TestClass]
public sealed class LargeListSelectionTests
{
    private const string ListControl = "ListControl";
    private const string TargetName = "V05000";
    private const int ValueCount = 10_001;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);

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
    public void LargeList_SelectionSurvivesTheBackgroundSort()
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

            // The unsorted rows appear first. Selecting the target at once is what makes the background sort
            // end while the selection is set, which the check below depends on.
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

            // Give the background sort time to finish, then read the selection again.
            Thread.Sleep(TimeSpan.FromSeconds(3));
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
