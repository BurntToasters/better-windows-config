using System.Security.AccessControl;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Editing;

/// <summary>
/// A tree expand whose subkey enumeration is refused (docs/failure-modes/tree.md, "Enumeration errors other than access
/// denied"). The sandbox key denies KEY_ENUMERATE_SUB_KEYS to the current user, so the open fails with access denied
/// (5). The node must collapse and show the classic "cannot be opened" box, as the remote tree does. The node is expanded
/// from the run root, because selecting the denied key would read its values first. The ACE is restored in finally.
/// </summary>
[TestClass]
public sealed class TreeEnumerationErrorTests
{
    private const string SealedKey = "Sealed";
    private const string InsideKey = "Inside";
    private const string ErrorTextId = "DialogErrorText";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Navigation")]
    [TestCategory("Mutating")]
    public void Tree_EnumerationDeniedShowsCannotOpenKey()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        RegistrySecurity? original = null;
        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey(SealedKey + @"\" + InsideKey);
            original = sandbox.DenyCurrentUser(SealedKey, RegistryRights.EnumerateSubKeys);
            E2EResults.Record(
                "tree-enumerate.seed",
                original is not null,
                "A child under the sealed key, and a deny ACE for KEY_ENUMERATE_SUB_KEYS on the sealed key.");

            using var session = StartApp(config, sandbox, "tree-enumerate");
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "tree-enumerate.sandbox.navigate");

            // The run root is selected but not expanded, so its children are not rows yet. Expand it first.
            AutomationElement? runRow = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                config.RunId);
            _ = ControlTestSupport.Expand(runRow);
            AutomationElement? sealedRow = null;
            _ = ControlTestSupport.WaitUntil(
                () => (sealedRow = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    SealedKey)) is not null,
                TimeSpan.FromSeconds(10));
            bool expandRequested = ControlTestSupport.Expand(sealedRow);
            bool errorShown = expandRequested && driver.WaitForDialog(TimeSpan.FromSeconds(15));
            string errorText = errorShown ? driver.TextOf(ErrorTextId) : string.Empty;
            E2EResults.Record(
                "tree-enumerate.message",
                errorShown &&
                    errorText.Contains("cannot be opened", StringComparison.OrdinalIgnoreCase) &&
                    errorText.Contains(SealedKey, StringComparison.OrdinalIgnoreCase),
                $"expand requested={expandRequested}, error box shown={errorShown}, text='{errorText}'.");

            bool dismissed = errorShown && driver.DismissErrorBox();
            string state = ControlTestSupport.StateOf(sealedRow);
            bool collapsed = dismissed && ControlTestSupport.WaitUntil(
                () => string.Equals(ControlTestSupport.StateOf(sealedRow), "Collapsed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "tree-enumerate.collapsed",
                collapsed,
                $"error dismissed={dismissed}, node state before dismiss='{state}', collapsed={collapsed}.");

            bool noRows = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                InsideKey) is null;
            E2EResults.Record(
                "tree-enumerate.no-rows",
                noRows,
                $"the child of the sealed key is not listed={noRows}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            try
            {
                if (original is not null)
                {
                    sandbox.RestoreSecurity(SealedKey, original);
                }
            }
            finally
            {
                ControlTestSupport.CleanupRegistry(config);
            }
        }
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope)
    {
        string dataRoot = config.DataRootFor(scope);
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

    private static void FailIfNeeded(int startIndex)
    {
        var failures = E2EResults.FailedChecksSince(startIndex);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Tree enumeration checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
