using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Navigation;

/// <summary>
/// Proves the Registry Editor starts on the virtual Computer node, shows the five live roots, and
/// restores the sandbox key selected in a previous process from the same isolated data root.
/// </summary>
[TestClass]
public sealed class NavigationTests
{
    private static readonly string[] RootNames =
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
    [TestCategory("Navigation")]
    public void Navigation_ShowsRootsAndRestoresLastKey()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("navigation.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        string dataRoot = CreateFreshDataRoot(config, "navigation");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
        };
        string expectedPath = $@"Computer\HKEY_CURRENT_USER\Software\BWC-E2E\{config.RunId}\Restore";

        try
        {
            var sandbox = new SandboxRegistry(config);
            sandbox.Cleanup();
            using (var restore = sandbox.CreateSubKey("Restore"))
            {
                restore.SetValue("Marker", "restored", RegistryValueKind.String);
            }

            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            {
                bool rootsVisible = ControlTestSupport.WaitUntil(
                    () => AllRootsVisible(session),
                    TimeSpan.FromSeconds(15));
                E2EResults.Record(
                    "navigation.roots.visible",
                    rootsVisible,
                    $"rows=[{string.Join(",", VisibleTreeRowNames(session))}].");

                var computer = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    "Computer");
                bool computerRoot = computer is not null &&
                    string.Equals(ControlTestSupport.SafeId(computer), "TreeRow_0", StringComparison.Ordinal);
                E2EResults.Record(
                    "navigation.computer.root",
                    computerRoot,
                    $"id='{ControlTestSupport.SafeId(computer)}' name='{ControlTestSupport.SafeName(computer)}'.");
                E2EResults.Record(
                    "navigation.computer.expanded",
                    string.Equals(ControlTestSupport.StateOf(computer), "Expanded", StringComparison.Ordinal),
                    $"Computer ExpandCollapseState={ControlTestSupport.StateOf(computer)}.");

                string initialAddress = session.WaitForText("AddressBar", "Computer", TimeSpan.FromSeconds(15))
                    ?? "(missing)";
                E2EResults.Record(
                    "navigation.address.initial",
                    string.Equals(initialAddress, "Computer", StringComparison.Ordinal),
                    $"expected 'Computer', got '{initialAddress}'.");

                bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey: "Restore");
                E2EResults.Record(
                    "navigation.select.sandbox",
                    navigated,
                    $"navigated to Restore={navigated}.");

                string selectedAddress = session.WaitForText("AddressBar", expectedPath, TimeSpan.FromSeconds(15))
                    ?? "(missing)";
                E2EResults.Record(
                    "navigation.address.selected",
                    string.Equals(selectedAddress, expectedPath, StringComparison.Ordinal),
                    $"expected '{expectedPath}', got '{selectedAddress}'.");

                bool markerVisible = ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.ListRowNames(session).Contains("Marker", StringComparer.Ordinal),
                    TimeSpan.FromSeconds(15));
                E2EResults.Record(
                    "navigation.values.sandbox",
                    markerVisible,
                    $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            }

            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            {
                bool restored = ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.WaitForTreeSelection(session, "Restore", TimeSpan.FromMilliseconds(250)),
                    TimeSpan.FromSeconds(30));
                var restoredRow = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    "Restore");
                E2EResults.Record(
                    "navigation.restore.selection",
                    restored,
                    $"Restore selected={ControlTestSupport.IsSelected(restoredRow)} id='{ControlTestSupport.SafeId(restoredRow)}'.");

                string restoredAddress = session.WaitForText("AddressBar", expectedPath, TimeSpan.FromSeconds(15))
                    ?? "(missing)";
                E2EResults.Record(
                    "navigation.restore.address",
                    string.Equals(restoredAddress, expectedPath, StringComparison.Ordinal),
                    $"expected '{expectedPath}', got '{restoredAddress}'.");

                bool markerAfterRestore = ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.ListRowNames(session).Contains("Marker", StringComparer.Ordinal),
                    TimeSpan.FromSeconds(15));
                E2EResults.Record(
                    "navigation.restore.values",
                    markerAfterRestore,
                    $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            }

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static bool AllRootsVisible(AppSession session)
    {
        foreach (string name in RootNames)
        {
            if (ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                name) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static AutomationElement[] VisibleTreeRows(AppSession session) =>
        ControlTestSupport.Rows(session, ControlTestSupport.TreeControl, ControlType.TreeItem);

    private static string[] VisibleTreeRowNames(AppSession session) =>
        VisibleTreeRows(session).Select(ControlTestSupport.SafeName).ToArray();

    private static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Navigation checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
