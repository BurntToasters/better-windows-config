using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Tree tests of the Local Group Policy Editor: the shape and order of the console tree over the F1 fixture templates, the
/// placeholder pages, a broken template that must not break the tree, and the real system templates (the one test that also
/// runs on the ship flavor). The failure modes are in docs/failure-modes/gpedit-tree-list.md. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditTreeTests
{
    private const string Computer = "Computer Configuration";
    private const string User = "User Configuration";

    // The expected tree over the fixture, derived by hand from the fixture templates (README of the fixture and the
    // classic probe): per side a category shows when it or a descendant holds a policy of that side; children are sorted
    // by display name; All Settings closes the Administrative Templates node.
    internal const string ExpectedShape = """
        Local Computer Policy
          Computer Configuration
            Software Settings
            Windows Settings
              Name Resolution Policy
              Scripts (Startup/Shutdown)
              Deployed Printers
              Security Settings
              Policy-based QoS
            Administrative Templates
              BWC Oracle
                Basics
                Elements
                  Booleans
                  Choices
                  Lists
                  Numbers
                  Texts
                Explain text
                Machine only
                  Machine nested
                    Machine deep
                Shared keys
                Sort cases
                Tools & Utilities
              All Settings
          User Configuration
            Software Settings
            Windows Settings
              Scripts (Logon/Logoff)
              Deployed Printers
              Security Settings
              Policy-based QoS
            Administrative Templates
              BWC Oracle
                Basics
                Elements
                  Booleans
                  Choices
                  Lists
                  Numbers
                  Texts
                User only
                  User nested
              All Settings
        """;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Tree_Shape()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("tree", "shape");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-tree-shape");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-tree-shape", sandbox, shape: true);

        string shape = run.Shape();
        string expected = ExpectedShape.ReplaceLineEndings("\n");
        checks.Record("shape", shape.ReplaceLineEndings("\n") == expected, $"shape differs.{Environment.NewLine}Got:{Environment.NewLine}{shape}");
        checks.Record("storeIsSandbox", run.Str("Store") == "sandbox", $"Store={run.Str("Store")}; the test runs on the sandbox policy object.");
        checks.Record("noProblems", run.Int("Problems") == 0 && run.Int("Failures") == 0, $"Problems={run.Int("Problems")}, Failures={run.Int("Failures")}, list={run.Str("ProblemList")}.");
        checks.Record("startsOnRoot", run.NodePath().SequenceEqual([GpeditRun.RootName]), $"Node='{run.Str("Node")}'; classic opens on the root.");

        // The root shows the two configuration nodes as folder rows, in one Name column.
        string[] rows = run.Rows().Select(r => r.Name).ToArray();
        checks.Record("rootRows", rows.SequenceEqual([Computer, User]), $"rows={string.Join("|", rows)}.");
        checks.Record("rootStatus", run.Shell.StatusText().Length == 0, $"status='{run.Shell.StatusText()}'.");

        // The tree control shows the root, both configurations and their children.
        string[] items = TreeItemNames(run.Shell);
        checks.Record(
            "visibleTree",
            items.Count(n => n == "Administrative Templates") == 2 && items.Contains(GpeditRun.RootName) && items.Contains(Computer) && items.Contains(User),
            $"tree items={string.Join("|", items)}.");

        run.Measure("shape", shape);
        run.Measure("loadMs", run.Int("LoadMs"));
        run.Measure("templates", run.Int("Templates"));
        run.Measure("categories", run.Int("Categories"));
        run.Measure("policies", run.Int("PolicyCount"));
        run.WriteArtifacts("gpedit-tree-shape.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Tree_PlaceholderPages()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("tree", "placeholders");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-tree-placeholders");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-tree-placeholders", sandbox);

        string[][] placeholders =
        [
            [GpeditRun.RootName, Computer, "Software Settings"],
            [GpeditRun.RootName, Computer, "Windows Settings", "Security Settings"],
            [GpeditRun.RootName, User, "Windows Settings", "Scripts (Logon/Logoff)"],
        ];
        foreach (string[] path in placeholders)
        {
            string leaf = path[^1];
            run.GoTo(path);
            bool page = ShellRun.WaitUntil(() => run.Str("Page") == "placeholder" && run.Shell.Find("GpeditPlaceholderPage") is not null);
            checks.Record($"page.{Slug(leaf)}", page, $"Page={run.Str("Page")} for '{leaf}'.");
            string text = ShellRun.SafeName(run.Shell.Find("GpeditPlaceholderText"));
            checks.Record($"text.{Slug(leaf)}", text.StartsWith(leaf + " is not available", StringComparison.Ordinal), $"text='{text}'.");
            checks.Record($"noList.{Slug(leaf)}", run.Int("Rows") == 0, $"rows on a placeholder page={run.Int("Rows")}.");
            checks.Record($"status.{Slug(leaf)}", run.Shell.StatusText().Length == 0, $"status='{run.Shell.StatusText()}'.");
        }

        run.WriteArtifacts("gpedit-tree-placeholders-page.png");

        // A real node after a placeholder shows the list again.
        run.GoTo(GpeditRun.RootName, Computer, "Administrative Templates", "BWC Oracle");
        checks.Record(
            "listComesBack",
            ShellRun.WaitUntil(() => run.Str("Page") == "list" && run.Shell.Find("GpeditPlaceholderPage") is null && run.Rows().Count == 7),
            $"Page={run.Str("Page")}, rows={run.Rows().Count}.");
        run.Measure("nodeAfterPlaceholders", run.Str("Node"));
        run.WriteArtifacts("gpedit-tree-placeholders.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Tree_BadTemplates()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("tree", "badtemplates");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-tree-bad");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases("MalformedXml", "MissingAdml", "DtdEntity");
        using GpeditRun run = GpeditRun.Open("gpedit-tree-bad", sandbox, shape: true);

        checks.Record("failuresRecorded", run.Int("Failures") == 3, $"Failures={run.Int("Failures")}; problems={run.Str("ProblemList")}.");
        checks.Record(
            "problemsRecorded",
            run.Int("Problems") == 3 && run.Str("ProblemList").Contains("TemplateFailure", StringComparison.Ordinal),
            $"Problems={run.Int("Problems")}, list={run.Str("ProblemList")}.");

        // The good templates still make the same tree: the broken ones add nothing, and nothing threw.
        string shape = run.Shape();
        checks.Record("goodTreeUnchanged", shape.ReplaceLineEndings("\n") == ExpectedShape.ReplaceLineEndings("\n"), $"shape differs.{Environment.NewLine}Got:{Environment.NewLine}{shape}");
        run.GoTo(GpeditRun.RootName, Computer, "Administrative Templates", "BWC Oracle", "Basics");
        checks.Record("basicsStillListed", run.Rows().Count == 12, $"rows in Basics={run.Rows().Count}; expected 12 machine policies.");
        checks.Record("windowAlive", run.Shell.Find(GpeditRun.RootId) is not null, "the window still answers UI Automation.");

        run.Measure("failures", run.Int("Failures"));
        run.Measure("problems", run.Str("ProblemList"));
        run.WriteArtifacts("gpedit-tree-badtemplates.png");
        checks.AssertAllPassed();
    }

    // The real system templates. Read only, and it runs on the ship flavor too: no variable of the seam is set, so the ship
    // editor uses the system template folder and the system policy object, and the E2E flavor falls back to the system
    // template folder and the deny-all store. Only UI Automation is read, because the readout does not exist in the ship
    // flavor.
    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_Tree_SystemTemplates()
    {
        var checks = new GpeditChecks("tree", "system");
        using ShellRun shell = ShellRun.Open("gpedit-tree-system", new Dictionary<string, string>(StringComparer.Ordinal) { ["BWC_E2E_GPEDIT_HOST"] = "editor" });
        shell.PlaceWindow(20, 20, Math.Min(1500, ShellRun.ScreenWidth - 60), 900);

        AutomationElement? admin = shell.Session.Window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(item => ShellRun.SafeName(item) == "Administrative Templates");
        checks.Record("adminNodeShown", admin is not null, "the first Administrative Templates node (Computer Configuration) is in the tree.");
        if (admin is not null)
        {
            admin.Click();
            Thread.Sleep(ShellRun.KeySettle);
            DateTime start = DateTime.UtcNow;
            shell.Tap(ShellRun.VkRight);
            const int boundSeconds = 30;
            bool filled = ShellRun.WaitUntil(
                () =>
                {
                    string[] now = TreeItemNames(shell);
                    return now.Contains("All Settings") && now.Contains("Windows Components");
                },
                TimeSpan.FromSeconds(boundSeconds));
            double seconds = (DateTime.UtcNow - start).TotalSeconds;
            string[] names = TreeItemNames(shell);
            checks.Record("treeFills", filled, $"Windows Components and All Settings under Administrative Templates within {boundSeconds} s (took {seconds:0.0} s); items={names.Length}.");
            checks.Record("manyCategories", names.Length >= 15, $"tree items after the expansion={names.Length}; the system templates have many categories.");
            shell.Measure("secondsToFill", Math.Round(seconds, 1));
            shell.Measure("treeItems", names.Length);
        }

        shell.WriteArtifacts("gpedit-tree-system.png", GpeditRun.Area);
        checks.AssertAllPassed();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static string[] TreeItemNames(ShellRun shell)
    {
        AutomationElement? tree = shell.Find(ShellRun.TreePaneId);
        if (tree is null)
        {
            return [];
        }

        return tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Select(ShellRun.SafeName).ToArray();
    }

    private static string Slug(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());
}
