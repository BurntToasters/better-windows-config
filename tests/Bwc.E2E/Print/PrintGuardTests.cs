using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Print;

/// <summary>
/// The E2E print guard. A guarded job refuses every device except "Microsoft Print to PDF" with an output file under
/// the artifacts folder. The probe runs the guarded path with the default printer's name and the physical printers'
/// names. Each refusal must come before any device context is created, and no dialog may open.
/// </summary>
[TestClass]
public sealed class PrintGuardTests
{
    private const string RefusalText = "print refused by E2E guard";

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
    public void Print_E2EGuardRefusesEveryPrinterBeforeAnyDeviceContext()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        string resultPath = Path.Combine(PrintTestSupport.ArtifactDir(config), $"guard-{config.RunId}.txt");
        string outsidePdf = Path.Combine(Path.GetTempPath(), $"bwc-guard-{config.RunId}.pdf");

        try
        {
            sandbox.Cleanup();
            PrintTestSupport.SeedPrintTree(sandbox, large: false);
            dataRoot = PrintTestSupport.CreateFreshDataRoot(config, "print-guard");
            PrintTestSupport.DeleteIfExists(resultPath);

            string? defaultPrinter = PrintTestSupport.DefaultPrinterName();
            var probes = new List<string>();
            if (!string.IsNullOrWhiteSpace(defaultPrinter))
            {
                probes.Add(defaultPrinter);
            }

            probes.AddRange(PrintTestSupport.PhysicalPrinters);
            probes.Add(PrintTestSupport.PdfPrinterName);
            probes.Add(PrintTestSupport.PdfPrinterName + "|" + outsidePdf);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                [PrintTestSupport.ArtifactsDirVariable] = PrintTestSupport.RepoArtifactsDir(config),
                [PrintTestSupport.GuardProbeVariable] = string.Join(';', probes),
                [PrintTestSupport.ResultPathVariable] = resultPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "print.guard.navigate");

            // Ctrl+P runs the probe. The probe never shows the Windows print dialog.
            ControlTestSupport.SendKeys(session, "CONTROL+KEY_P");
            bool reported = ControlTestSupport.WaitUntil(() => File.Exists(resultPath), TimeSpan.FromSeconds(30));
            E2EResults.Record("print.guard.reported", reported, $"result file written: {reported}.");

            if (reported)
            {
                CheckProbeLines(resultPath, probes, defaultPrinter);
            }

            E2EResults.Record(
                "print.guard.outside-file",
                !File.Exists(outsidePdf),
                $"the refused PDF path was not created: {!File.Exists(outsidePdf)}.");
            E2EResults.Record(
                "print.guard.enabled",
                session.IsEnabled,
                $"the main window stayed enabled, so no print dialog opened: {session.IsEnabled}.");

            FailIfNeeded();
        }
        finally
        {
            PrintTestSupport.DeleteIfExists(outsidePdf);
            ControlTestSupport.CleanupRegistry(config);
            PrintTestSupport.DeleteDataRoot(dataRoot);
        }
    }

    // Every probe line must be a Failed status with dcCreated False and the guard's refusal text. The default
    // printer's line is checked on its own, because it is the printer the tests must never reach.
    private static void CheckProbeLines(string resultPath, List<string> probes, string? defaultPrinter)
    {
        string[] lines = File.ReadAllLines(resultPath).Where(line => line.Length > 0).ToArray();
        bool countMatches = lines.Length == probes.Count;

        bool allRefused = lines.All(line =>
        {
            string[] fields = line.Split('\t');
            return fields.Length >= 4 &&
                fields[1] == "Failed" &&
                fields[2] == "False" &&
                fields[3].Contains(RefusalText, StringComparison.Ordinal);
        });
        E2EResults.Record(
            "print.guard.refused",
            countMatches && allRefused,
            $"probes={probes.Count}, lines={lines.Length}, all refused before any device context: {allRefused}.");

        if (string.IsNullOrWhiteSpace(defaultPrinter))
        {
            E2EResults.Note("print.guard.default-printer", "No default printer is set. The physical printers were probed.");
            return;
        }

        bool defaultRefused = lines.Any(line =>
        {
            string[] fields = line.Split('\t');
            return fields.Length >= 4 &&
                string.Equals(fields[0], defaultPrinter, StringComparison.Ordinal) &&
                fields[1] == "Failed" &&
                fields[2] == "False" &&
                fields[3].Contains(RefusalText, StringComparison.Ordinal);
        });
        E2EResults.Record(
            "print.guard.default-printer",
            defaultRefused,
            $"default printer '{defaultPrinter}' refused before any device context: {defaultRefused}.");
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Print guard checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
