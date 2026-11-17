using System.Security.AccessControl;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Editing;

/// <summary>
/// A Delete Key that fails after some subkeys are already gone (docs/failure-modes/editing.md, "Delete that fails part
/// way"). The key has two subkeys and DELETE is denied on the key itself, so RegDeleteTree removes the subkeys and then
/// fails on the key. The tree must be rebuilt, so no removed subkey stays on screen. The ACE is restored in finally.
/// </summary>
[TestClass]
public sealed class KeyDeleteFailureTests
{
    private const int MenuEdit = 513;
    private const int MenuEditDelete = 913;
    private const string LockedKey = "Locked";
    private const string FirstChild = "LockedAlpha";
    private const string SecondChild = "LockedBeta";
    private const string ErrorTextId = "DialogErrorText";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Editing")]
    [TestCategory("Mutating")]
    public void Editing_DeleteFailureRebuildsTheTree()
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
            sandbox.CreateKey(LockedKey + @"\" + FirstChild);
            sandbox.CreateKey(LockedKey + @"\" + SecondChild);
            original = sandbox.DenyCurrentUser(LockedKey, RegistryRights.Delete);
            E2EResults.Record(
                "delete-failure.seed",
                original is not null,
                "Two subkeys under the locked key, and a deny-DELETE ACE for the current user on the key.");

            using var session = StartApp(config, sandbox, "delete-failure");
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, LockedKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "delete-failure.sandbox.navigate");

            AutomationElement? locked = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                LockedKey);
            bool expanded = ControlTestSupport.Expand(locked) && ControlTestSupport.WaitUntil(
                () => ContainsTreeRow(session, FirstChild) && ContainsTreeRow(session, SecondChild),
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "delete-failure.expanded",
                expanded,
                $"locked key expanded with both subkeys shown={expanded}.");
            E2EGuards.RequireSandboxSelection(session, sandbox, "delete-failure.sandbox.selection");

            driver.OpenMenu(MenuEdit, MenuEditDelete);
            bool confirm = driver.WaitForDialog();
            bool invoked = confirm && driver.Invoke("DialogPrimary");
            E2EResults.Record(
                "delete-failure.confirmed",
                confirm && invoked,
                $"confirm dialog={confirm}, Yes invoked={invoked}.");

            // The failed delete shows the error box straight after the confirmation closes, so the next dialog is the error.
            bool errorShown = invoked && driver.WaitForDialog(TimeSpan.FromSeconds(15));
            string errorText = errorShown ? driver.TextOf(ErrorTextId) : string.Empty;
            E2EResults.Record(
                "delete-failure.error",
                errorShown && errorText.Length > 0,
                $"error box shown={errorShown}, text='{errorText}'.");

            // The subkeys are gone from the registry while the key itself stays: the halfway state this test needs.
            bool childrenRemoved = !sandbox.Exists(LockedKey + @"\" + FirstChild) &&
                !sandbox.Exists(LockedKey + @"\" + SecondChild);
            bool keyStays = sandbox.Exists(LockedKey);
            E2EResults.Record(
                "delete-failure.halfway",
                childrenRemoved && keyStays,
                $"subkeys removed={childrenRemoved}, locked key kept={keyStays}.");

            bool dismissed = errorShown && driver.DismissErrorBox();
            bool treeRebuilt = dismissed && ControlTestSupport.WaitUntil(
                () => !ContainsTreeRow(session, FirstChild) && !ContainsTreeRow(session, SecondChild),
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "delete-failure.tree",
                treeRebuilt,
                $"error dismissed={dismissed}, removed subkeys no longer in the tree={treeRebuilt}.");

            bool stillSelected = ControlTestSupport.WaitForTreeSelection(session, LockedKey, TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "delete-failure.selection",
                stillSelected,
                $"the failed key stays selected={stillSelected}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            try
            {
                if (original is not null)
                {
                    sandbox.RestoreSecurity(LockedKey, original);
                }
            }
            finally
            {
                ControlTestSupport.CleanupRegistry(config);
            }
        }
    }

    private static bool ContainsTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name) is not null;

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
            $"Delete failure checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
