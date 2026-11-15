using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Print;

/// <summary>
/// The only print tests that produce output. They bypass the Windows print dialog and print through the E2E guarded
/// path to "Microsoft Print to PDF", with the output file under artifacts/e2e/print/&lt;run-id&gt;.pdf. The printer must
/// exist with the "Microsoft Print To PDF" driver. When it does not, the test records "not run" and passes.
/// </summary>
[TestClass]
public sealed class PrintPdfTests
{
    private const string ProgressTextId = "PrintProgressText";
    private const string CancelId = "DialogSecondary";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Print")]
    [TestCategory("Mutating")]
    public void Print_PdfPrinterWritesRunArtifactThroughGuard()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        if (!RequirePdfPrinter("print.pdf"))
        {
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        string pdfPath = Path.Combine(PrintTestSupport.ArtifactDir(config), config.RunId + ".pdf");
        string resultPath = Path.Combine(PrintTestSupport.ArtifactDir(config), $"pdf-{config.RunId}.txt");

        try
        {
            sandbox.Cleanup();
            PrintTestSupport.SeedPrintTree(sandbox, large: false);
            dataRoot = PrintTestSupport.CreateFreshDataRoot(config, "print-pdf");
            PrintTestSupport.DeleteIfExists(pdfPath);
            PrintTestSupport.DeleteIfExists(resultPath);

            using var session = new AppSession(config, config.RegeditExePath, environment: PdfEnvironment(config, sandbox, dataRoot, pdfPath, resultPath, delayMs: null));
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "print.pdf.navigate");

            ControlTestSupport.SendKeys(session, "CONTROL+KEY_P");
            bool reported = ControlTestSupport.WaitUntil(() => File.Exists(resultPath), TimeSpan.FromSeconds(90));
            Dictionary<string, string> result = reported ? PrintTestSupport.ReadResult(resultPath) : new Dictionary<string, string>();
            bool printed = result.GetValueOrDefault("status") == "Printed" && result.GetValueOrDefault("dcCreated") == "True";
            E2EResults.Record("print.pdf.result", printed, $"result file={reported}, status={Get(result, "status")}, dcCreated={Get(result, "dcCreated")}.");

            bool written = ControlTestSupport.WaitUntil(
                () => File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0,
                TimeSpan.FromSeconds(30));
            bool pdfHeader = written && File.ReadAllBytes(pdfPath).Take(5).SequenceEqual("%PDF-"u8.ToArray());
            E2EResults.Record(
                "print.pdf.file",
                written && pdfHeader,
                $"artifact={pdfPath}, written={written}, pdfHeader={pdfHeader}.");

            E2EResults.Record("print.pdf.enabled", session.IsEnabled, $"the main window stayed enabled: {session.IsEnabled}.");
            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            PrintTestSupport.DeleteDataRoot(dataRoot);
        }
    }

    [TestMethod]
    [TestCategory("Print")]
    [TestCategory("Mutating")]
    public void Print_ProgressCancelStopsJobAndLeavesNoOutput()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        if (!RequirePdfPrinter("print.progress"))
        {
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        string pdfPath = Path.Combine(PrintTestSupport.ArtifactDir(config), config.RunId + "-cancel.pdf");
        string resultPath = Path.Combine(PrintTestSupport.ArtifactDir(config), $"cancel-{config.RunId}.txt");

        try
        {
            sandbox.Cleanup();
            PrintTestSupport.SeedPrintTree(sandbox, large: true);
            dataRoot = PrintTestSupport.CreateFreshDataRoot(config, "print-cancel");
            PrintTestSupport.DeleteIfExists(pdfPath);
            PrintTestSupport.DeleteIfExists(resultPath);
            int jobsBefore = PrintTestSupport.JobCount(PrintTestSupport.PdfPrinterName);

            // The line delay gives the test time to press Cancel while the walk is still running.
            using var session = new AppSession(config, config.RegeditExePath, environment: PdfEnvironment(config, sandbox, dataRoot, pdfPath, resultPath, delayMs: 40));
            using var driver = new EditingDriver(session);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "print.progress.navigate");

            ControlTestSupport.SendKeys(session, "CONTROL+KEY_P");
            AutomationElement? progressText = driver.WaitForElement(ProgressTextId, TimeSpan.FromSeconds(15));
            bool owned = progressText is not null && !session.IsEnabled;
            E2EResults.Record(
                "print.progress.shown",
                progressText is not null && owned,
                $"progress text present={progressText is not null}, main window disabled while it runs={owned}.");

            bool cancelled = progressText is not null && driver.Invoke(CancelId);
            bool reported = ControlTestSupport.WaitUntil(() => File.Exists(resultPath), TimeSpan.FromSeconds(30));
            Dictionary<string, string> result = reported ? PrintTestSupport.ReadResult(resultPath) : new Dictionary<string, string>();
            E2EResults.Record(
                "print.progress.cancelled",
                cancelled && result.GetValueOrDefault("status") == "Cancelled",
                $"cancel clicked={cancelled}, status={Get(result, "status")}.");

            // A cancelled job never writes the trailer, so a complete PDF is one that ends with %%EOF.
            bool completePdf = File.Exists(pdfPath) &&
                System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdfPath)).Contains("%%EOF", StringComparison.Ordinal);
            E2EResults.Record("print.progress.no-output", !completePdf, $"no complete PDF at {pdfPath}: {!completePdf}.");

            int jobsAfter = PrintTestSupport.JobCount(PrintTestSupport.PdfPrinterName);
            E2EResults.Record(
                "print.progress.no-spooler-job",
                jobsAfter <= jobsBefore,
                $"PDF printer jobs before={jobsBefore}, after={jobsAfter}.");

            bool reenabled = ControlTestSupport.WaitUntil(() => session.IsEnabled, TimeSpan.FromSeconds(10));
            E2EResults.Record("print.progress.reenabled", reenabled, $"the main window is enabled again: {reenabled}.");
            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            PrintTestSupport.DeleteDataRoot(dataRoot);
        }
    }

    private static bool RequirePdfPrinter(string checkId)
    {
        PrintTestSupport.PdfPrinterState state = PrintTestSupport.ProbePdfPrinter();
        if (state.Usable)
        {
            return true;
        }

        E2EResults.Record(
            checkId + ".not-run",
            true,
            $"not run: '{PrintTestSupport.PdfPrinterName}' installed={state.Installed}, driver='{state.Driver}'.");
        return false;
    }

    private static Dictionary<string, string> PdfEnvironment(
        E2EConfig config,
        SandboxRegistry sandbox,
        string dataRoot,
        string pdfPath,
        string resultPath,
        int? delayMs)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            [PrintTestSupport.ArtifactsDirVariable] = PrintTestSupport.RepoArtifactsDir(config),
            [PrintTestSupport.PdfPathVariable] = pdfPath,
            [PrintTestSupport.ResultPathVariable] = resultPath,
        };
        if (delayMs is int delay)
        {
            environment[PrintTestSupport.LineDelayVariable] = delay.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return environment;
    }

    private static string Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : "(none)";

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Print PDF checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
