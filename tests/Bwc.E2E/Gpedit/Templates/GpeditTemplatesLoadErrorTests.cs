using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Templates;

/// <summary>
/// Load-error list tests (finding G6-1): the template problems that the model records are shown, as a
/// status bar notice and as
/// the Problems list of the Add/Remove Templates dialog, and one bad template does not hide the others.
/// The cases are the H
/// group captures (VIEW-038 to VIEW-041, ADMX-003 to ADMX-011). The failure modes are in
/// docs/failure-modes/gpedit-templates.md,
/// section 10. Every test writes artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture,
/// and its checks to the E2E
/// result artifact.
/// </summary>
[TestClass]
public sealed class GpeditTemplatesLoadErrorTests
{
    /// <summary>The cases that raised the error box in classic and left the template out (VIEW-038), with
    /// the engine code reported.</summary>
    private static readonly (string Case, string Code)[] BoxCases =
    [
        ("MalformedXml", "MalformedXml"),
        ("MissingAdml", "MissingAdml"),
        ("DtdEntity", "DtdProhibited"),
        ("UndefinedString", "UndefinedString"),
        ("DuplicatePolicy", "DuplicatePolicy"),
        ("ListItemNoKey", "MissingAttribute"),
        ("BooleanNoValueName", "MissingAttribute"),
    ];

    /// <summary>The cases that might have loaded and raised the error box too (VIEW-040).</summary>
    private static readonly string[] AttributeCases = ["ClassLowerCase", "BoolAttributeUpper", "BoolAttributeMixed"];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    private static bool Refresh(GpeditRun run, int expected)
    {
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        _ = run.FocusPane("tree");
        run.Press(GpeditKeys.F5);
        return run.WaitCount("Refreshes", expected) && run.WaitReloadSettled();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Templates_LoadErrors_BoxCases()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("templates", "loaderrors.box");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-templates-loaderrors-box");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases(BoxCases.Select(c => c.Case).ToArray());
        using GpeditRun run = GpeditRun.Open("gpedit-templates-loaderrors-box", sandbox);

        // G6-1: the problems are shown without the user asking. No modal box blocks the window (declared difference).
        checks.Record("notice", run.WaitNotice(shown: true), $"the status bar notice shows; notice='{run.Notice()}'.");
        checks.Record("noticeCount", run.WaitNoticeCount(BoxCases.Length), $"the notice holds {BoxCases.Length}; notice='{run.Notice()}'.");
        checks.Record("noModalBox", run.Shell.Session.IsEnabled && run.Str("Loaded") == "1", $"the window is enabled, Loaded='{run.Str("Loaded")}'.");

        // VIEW-038, VIEW-041: the good tree stays, and none of the bad categories is in it.
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        string[] rows = run.RowNamesNow();
        checks.Record("tree.good", rows.Contains(Oracle), $"rows={string.Join("|", rows)}.");
        checks.Record("tree.noBadCategory", rows.All(n => !n.StartsWith("Bad fixture", StringComparison.Ordinal)), $"rows={string.Join("|", rows)}.");
        checks.Record("policyCount", run.Count("PolicyCount") == 70, $"PolicyCount={run.Str("PolicyCount")}; the good template alone provides 70.");
        run.GoTo(BasicsPath);
        checks.Record("goodPolicies", run.Rows().Count == 12, $"rows in Basics={run.Rows().Count}.");
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        // The list in the dialog: one row per problem, with the file and the engine code.
        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        List<ProblemRow> problems = dialog.Problems();
        checks.Record("list.count", problems.Count == BoxCases.Length, $"problems={problems.Describe()}.");
        checks.Record("list.noDuplicates", problems.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == problems.Count, $"problems={problems.Describe()}.");
        checks.Record("list.kind", problems.All(p => p.Kind == "TemplateFailure"), $"problems={problems.Describe()}.");
        foreach ((string name, string code) in BoxCases)
        {
            ProblemRow? row = problems.FirstOrDefault(p => p.Name.StartsWith("Bwc" + name, StringComparison.Ordinal));
            checks.Record("list." + name, row is not null && row.Code == code, $"expected a row for Bwc{name} with {code}; problems={problems.Describe()}.");
        }

        checks.Record("list.storeEmpty", dialog.Rows().Count == 0, $"imported templates={dialog.Rows().Describe()}; the problems are not imports.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");

        run.Measure("problems", problems.Describe());
        run.WriteArtifacts("gpedit-templates-loaderrors-box.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Templates_LoadErrors_UndefinedUsing()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("templates", "loaderrors.using");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-templates-loaderrors-using");
        sandbox.SeedStandard();
        sandbox.DialogEnabled = true;
        sandbox.UseTemplatesWithBadCases("UndefinedUsing");
        using GpeditRun run = GpeditRun.Open("gpedit-templates-loaderrors-using", sandbox);

        // VIEW-039: the template loads, its category and its policy show and open. BWC lists the problem as
        // well (declared difference).
        checks.Record("notice", run.WaitNoticeCount(1), $"the notice holds 1; notice='{run.Notice()}'.");
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        const string category = "Bad fixture UndefinedUsing";
        checks.Record("tree.category", run.WaitRow(category), $"rows={string.Join("|", run.RowNamesNow())}.");
        run.GoTo(Root, Computer, Admin, category);
        const string policy = "Bad fixture policy setting";
        checks.Record("policy.row", run.WaitRow(policy), $"rows={string.Join("|", run.RowNamesNow())}.");
        using (GpeditDialog? opened = GpeditDialog.Open(run, policy))
        {
            checks.Record("policy.opens", opened is not null, "the policy dialog opens for the policy of the template that has an undefined using.");
            if (opened is not null)
            {
                _ = opened.Invoke(GpeditDialog.Cancel);
                _ = opened.WaitClosed();
            }
        }

        run.GoTo(GpeditTemplatesRun.AdminComputer);
        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        List<ProblemRow> problems = dialog.Problems();
        checks.Record("list.count", problems.Count == 1, $"problems={problems.Describe()}.");
        checks.Record("list.code", problems.Count == 1 && problems[0].Code == "UndefinedUsing" && problems[0].Name.StartsWith("BwcUndefinedUsing", StringComparison.Ordinal), $"problems={problems.Describe()}.");
        checks.Record("list.detail", problems.Count == 1 && problems[0].Detail.Contains("Ghost", StringComparison.Ordinal), $"detail='{problems.FirstOrDefault()?.Detail}'.");
        _ = dialog.Close();

        run.WriteArtifacts("gpedit-templates-loaderrors-using.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Templates_LoadErrors_AttributeCases()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("templates", "loaderrors.attributes");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-templates-loaderrors-attributes");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases(AttributeCases);
        using GpeditRun run = GpeditRun.Open("gpedit-templates-loaderrors-attributes", sandbox);

        // VIEW-040, ADMX-012, ADMX-013: each raised the error box in classic and was left out of the tree.
        checks.Record("notice", run.WaitNoticeCount(AttributeCases.Length), $"the notice holds {AttributeCases.Length}; notice='{run.Notice()}'.");
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        string[] rows = run.RowNamesNow();
        checks.Record("tree.noCategory", rows.All(n => !n.StartsWith("Bad fixture", StringComparison.Ordinal)) && rows.Contains(Oracle), $"rows={string.Join("|", rows)}.");

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        List<ProblemRow> problems = dialog.Problems();
        checks.Record("list.count", problems.Count == AttributeCases.Length, $"problems={problems.Describe()}.");
        foreach (string name in AttributeCases)
        {
            ProblemRow? row = problems.FirstOrDefault(p => p.Name.StartsWith("Bwc" + name, StringComparison.Ordinal));
            checks.Record("list." + name, row is { Code: "InvalidAttributeValue" }, $"expected a row for Bwc{name} with InvalidAttributeValue; problems={problems.Describe()}.");
        }

        _ = dialog.Close();
        run.WriteArtifacts("gpedit-templates-loaderrors-attributes.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Templates_Notice_ClearsAndReturns()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("templates", "notice.clears");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-templates-notice-clears");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases();
        using GpeditRun run = GpeditRun.Open("gpedit-templates-notice-clears", sandbox);

        checks.Record("clean.noNotice", run.Notice().Length == 0 && run.Count("Problems") == 0, $"notice='{run.Notice()}', Problems={run.Str("Problems")}.");

        // A broken template appears while the editor runs; Refresh reads again and the notice and the list show it.
        sandbox.AddBadTemplateCase("MalformedXml");
        checks.Record("added.refreshed", Refresh(run, 1), $"Refreshes={run.Str("Refreshes")}.");
        checks.Record("added.notice", run.WaitNoticeCount(1), $"notice='{run.Notice()}'.");
        using (GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run))
        {
            List<ProblemRow> problems = dialog?.Problems() ?? [];
            checks.Record("added.list", problems.Count == 1 && problems[0].Code == "MalformedXml", $"problems={problems.Describe()}.");
            _ = dialog?.Close();
        }

        // The template goes; the next reload finds nothing, and the notice and the list are gone with it.
        sandbox.RemoveTemplate("BwcMalformedXml");
        checks.Record("removed.refreshed", Refresh(run, 2), $"Refreshes={run.Str("Refreshes")}.");
        checks.Record("removed.noNotice", run.WaitNotice(shown: false), $"notice='{run.Notice()}'.");
        using (GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run))
        {
            checks.Record("removed.list", dialog is not null && dialog.Problems().Count == 0, $"problems={dialog?.Problems().Describe()}.");
            _ = dialog?.Close();
        }

        // Another broken template: the notice returns with the new count, and the list holds only the new
        // problem (not the old one).
        sandbox.AddBadTemplateCase("DtdEntity");
        checks.Record("again.refreshed", Refresh(run, 3), $"Refreshes={run.Str("Refreshes")}.");
        checks.Record("again.notice", run.WaitNoticeCount(1), $"notice='{run.Notice()}'.");
        using (GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run))
        {
            List<ProblemRow> problems = dialog?.Problems() ?? [];
            checks.Record("afterReload.list", problems.Count == 1 && problems[0].Code == "DtdProhibited", $"problems={problems.Describe()}.");
            _ = dialog?.Close();
        }

        run.WriteArtifacts("gpedit-templates-notice-clears.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Templates_Notice_NotAnEvent()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("templates", "notice.stable");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-templates-notice-stable");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases("MalformedXml");
        using GpeditRun run = GpeditRun.Open("gpedit-templates-notice-stable", sandbox);

        // DLG-122: the problem is announced once and the user is not interrupted again; here it is a pane that
        // keeps its text.
        checks.Record("shown", run.WaitNoticeCount(1), $"notice='{run.Notice()}'.");
        string first = run.Notice();
        (string[] Path, string Id)[] nodes =
        [
            (GpeditTemplatesRun.AdminComputer, "adminComputer"),
            (BasicsPath, "basics"),
            ([Root, Computer, Admin, AllSettings], "allSettings"),
            ([Root, User, Admin, Oracle, BasicsName], "userBasics"),
            ([Root, Computer], "computerConfiguration"),
        ];
        foreach ((string[] path, string id) in nodes)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            checks.Record(id + ".same", run.Notice() == first && run.Shell.Session.IsEnabled, $"notice='{run.Notice()}' (expected '{first}'), window enabled={run.Shell.Session.IsEnabled}.");
        }

        // The good policy dialog opens next to the bad template.
        run.GoTo(BasicsPath);
        checks.Record("goodRows", run.Rows().Count == 12, $"rows in Basics={run.Rows().Count}.");
        checks.Record("noReloadStarted", run.Count("TemplateReloads") == 0 && run.Count("Refreshes") <= 0, $"TemplateReloads={run.Str("TemplateReloads")}, Refreshes={run.Str("Refreshes")}: browsing starts no reload.");

        run.WriteArtifacts("gpedit-templates-notice-stable.png");
        checks.AssertAllPassed();
    }
}
