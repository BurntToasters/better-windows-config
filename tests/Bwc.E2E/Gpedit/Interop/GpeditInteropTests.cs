using System.Diagnostics;
using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.Installer;
using Bwc.Policy.Tests.Golden;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Gpedit.Interop;

/// <summary>
/// The four elevated interop tests between BWC and the classic Local Group Policy Editor (piece A2,
/// docs/parity/gpedit-audit.md rows POL-068 to POL-072 and BEH-027). Each test starts the ship
/// editor from publish/&lt;rid&gt;, the real product on the real policy folder, inside the elevated
/// runner. The administrator marker %ProgramData%\BwcElevatedRunner\real-save.marker is the opt-in
/// for a real save. Tests 1 to 3 back up the real policy files and the fixture templates in
/// PolicyDefinitions first and restore them byte for byte in finally, on every path. Test 4 only
/// starts the editor and closes it through the updater. Failure modes:
/// docs/failure-modes/e2e-harness.md, section 49.
/// </summary>
[TestClass]
public sealed class GpeditInteropTests
{
    private const string NotElevated =
        "Not elevated: the Local Group Policy Editor interop tests run only through the elevated runner (node scripts/elevated-runner/run.js).";

    private const string MarkerMissing =
        "The administrator marker is missing, so the real policy save is not authorized. An administrator runs: " +
        "powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\elevated-runner\\install.ps1 -Marker";

    private static readonly TimeSpan AppliedSettle = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AppliedLate = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(120);

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BwcElevatedRunner",
        "real-save.marker");

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void GpeditInterop_BwcWrites_ClassicReads()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RequireReady();
        var checks = new GpeditChecks("interop", "bwc-writes-classic-reads");
        RealPolicyFolder? policy = null;
        RealTemplates? templates = null;
        FixtureRegistry? registry = null;
        try
        {
            templates = RealTemplates.Install();
            policy = RealPolicyFolder.Capture();
            registry = FixtureRegistry.Capture();
            using GpeditRun run = GpeditRun.OpenShip("gpedit-interop-bwc-writes-classic-reads", InteropShip.EditorExe);
            bool located = InteropShip.SelectCategory(run);
            checks.Record("bwc.category", located, "The ship editor shows Computer Configuration > Administrative Templates > BWC Oracle > Basics.");
            if (located)
            {
                OpenAndSet(run, checks, "bwc.enable", GpeditDialog.Enabled, "Enabled");
                checks.Note("bwc.applied.enabled", "After OK, to let the save settle: " + WaitForModeNumber(5).Describe());

                string enabled = ReadClassicState();
                checks.Record("classic.enabled.state", enabled == "Enabled", "Classic shows the state '" + enabled + "', expected Enabled.");

                OpenAndSet(run, checks, "bwc.notConfigured", GpeditDialog.NotConfigured, "Not Configured");
                checks.Note("bwc.applied.removed", "After Not Configured, to let the save settle: " + WaitForModeNumber(null).Describe());

                string notConfigured = ReadClassicState();
                checks.Record("classic.notConfigured.state", notConfigured == "Not Configured", "Classic shows the state '" + notConfigured + "', expected Not Configured.");
            }
        }
        finally
        {
            RecordCleanup(checks, registry, policy, templates);
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void GpeditInterop_ClassicWrites_BwcReadsByFileCopy()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RequireReady();
        var checks = new GpeditChecks("interop", "classic-writes-bwc-reads");
        RealPolicyFolder? policy = null;
        RealTemplates? templates = null;
        FixtureRegistry? registry = null;
        try
        {
            templates = RealTemplates.Install();
            policy = RealPolicyFolder.Capture();
            registry = FixtureRegistry.Capture();
            (GoldenCase capture, GoldenStep step, string expectedState, JsonArray options) = GoldenInterop.LoadStep();
            policy.WriteGoldenStep(step);
            checks.Record("golden.copied", policy.MatchesGoldenStep(step), "Golden " + capture.Id + " step " + step.Number + " (" + step.Label + ") is in the real policy folder.");

            using GpeditRun run = GpeditRun.OpenShip("gpedit-interop-classic-writes-bwc-reads", InteropShip.EditorExe);
            bool located = InteropShip.SelectCategory(run);
            checks.Record("bwc.category", located, "The ship editor shows Computer Configuration > Administrative Templates > BWC Oracle > Basics.");
            if (located)
            {
                using GpeditDialog? dialog = GpeditDialog.Open(run, InteropShip.FixturePolicy);
                checks.Record("bwc.dialog", dialog is not null, "The setting dialog of '" + InteropShip.FixturePolicy + "' opens on the golden files.");
                if (dialog is not null)
                {
                    string state = InteropShip.DialogState(dialog);
                    checks.Record("bwc.state", state == expectedState, "BWC shows '" + state + "', the golden before.state is '" + expectedState + "'.");
                    bool optionsMatch = InteropShip.OptionsMatch(dialog, options);
                    checks.Record("bwc.options", optionsMatch, "BWC shows the golden before.options (" + options.Count + " recorded option(s)).");
                    bool cancelled = dialog.Invoke(GpeditDialog.Cancel) && InteropShip.WaitClosed(dialog, TimeSpan.FromSeconds(10));
                    checks.Record("bwc.cancel", cancelled, "Cancel closes the dialog and the test pressed no other button.");
                    checks.Record("bwc.cancel.noWrite", policy.MatchesGoldenStep(step), "Cancel left the five policy files as the golden step wrote them.");
                }
            }
        }
        finally
        {
            RecordCleanup(checks, registry, policy, templates);
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void GpeditInterop_RealSaveWithRefresh()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RequireReady();
        var checks = new GpeditChecks("interop", "real-save-with-refresh");
        RealPolicyFolder? policy = null;
        RealTemplates? templates = null;
        FixtureRegistry? registry = null;
        try
        {
            templates = RealTemplates.Install();
            policy = RealPolicyFolder.Capture();
            registry = FixtureRegistry.Capture();
            using GpeditRun run = GpeditRun.OpenShip("gpedit-interop-real-save-with-refresh", InteropShip.EditorExe);
            bool located = InteropShip.SelectCategory(run);
            checks.Record("bwc.category", located, "The ship editor shows Computer Configuration > Administrative Templates > BWC Oracle > Basics.");
            if (located)
            {
                OpenAndSet(run, checks, "bwc.enable", GpeditDialog.Enabled, "Enabled");
                ModeResult applied = WaitForModeNumber(5);
                checks.Record("registry.appeared", applied.Matches, "The refresh applies ModeNumber after OK: " + applied.Describe());
                checks.Record("registry.appeared.kind", applied.LateKind == RegistryValueKind.DWord, "ModeNumber is a " + applied.LateKind + ", expected DWord.");

                OpenAndSet(run, checks, "bwc.notConfigured", GpeditDialog.NotConfigured, "Not Configured");
                ModeResult removed = WaitForModeNumber(null);
                checks.Record("registry.removed", removed.Matches, "The refresh removes ModeNumber after Not Configured: " + removed.Describe());
            }
        }
        finally
        {
            RecordCleanup(checks, registry, policy, templates);
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void GpeditInterop_UpdateClosesElevatedEditor()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RequireReady();
        var checks = new GpeditChecks("interop", "update-closes-elevated-editor");
        using ShellRun shell = ShellRun.Open("gpedit-interop-update-closes-elevated-editor", exePath: InteropShip.EditorExe);
        int pid = shell.Session.ProcessId;
        using Process editor = Process.GetProcessById(pid);
        int integrity = MachineNative.IntegrityRid(pid);
        checks.Record("editor.elevated", integrity >= MachineNative.HighIntegrity, $"BwcGpedit.exe pid {pid} runs at integrity 0x{integrity:X}, high is expected.");

        HelperResult helper = RunHelper(
            InteropShip.UpdaterExe,
            ["--close-running", "--install-dir", InteropShip.PublishDir(), "--timeout-seconds", "30"]);
        checks.Record("helper.exit", helper.ExitCode == 0, $"BwcUpdate.exe --close-running exited {helper.ExitCode}; stdout: {helper.Stdout.Trim()}");
        checks.Record("editor.closed", editor.WaitForExit(TimeSpan.FromSeconds(10)), "The elevated BwcGpedit.exe was closed by the helper.");

        checks.AssertAllPassed();
    }

    /// <summary>
    /// The gates after the E2EOnly flavor guard: elevation with the elevated runner command, then the
    /// administrator marker without which a real policy save is not authorized.
    /// </summary>
    private static void RequireReady()
    {
        if (!MachineNative.IsCurrentProcessElevated())
        {
            Assert.Inconclusive(NotElevated);
        }

        if (!File.Exists(MarkerPath))
        {
            Assert.Inconclusive(MarkerMissing);
        }
    }

    /// <summary>Opens the fixture policy, selects a state, presses OK, and waits for the dialog to close.</summary>
    private static void OpenAndSet(GpeditRun run, GpeditChecks checks, string prefix, string radioId, string expectedState)
    {
        using GpeditDialog? dialog = GpeditDialog.Open(run, InteropShip.FixturePolicy);
        checks.Record(prefix + ".dialog", dialog is not null, "The setting dialog of '" + InteropShip.FixturePolicy + "' opens.");
        if (dialog is null)
        {
            return;
        }

        string before = InteropShip.DialogState(dialog);
        checks.Record(prefix + ".before", before != "Unknown", "The dialog opens with the state '" + before + "'.");
        bool selected = dialog.Select(radioId);
        string after = InteropShip.DialogState(dialog);
        checks.Record(prefix + ".selected", selected && after == expectedState, "The radio '" + expectedState + "' is selected; the dialog reads '" + after + "'.");
        bool ok = dialog.Invoke(GpeditDialog.Ok);
        bool closed = ok && InteropShip.WaitClosed(dialog, TimeSpan.FromSeconds(30));
        checks.Record(prefix + ".ok", closed, "OK applies the state and closes the dialog.");
    }

    /// <summary>Starts classic, opens the fixture policy, and returns the state its dialog shows.</summary>
    private static string ReadClassicState()
    {
        using var classic = ClassicGpedit.Start();
        return classic.ReadPolicyState(InteropShip.FixturePolicy, InteropShip.CategoryPath);
    }

    /// <summary>
    /// Polls the live registry for up to the settle, then reads once more after the late wait, as the
    /// oracle matrix does (docs/failure-modes/oracle-gpedit-matrix.md, sections 6 and 7).
    /// </summary>
    private static ModeResult WaitForModeNumber(int? expected)
    {
        bool settled = ShellRun.WaitUntil(() => FixtureRegistry.Read(out _) == expected, AppliedSettle);
        Thread.Sleep(AppliedLate);
        int? late = FixtureRegistry.Read(out RegistryValueKind kind);
        return new ModeResult(settled, late, kind, expected, late == expected);
    }

    /// <summary>Restores the registry value, the real policy files and the templates, and records each result.</summary>
    private static void RecordCleanup(GpeditChecks checks, FixtureRegistry? registry, RealPolicyFolder? policy, RealTemplates? templates)
    {
        IReadOnlyList<string> registryProblems = Problems(registry is null ? null : registry.Restore, "the registry was not captured.");
        checks.Record("cleanup.registry", registryProblems.Count == 0, registryProblems.Count == 0 ? "The fixture registry value is back." : string.Join("; ", registryProblems));

        IReadOnlyList<string> policyProblems = Problems(policy is null ? null : policy.Restore, "the policy folder was not captured.");
        checks.Record("cleanup.policyFiles", policyProblems.Count == 0, policyProblems.Count == 0 ? "The five real policy files are back byte for byte." : string.Join("; ", policyProblems));

        IReadOnlyList<string> templateProblems = Problems(templates is null ? null : templates.Restore, "the templates were not installed.");
        checks.Record("cleanup.templates", templateProblems.Count == 0, templateProblems.Count == 0 ? "The PolicyDefinitions files are back byte for byte." : string.Join("; ", templateProblems));
    }

    /// <summary>Runs one restore; a cleanup that throws becomes a problem, never an exception out of finally.</summary>
    private static IReadOnlyList<string> Problems(Func<IReadOnlyList<string>>? restore, string missing)
    {
        if (restore is null)
        {
            return [missing];
        }

        try
        {
            return restore();
        }
        catch (Exception ex)
        {
            return [ex.GetType().Name + ": " + ex.Message];
        }
    }

    /// <summary>Runs BwcUpdate.exe with its own folder as the working directory and captures its output.</summary>
    private static HelperResult RunHelper(string exe, string[] arguments)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The helper did not start: " + exe);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(HelperTimeout);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(TimeSpan.FromSeconds(5));
        }

        _ = Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(5));
        return new HelperResult(
            exited ? process.ExitCode : -1,
            stdout.IsCompleted ? stdout.Result : string.Empty,
            stderr.IsCompleted ? stderr.Result : string.Empty);
    }

    private sealed record ModeResult(bool Settled, int? Late, RegistryValueKind LateKind, int? Expected, bool LateMatches)
    {
        internal bool Matches => Settled || LateMatches;

        internal string Describe() =>
            $"ModeNumber is {(Late is null ? "absent" : Late.ToString())} ({LateKind}), expected {(Expected is null ? "absent" : Expected.ToString())}; " +
            $"settled={Settled}, lateMatches={LateMatches}.";
    }

    private sealed record HelperResult(int ExitCode, string Stdout, string Stderr);
}
