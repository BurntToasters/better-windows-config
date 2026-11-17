using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.List;

/// <summary>
/// Commands on the two (Default) rows of one key. A key can hold a literal value named (Default) and no unnamed value:
/// then the synthetic (Default) row has no value behind it, and Delete on it must refuse with classic's text while the
/// literal stays. Modify on the literal row must change the literal only. A key with a real default and a literal
/// (Default) must lose only the literal when the literal row is deleted. Each run key is removed in finally.
/// </summary>
[TestClass]
public sealed class DefaultRowCommandTests
{
    private const int MenuEdit = 513;
    private const int MenuEditDelete = 913;
    private const string DefaultLabel = "(Default)";
    private const string DeleteRefusedText = "Unable to delete all specified values.";
    private const string ChangedLiteral = "changed literal";
    private const string LiteralKey = "SyntheticLiteral";
    private const string DefaultAndLiteralKey = "DefaultAndLiteral";
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
    public void List_DeleteOnSyntheticDefaultRefusesAndKeepsLiteral()
    {
        _checkStart = E2EResults.CheckCount;
        RunWithKey(LiteralKey, SeedLiteralOnly, "list.cmd.syntheticdelete", (session, sandbox, config) =>
        {
            _ = config;
            bool selected = SelectRow(session, 0);
            using var driver = new EditingDriver(session);
            driver.OpenMenu(MenuEdit, MenuEditDelete);
            bool dialog = driver.WaitForDialog();
            bool confirmed = dialog && driver.Invoke("DialogPrimary");
            bool refused = confirmed && ControlTestSupport.WaitUntil(
                () => driver.TextOf("DialogErrorText") == DeleteRefusedText,
                TimeSpan.FromSeconds(10));
            if (refused)
            {
                _ = driver.DismissErrorBox();
            }

            string? literal = sandbox.ReadString(LiteralKey, DefaultLabel);
            string? other = sandbox.ReadString(LiteralKey, "Other");
            string? unnamed = sandbox.ReadString(LiteralKey, string.Empty);
            E2EResults.Record(
                "list.cmd.syntheticdelete",
                selected && dialog && confirmed && refused && literal == "literal only" && other == "named" && unnamed is null,
                $"selected={selected}, confirm={dialog}, Yes invoked={confirmed}, classic text shown={refused}, " +
                $"literal='{literal}', other='{other}', unnamed default={(unnamed is null ? "absent" : "present")}.");
        });

        FailIfNeeded();
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void List_ModifyLiteralChangesOnlyTheLiteral()
    {
        _checkStart = E2EResults.CheckCount;
        RunWithKey(LiteralKey, SeedLiteralOnly, "list.cmd.literalmodify", (session, sandbox, config) =>
        {
            _ = config;
            bool selected = SelectRow(session, 1);
            using var driver = new EditingDriver(session);
            bool opened = selected && driver.TypeKeys("Enter") && driver.WaitForDialog();
            bool typed = opened && driver.SetText("ValueEditorInput", ChangedLiteral);
            bool saved = typed && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();

            _ = ControlTestSupport.WaitUntil(
                () => sandbox.ReadString(LiteralKey, DefaultLabel) == ChangedLiteral,
                TimeSpan.FromSeconds(10));
            string? literal = sandbox.ReadString(LiteralKey, DefaultLabel);
            string? unnamed = sandbox.ReadString(LiteralKey, string.Empty);
            string? other = sandbox.ReadString(LiteralKey, "Other");
            E2EResults.Record(
                "list.cmd.literalmodify",
                saved && literal == ChangedLiteral && unnamed is null && other == "named",
                $"opened={opened}, typed={typed}, saved={saved}, literal='{literal}', " +
                $"unnamed default={(unnamed is null ? "absent" : "present")}, other='{other}'.");
        });

        FailIfNeeded();
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void List_DeleteLiteralKeepsRealDefault()
    {
        _checkStart = E2EResults.CheckCount;
        RunWithKey(DefaultAndLiteralKey, SeedDefaultAndLiteral, "list.cmd.literaldelete", (session, sandbox, config) =>
        {
            _ = config;
            bool selected = SelectRow(session, 1);
            using var driver = new EditingDriver(session);
            driver.OpenMenu(MenuEdit, MenuEditDelete);
            bool dialog = driver.WaitForDialog();
            bool confirmed = dialog && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();
            bool removed = ControlTestSupport.WaitUntil(
                () => sandbox.ReadString(DefaultAndLiteralKey, DefaultLabel) is null,
                TimeSpan.FromSeconds(10));

            string? unnamed = sandbox.ReadString(DefaultAndLiteralKey, string.Empty);
            E2EResults.Record(
                "list.cmd.literaldelete",
                selected && confirmed && removed && unnamed == "default text",
                $"selected={selected}, confirm={dialog}, confirmed={confirmed}, literal removed={removed}, " +
                $"default='{unnamed}'.");
        });

        FailIfNeeded();
    }

    private static void SeedLiteralOnly(SandboxRegistry sandbox)
    {
        sandbox.SetString(LiteralKey, DefaultLabel, "literal only");
        sandbox.SetString(LiteralKey, "Other", "named");
    }

    private static void SeedDefaultAndLiteral(SandboxRegistry sandbox)
    {
        sandbox.SetString(DefaultAndLiteralKey, string.Empty, "default text");
        sandbox.SetString(DefaultAndLiteralKey, DefaultLabel, "literal text");
    }

    /// <summary>Seeds one key, opens BWC on it, runs the check, and removes the run key in finally.</summary>
    private static void RunWithKey(
        string childKey,
        Action<SandboxRegistry> seed,
        string checkId,
        Action<AppSession, SandboxRegistry, E2EConfig> check)
    {
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record(checkId + ".publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            seed(sandbox);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("list-commands"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, checkId + ".navigate");
            bool listed = ControlTestSupport.WaitUntil(() => RowAt(session, 0) is not null, TimeSpan.FromSeconds(15));
            E2EGuards.RequireSandboxNavigation(session, sandbox, listed, checkId + ".list");
            check(session, sandbox, config);
            session.Close();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>Selects the row at the index, with keyboard focus in the list.</summary>
    private static bool SelectRow(AppSession session, int index)
    {
        AutomationElement? row = RowAt(session, index);
        ControlTestSupport.Select(row);
        return ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(RowAt(session, index)), TimeSpan.FromSeconds(5));
    }

    private static AutomationElement? RowAt(AppSession session, int index)
    {
        string id = ControlTestSupport.ListRowPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .FirstOrDefault(row => ControlTestSupport.SafeId(row) == id);
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"List command checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
