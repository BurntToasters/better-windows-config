using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Navigation;

/// <summary>
/// The value list is a snapshot (BEH-007). A value written outside the app is NOT listed until F5 (View > Refresh,
/// accelerator 648), and F5 lists it while the selected key stays selected. The absence is checked after a settle
/// period, so a registry watcher that updated the list on its own would fail the test.
/// </summary>
[TestClass]
public sealed class RefreshTests
{
    private const string AlphaName = "AlphaString";
    private const string AlphaData = "alpha";
    private const string OutsideName = "OutsideAdded";
    private const string OutsideData = "written outside the app";
    private static readonly TimeSpan SettlePeriod = TimeSpan.FromSeconds(3);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Navigation")]
    [TestCategory("Mutating")]
    public void Navigation_RefreshShowsValueAddedOutside()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            sandbox.SetString(string.Empty, AlphaName, AlphaData);

            string dataRoot = config.DataRootFor("navigation-refresh");
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

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "refresh.navigate");
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(AlphaName, StringComparer.OrdinalIgnoreCase),
                ControlTestSupport.UiTimeout);
            E2EResults.Record("refresh.seed", seeded, $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            if (!seeded)
            {
                Assert.Fail("The sandbox value was not listed.");
            }

            // The value is written behind the app's back, under the run root, and the app is given time to notice it.
            sandbox.SetString(string.Empty, OutsideName, OutsideData);
            Thread.Sleep(SettlePeriod);
            bool absentBeforeF5 = !ControlTestSupport.ListRowNames(session).Contains(OutsideName, StringComparer.OrdinalIgnoreCase);
            E2EResults.Record(
                "refresh.absent-before-f5",
                absentBeforeF5,
                $"listed before F5={!absentBeforeF5}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            bool sent;
            try
            {
                ControlTestSupport.SendKeys(session, "F5");
                sent = true;
            }
            catch (Exception)
            {
                sent = false;
            }

            bool listed = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(OutsideName, StringComparer.OrdinalIgnoreCase),
                ControlTestSupport.UiTimeout);
            E2EResults.Record(
                "refresh.listed-after-f5",
                sent && listed,
                $"F5 sent={sent}, listed={listed}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            bool selectionKept = ControlTestSupport.WaitForTreeSelection(session, config.RunId, ControlTestSupport.ExpandTimeout);
            E2EResults.Record(
                "refresh.selection-kept",
                selectionKept,
                $"the run key is still selected after F5={selectionKept}.");

            var failures = E2EResults.FailedChecksSince(checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Refresh checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }
}
