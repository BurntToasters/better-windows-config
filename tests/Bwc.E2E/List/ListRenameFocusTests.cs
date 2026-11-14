using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.Hex;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.List;

/// <summary>
/// Renames a value with F2, types a new name, then clicks another row. Classic regedit commits the
/// rename on focus loss, so the typed name must be written to the registry.
/// </summary>
[TestClass]
public sealed class ListRenameFocusTests
{
    private const string RenameSource = "FocusRenameA";
    private const string RenameOther = "FocusRenameB";
    private const string RenameTarget = "FocusRenamed";
    private const string ListRenameBox = "ListRenameBox";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ListRename")]
    [TestCategory("Mutating")]
    [TestCategory("E2EOnly")]
    public void ListRenameFocus_ClickingAnotherRowCommitsTheRename()
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
            E2EResults.Record("listrename.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            using (var root = sandbox.CreateSubKey(string.Empty))
            {
                root.SetValue(RenameSource, "a", RegistryValueKind.String);
                root.SetValue(RenameOther, "b", RegistryValueKind.String);
            }

            dataRoot = HexEditingTests.CreateFreshDataRoot(config, "listrename");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(RenameOther, StringComparer.Ordinal),
                TimeSpan.FromSeconds(15));
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, "listrename.sandbox.navigate");

            AutomationElement? source = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.ListControl,
                ControlType.DataItem,
                RenameSource);
            ControlTestSupport.Select(source);
            bool selected = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    RenameSource)),
                TimeSpan.FromSeconds(5));
            E2EGuards.RequireSandboxSelection(session, sandbox, "listrename.sandbox.select");

            bool f2 = selected && driver.TypeKeys("F2");
            bool renameOpen = f2 && ControlTestSupport.WaitUntil(
                () => session.Find(ListRenameBox) is not null,
                TimeSpan.FromSeconds(5));
            bool typed = renameOpen && driver.TypeKeys(RenameTarget);

            AutomationElement? other = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.ListControl,
                ControlType.DataItem,
                RenameOther);
            bool clicked = false;
            if (typed && other is not null)
            {
                other.Click();
                clicked = true;
            }

            bool committed = clicked && ControlTestSupport.WaitUntil(
                () => sandbox.ReadString(string.Empty, RenameTarget) == "a" &&
                    sandbox.ReadString(string.Empty, RenameSource) is null,
                TimeSpan.FromSeconds(10));
            string? stored = sandbox.ReadString(string.Empty, RenameTarget);

            E2EResults.Record(
                "listrename.focusloss.commit",
                selected && renameOpen && typed && clicked && committed,
                $"selected={selected}, renameOpen={renameOpen}, typed={typed}, clicked={clicked}, " +
                $"committed={committed}, stored='{stored ?? "(missing)"}', expected='{RenameTarget}'.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            HexEditingTests.DeleteDataRoot(dataRoot);
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"List rename focus checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
