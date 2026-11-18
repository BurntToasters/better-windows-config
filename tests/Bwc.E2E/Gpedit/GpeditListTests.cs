using System.Globalization;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Result list tests of the Local Group Policy Editor over the F1 fixture templates and a sandbox policy object that the
/// test seeds: the folder rows and columns of a category, the State and Comment cells, the Extended pane, the status bar
/// and an unreadable policy file. The failure modes are in docs/failure-modes/gpedit-tree-list.md. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditListTests
{
    private const string Root = GpeditRun.RootName;
    private const string Computer = "Computer Configuration";
    private const string User = "User Configuration";
    private const string Admin = "Administrative Templates";
    private const string Oracle = "BWC Oracle";

    private static readonly string[] ComputerBasics =
    [
        "Class both flag setting",
        "Class both with an element setting",
        "Class machine flag setting",
        "Decimal enabled and disabled values setting",
        "Disabled list only setting",
        "Disabled value only setting",
        "Element key differs from policy key setting",
        "Enabled list and disabled list with value name setting",
        "Enabled list only setting",
        "Enabled value only setting",
        "Key outside the policy roots setting",
        "No value name, elements only setting",
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_List_Category()
    {
        var checks = new GpeditChecks("list", "category");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-list-category");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-list-category", sandbox);

        run.GoTo(Root, Computer, Admin, Oracle);
        string[] computerRows = run.Rows().Select(r => r.Name).ToArray();
        string[] expectedComputer = ["Basics", "Elements", "Explain text", "Machine only", "Shared keys", "Sort cases", "Tools & Utilities"];
        checks.Record("computerFolders", computerRows.SequenceEqual(expectedComputer), $"rows={string.Join("|", computerRows)}.");
        checks.Record("computerCounts", run.Int("ShownFolders") == 7 && run.Int("ShownPolicies") == 0, $"folders={run.Int("ShownFolders")}, policies={run.Int("ShownPolicies")}.");
        checks.Record("settingsLayout", run.Str("Layout") == "Settings", $"layout={run.Str("Layout")}.");

        string[] headers = run.HeaderNames("setting", "state", "comment");
        checks.Record("headers", headers.SequenceEqual(["Setting", "State", "Comment"]), $"headers={string.Join("|", headers)}.");

        // The probe widths of the classic columns, in device independent pixels. The Standard tab gives the list the whole pane,
        // so no column is clipped by the description pane.
        run.Shell.Find("ConsoleExtendedTab_Standard")?.Click();
        _ = ShellRun.WaitUntil(() => run.Rect(GpeditRun.ListRun_HeaderItemId("comment")) is not null, TimeSpan.FromSeconds(5));
        double[] widths = new[] { "setting", "state", "comment" }.Select(id => run.Rect(GpeditRun.ListRun_HeaderItemId(id)) is { } rect ? rect.Width / run.Scale : -1).ToArray();
        checks.Record(
            "columnWidths",
            Near(widths[0], 350) && Near(widths[1], 100) && Near(widths[2], 139),
            $"widths={string.Join("|", widths.Select(w => w.ToString("0.#", CultureInfo.InvariantCulture)))} DIP; classic 350, 100 and 139.");
        run.WriteArtifacts("gpedit-list-category-computer.png");

        // A user category lists its folders first and then its own policy.
        run.GoTo(Root, User, Admin, Oracle);
        string[] userRows = run.Rows().Select(r => r.Name).ToArray();
        string[] expectedUser = ["Basics", "Elements", "User only", "Top level user flag setting"];
        checks.Record("userFoldersThenPolicy", userRows.SequenceEqual(expectedUser), $"rows={string.Join("|", userRows)}.");
        checks.Record("userCounts", run.Int("ShownFolders") == 3 && run.Int("ShownPolicies") == 1, $"folders={run.Int("ShownFolders")}, policies={run.Int("ShownPolicies")}.");

        // Opening a folder row selects its tree node.
        run.GoTo(Root, User, Admin, Oracle, "User only");
        checks.Record("folderRowOpens", run.NodePath().SequenceEqual([Root, User, Admin, Oracle, "User only"]), $"node={run.Str("Node")}.");
        checks.Record("userOnlyRows", run.Rows().Select(r => r.Name).SequenceEqual(["User nested"]), $"rows={string.Join("|", run.Rows().Select(r => r.Name))}.");

        run.Measure("computerRows", computerRows);
        run.Measure("userRows", userRows);
        run.Measure("widthsDip", widths);
        run.WriteArtifacts("gpedit-list-category.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_List_States()
    {
        var checks = new GpeditChecks("list", "states");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-list-states");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-list-states", sandbox);

        run.GoTo(Root, Computer, Admin, Oracle, "Basics");
        List<GpeditRow> computer = run.Rows();
        checks.Record("computerOrder", computer.Select(r => r.Name).SequenceEqual(ComputerBasics), $"rows={string.Join("|", computer.Select(r => r.Name))}.");
        checks.Record("computerCells", computer.All(r => r.Cells.Length == 3), $"cells per row={string.Join(",", computer.Select(r => r.Cells.Length))}.");
        string[] machineStates = computer.Select(r => r.Cells.ElementAtOrDefault(1) ?? string.Empty).ToArray();
        string[] machineComments = computer.Select(r => r.Cells.ElementAtOrDefault(2) ?? string.Empty).ToArray();
        string[] expectedMachineStates =
        [
            "Enabled", "Not configured", "Enabled", "Disabled", "Not configured", "Not configured",
            "Not configured", "Not configured", "Not configured", "Not configured", "Not configured", "Not configured",
        ];
        string[] expectedMachineComments = ["Yes", "No", "Yes", "No", "No", "No", "No", "No", "No", "No", "No", "No"];
        checks.Record("computerStates", machineStates.SequenceEqual(expectedMachineStates), $"states={string.Join("|", machineStates)}.");
        checks.Record("computerComments", machineComments.SequenceEqual(expectedMachineComments), $"comments={string.Join("|", machineComments)}.");
        run.WriteArtifacts("gpedit-list-states-computer.png");

        // The class Both policy reads each side's own file and comment.
        run.GoTo(Root, User, Admin, Oracle, "Basics");
        List<GpeditRow> user = run.Rows();
        string[] expectedUser =
        [
            "Class both flag setting",
            "Class both with an element setting",
            "Class user flag setting",
            "String enabled and disabled values setting",
        ];
        checks.Record("userOrder", user.Select(r => r.Name).SequenceEqual(expectedUser), $"rows={string.Join("|", user.Select(r => r.Name))}.");
        string[] userStates = user.Select(r => r.Cells.ElementAtOrDefault(1) ?? string.Empty).ToArray();
        string[] userComments = user.Select(r => r.Cells.ElementAtOrDefault(2) ?? string.Empty).ToArray();
        checks.Record("userStates", userStates.SequenceEqual(["Disabled", "Not configured", "Enabled", "Not configured"]), $"states={string.Join("|", userStates)}.");
        checks.Record("userComments", userComments.SequenceEqual(["No", "No", "No", "No"]), $"comments={string.Join("|", userComments)}; the comment of Class both flag is in the computer file only.");

        // UI Automation names a policy row "<name> setting" and a folder row by its plain name.
        run.GoTo(Root, Computer, Admin, Oracle);
        checks.Record("folderRowName", run.Rows().Any(r => r.Name == "Basics"), $"rows={string.Join("|", run.Rows().Select(r => r.Name))}.");

        run.Measure("computerStates", machineStates);
        run.Measure("computerComments", machineComments);
        run.Measure("userStates", userStates);
        run.WriteArtifacts("gpedit-list-states.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_List_ExtendedPane()
    {
        var checks = new GpeditChecks("list", "extended");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-list-extended");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-list-extended", sandbox);

        run.GoTo(Root, Computer, Admin, Oracle);
        checks.Record("categoryTitle", ShellRun.WaitUntil(() => run.DescriptionTitle == Oracle), $"title='{run.DescriptionTitle}'.");
        string categoryText = run.DescriptionText("hint");
        checks.Record(
            "categoryText",
            categoryText.StartsWith("Fixture settings used by Better Windows Config", StringComparison.Ordinal),
            $"text='{categoryText}'; the explain text of the category.");
        checks.Record("categoryNoLink", run.EditLink is null, "a category has no edit link.");

        // A folder row shows the description of its category.
        run.SelectRow("Basics");
        checks.Record(
            "folderRow",
            ShellRun.WaitUntil(() => run.DescriptionTitle == "Basics" && run.DescriptionText("hint") == "Class, value forms, value lists, and key placement."),
            $"title='{run.DescriptionTitle}', text='{run.DescriptionText("hint")}'.");

        run.GoTo(Root, Computer, Admin, Oracle, "Elements");
        checks.Record("noExplainText", ShellRun.WaitUntil(() => run.DescriptionTitle == "Elements" && run.DescriptionText("hint") == "Select an item to see its description."), $"title='{run.DescriptionTitle}', text='{run.DescriptionText("hint")}'.");

        // A policy row: title, Requirements (supported on), Description (the help text) and the edit link.
        run.GoTo(Root, Computer, Admin, Oracle, "Basics");
        run.SelectRow("Class machine flag setting");
        checks.Record("policyTitle", ShellRun.WaitUntil(() => run.DescriptionTitle == "Class machine flag"), $"title='{run.DescriptionTitle}'.");
        checks.Record("requirements", run.DescriptionText("requirements") == "BWC oracle fixture: any system", $"requirements='{run.DescriptionText("requirements")}'.");
        checks.Record(
            "description",
            run.DescriptionText("description") == "A machine policy with a value name and no explicit values. Enabled writes 1 and Disabled writes 0.",
            $"description='{run.DescriptionText("description")}'.");
        AutomationElement? link = run.EditLink;
        checks.Record("editLinkShown", link is not null, "the Edit policy setting link is shown for a policy.");
        checks.Record("editLinkName", ShellRun.SafeName(link) == "Edit policy setting", $"link name='{ShellRun.SafeName(link)}'.");

        // The link raises the edit request for G3a: nothing handles it in this version, so the readout counts it.
        link?.Patterns.Invoke.PatternOrDefault?.Invoke();
        checks.Record(
            "editRequest",
            ShellRun.WaitUntil(() => run.Int("Edits") == 1 && run.Str("LastEdit") == "BWC.Policies.Oracle:Pol_Class_Machine"),
            $"Edits={run.Int("Edits")}, LastEdit='{run.Str("LastEdit")}'.");

        // A class Both policy shows its own supported-on text.
        run.SelectRow("Class both flag setting");
        checks.Record("secondPolicy", ShellRun.WaitUntil(() => run.DescriptionTitle == "Class both flag" && run.DescriptionText("requirements") == "BWC oracle fixture: older systems only"), $"title='{run.DescriptionTitle}', requirements='{run.DescriptionText("requirements")}'.");

        run.Measure("title", run.DescriptionTitle);
        run.Measure("requirements", run.DescriptionText("requirements"));
        run.Measure("edits", run.Int("Edits"));
        run.WriteArtifacts("gpedit-list-extended.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_List_StatusBar()
    {
        var checks = new GpeditChecks("list", "status");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-list-status");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-list-status", sandbox);

        checks.Record("root", run.Shell.StatusText().Length == 0, $"status at the root='{run.Shell.StatusText()}'.");
        run.GoTo(Root, Computer);
        checks.Record("configuration", run.Shell.StatusText().Length == 0, $"status on Computer Configuration='{run.Shell.StatusText()}'.");

        run.GoTo(Root, Computer, Admin, Oracle, "Basics");
        checks.Record("basics", ShellRun.WaitUntil(() => run.Shell.StatusText() == "12 setting(s)"), $"status in Basics='{run.Shell.StatusText()}'.");

        run.GoTo(Root, Computer, Admin, Oracle);
        checks.Record("categoryWithoutPolicies", ShellRun.WaitUntil(() => run.Shell.StatusText() == "0 setting(s)"), $"status in BWC Oracle='{run.Shell.StatusText()}'.");

        run.GoTo(Root, User, Admin, Oracle);
        checks.Record("userCategory", ShellRun.WaitUntil(() => run.Shell.StatusText() == "1 setting(s)"), $"status in the user BWC Oracle='{run.Shell.StatusText()}'.");

        run.GoTo(Root, Computer, Admin, "All Settings");
        int shown = run.Int("ShownPolicies");
        checks.Record(
            "allSettings",
            ShellRun.WaitUntil(() => run.Shell.StatusText() == $"{run.Int("ShownPolicies").ToString(CultureInfo.InvariantCulture)} setting(s)" && run.Int("ShownPolicies") > 12),
            $"status in All Settings='{run.Shell.StatusText()}', policy rows={run.Int("ShownPolicies")}.");

        run.Measure("allSettingsRows", run.Int("ShownPolicies"));
        run.Measure("firstReading", shown);
        run.WriteArtifacts("gpedit-list-status.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_List_UnreadablePolicyFile()
    {
        var checks = new GpeditChecks("list", "unreadable");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-list-unreadable");
        sandbox.SeedStandard();
        sandbox.WriteUnreadablePolicyFile(Bwc.Gpo.Store.GpoSide.Machine);
        using GpeditRun run = GpeditRun.Open("gpedit-list-unreadable", sandbox);

        checks.Record("problemRecorded", run.Int("Problems") >= 1 && run.Str("ProblemList").Contains("PolicyFile", StringComparison.Ordinal), $"Problems={run.Int("Problems")}, list={run.Str("ProblemList")}.");

        // The computer states are unknown (blank), the rows are still listed, and the window is alive.
        run.GoTo(Root, Computer, Admin, Oracle, "Basics");
        List<GpeditRow> computer = run.Rows();
        checks.Record("rowsStillListed", computer.Select(r => r.Name).SequenceEqual(ComputerBasics), $"rows={string.Join("|", computer.Select(r => r.Name))}.");
        string[] states = computer.Select(r => r.Cells.ElementAtOrDefault(1) ?? "?").ToArray();
        checks.Record("statesUnknown", states.All(s => s.Length == 0), $"states={string.Join("|", states)}; an unreadable file shows no state.");

        // The user file is fine, so its states are still read.
        run.GoTo(Root, User, Admin, Oracle, "Basics");
        string[] userStates = run.Rows().Select(r => r.Cells.ElementAtOrDefault(1) ?? "?").ToArray();
        checks.Record("userSideRead", userStates.SequenceEqual(["Disabled", "Not configured", "Enabled", "Not configured"]), $"user states={string.Join("|", userStates)}.");

        run.Measure("problems", run.Str("ProblemList"));
        run.Measure("computerStates", states);
        run.WriteArtifacts("gpedit-list-unreadable.png");
        checks.AssertAllPassed();
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 3;
}
