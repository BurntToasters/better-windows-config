using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bwc.E2E.Harness;
using Bwc.E2E.Hub;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Installer;

/// <summary>
/// The per-machine install, proved with the E2E <c>/MACHINE</c> setup: Program Files, HKLM (64-bit view), the all-users
/// Start menu, the real elevated update helper, and the in-place uninstaller. Each test runs only in an elevated process
/// (the user's elevated runner, docs/elevated-runner.md) and only when <c>scripts/test-installer-e2e.js --prepare</c> wrote
/// artifacts/e2e/installer-inputs.json for this rid and the setups still match it. Otherwise it is Inconclusive (not run).
/// The tests write only the run's own names: %ProgramFiles%\BWC-E2E\run id (and its .new, .old, .upd siblings), the Start
/// menu folder BWC-E2E-run id, HKLM\Software\BWC-E2E\run id, and for the upgrade test a per-user sandbox install.
/// Everything is removed in <see cref="InstallerRun.Dispose"/>. Failure modes: docs/failure-modes/installer-e2e.md, 43-81.
/// Output: artifacts/e2e/installer-machine.json.
/// </summary>
[TestClass]
public sealed class InstallerMachineTests
{
    private const string NotElevated =
        "Not elevated: the machine-mode installer tests run only through the elevated runner (node scripts/elevated-runner/run.js).";

    private const string HubAumid = "BetterWindowsConfig.Hub";
    private const string RegeditAumid = "BetterWindowsConfig.Regedit";
    private const string HubShortcut = "Better Windows Config.lnk";
    private const string RegeditShortcut = "Registry Editor (BWC).lnk";
    private const string TrustedKeyVariable = "BWC_E2E_UPDATE_TRUSTED_KEY";
    private const string ProtectedRootVariable = "BWC_E2E_UPDATE_PROTECTED_ROOT";
    private const string ForceElevatedVariable = "BWC_E2E_UPDATE_FORCE_ELEVATED";
    private const string RecordVariable = "BWC_E2E_INSTALLER_RECORD";
    private const string NoRelaunchVariable = "BWC_E2E_INSTALLER_NO_RELAUNCH";
    private const string StagingFolderName = "BWC-Update";
    private const string PendingRenamesKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string PendingRenamesValue = "PendingFileRenameOperations";

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RegeditSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HubStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex HexFolder = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private static MachineInputs? _inputs;

    [ClassCleanup]
    public static void ClassCleanup()
    {
        MachineWorkRoot.Cleanup();
        MachineChecks.Write(GuiGate.Config, _inputs);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_FreshInstallOfB()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "fresh");
            bool absent = !Directory.Exists(run.InstallDir) && !run.UninstallKeyExists() && !Directory.Exists(run.ShortcutDir);
            MachineChecks.Record("fresh.absentBefore", absent, $"Nothing exists yet for {run.RunId}: folder, HKLM key, Start menu folder.");
            if (absent && InstallMachine(run, inputs.B, "fresh"))
            {
                CheckInstalledFiles(run, inputs.B.Version, "fresh");
                CheckMachineKey(run, inputs.B.Version, "fresh");
                CheckShortcuts(run, "fresh");
                CheckFolderAcl(run, "fresh");
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("fresh.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_UpgradeFromPerUserAToMachineB()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun legacy = InstallerRun.Create(config, "upgl", MachineWorkRoot.Required)
                .UseRecorder((id, pass, notes) => MachineChecks.Record(id, pass, notes));
            using InstallerRun machine = InstallerRun.CreateMachine(config, "upgm");
            if (InstallPerUser(legacy, inputs.A, "upg.legacy"))
            {
                RunUpgrade(config, inputs, legacy, machine);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("upg.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_ElevatedUpdateThroughHelperChain()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "chain");
            if (InstallMachine(run, inputs.A, "chain.a"))
            {
                RunChain(config, inputs, run);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("chain.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_LaunchHubStartsAtMediumIntegrity()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "launch");
            if (InstallMachine(run, inputs.A, "launch"))
            {
                RunLaunchHub(config, run);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("launch.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_CloseRunningClosesElevatedRegedit()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "close");
            if (InstallMachine(run, inputs.A, "close"))
            {
                RunCloseRunning(config, run);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("close.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_UninstallThroughTheUninstallString()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "unin");
            if (InstallMachine(run, inputs.B, "unin"))
            {
                RunUninstall(run, inputs.B);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("unin.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_DowngradeIsRefusedAndSameVersionRepairs()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "order");
            if (InstallMachine(run, inputs.B, "order.b"))
            {
                RunVersionOrder(run, inputs);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("order.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    /// <summary>installer.md M47, installer-e2e.md 90: an uninstaller started with _?= naming another folder stops.</summary>
    [TestMethod]
    [TestCategory("Installer-Machine")]
    [TestCategory("Elevated")]
    [TestCategory("E2EOnly")]
    public void InstallerMachine_UninstallerRefusesAnotherFolder()
    {
        (E2EConfig config, MachineInputs inputs) = RequireReady();
        int start = MachineChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.CreateMachine(config, "locn");
            if (InstallMachine(run, inputs.B, "locn"))
            {
                RunUninstallAnotherFolder(run);
            }
        }
        catch (Exception ex)
        {
            MachineChecks.Record("locn.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Gates and shared steps
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The gates in order: elevated, E2E flavor, and inputs from --prepare that still match. Anything missing is
    /// Inconclusive (not run) with the reason, except the flavor, which fails. The first test past the gates removes the
    /// old installer-machine.json.
    /// </summary>
    private static (E2EConfig Config, MachineInputs Inputs) RequireReady()
    {
        if (!MachineNative.IsCurrentProcessElevated())
        {
            Assert.Inconclusive(NotElevated);
        }

        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        MachineInputs? inputs = MachineInputs.TryRead(config, out string reason);
        if (inputs is null)
        {
            Assert.Inconclusive(reason);
        }

        MachineWorkRoot? work = MachineWorkRoot.TryEnsure(inputs, out string stageReason);
        if (work is null)
        {
            Assert.Inconclusive(stageReason);
        }

        _inputs = inputs;
        MachineChecks.Begin(config);
        return (config, work.Staged);
    }

    /// <summary>
    /// Silent /MACHINE install of one setup. Records the exit code, the Hub exe, and the HKLM DisplayVersion. Returns true
    /// only when the setup ran and the key shows the expected version.
    /// </summary>
    private static bool InstallMachine(InstallerRun run, MachineInputs.SetupFile setup, string prefix, string? legacyHint = null)
    {
        int exitCode = run.RunSetup(setup.Path, SetupTimeout, legacyHint);
        bool hub = File.Exists(run.HubExe);
        bool ran = exitCode == 0 && hub;
        MachineChecks.Record(
            prefix + ".setup",
            ran,
            $"The silent /MACHINE setup {setup.Version} exited {exitCode} (-1 is a timeout); the Hub exe is {(hub ? "present" : "missing")} in {run.InstallDir}.");
        string? displayVersion = run.ReadUninstallValue("DisplayVersion");
        bool same = displayVersion == setup.Version;
        MachineChecks.Record(
            prefix + ".displayVersion",
            same,
            $"HKLM (64-bit view) {run.RunId} shows DisplayVersion '{displayVersion}', expected {setup.Version}.");
        return ran && same;
    }

    private static bool InstallPerUser(InstallerRun run, MachineInputs.SetupFile setup, string prefix)
    {
        int exitCode = run.RunSetup(setup.Path, SetupTimeout);
        bool hub = File.Exists(run.HubExe);
        string? displayVersion = run.ReadUninstallValue("DisplayVersion");
        bool ok = exitCode == 0 && hub && displayVersion == setup.Version;
        MachineChecks.Record(
            prefix + ".setup",
            ok,
            $"The per-user sandbox setup {setup.Version} exited {exitCode}; Hub exe {(hub ? "present" : "missing")}; HKCU key shows '{displayVersion}'.");
        return ok;
    }

    private static void CheckInstalledFiles(InstallerRun run, string version, string prefix)
    {
        string[] required =
        [
            run.HubExe,
            run.RegeditExe,
            run.UpdaterExe,
            run.UninstallerExe,
            Path.Combine(run.InstallDir, "installed-files.txt"),
            Path.Combine(run.InstallDir, "LICENSE"),
            Path.Combine(run.InstallDir, "THIRD-PARTY-NOTICES.txt"),
        ];
        string[] missing = required.Where(path => !File.Exists(path)).Select(Path.GetFileName).ToArray()!;
        MachineChecks.Record(
            prefix + ".files",
            missing.Length == 0,
            missing.Length == 0 ? "The Hub, Registry Editor, updater, uninstaller, manifest, LICENSE, and notices are in " + run.InstallDir + "." : "Missing: " + string.Join(", ", missing));
        string? hubVersion = InstallerRun.VersionOf(run.HubExe);
        MachineChecks.Record(prefix + ".files.version", hubVersion == version, $"The installed Hub exe reports {hubVersion}, expected {version}.");
        MachineChecks.Record(
            prefix + ".files.noStaging",
            !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old") && !Directory.Exists(run.ProtectedDir),
            "No .new, .old, or .upd folder exists next to the install folder after a fresh install.");
    }

    /// <summary>
    /// The HKLM key (64-bit view): the folder, the version, the uninstall strings with _?= so the uninstaller runs in place,
    /// and nothing under WOW6432Node.
    /// </summary>
    private static void CheckMachineKey(InstallerRun run, string version, string prefix)
    {
        string? location = run.ReadUninstallValue("InstallLocation");
        MachineChecks.Record(
            prefix + ".key.location",
            string.Equals(location, run.InstallDir, StringComparison.OrdinalIgnoreCase),
            $"InstallLocation is '{location}', expected '{run.InstallDir}'.");
        string? shown = run.ReadUninstallValue("DisplayVersion");
        MachineChecks.Record(prefix + ".key.version", shown == version, $"DisplayVersion is '{shown}', expected {version}.");

        string? uninstall = run.ReadUninstallValue("UninstallString");
        string? quiet = run.ReadUninstallValue("QuietUninstallString");
        string expectedUninstall = "\"" + run.UninstallerExe + "\" _?=" + run.InstallDir;
        string expectedQuiet = "\"" + run.UninstallerExe + "\" /S _?=" + run.InstallDir;
        MachineChecks.Record(
            prefix + ".key.uninstallString",
            string.Equals(uninstall, expectedUninstall, StringComparison.OrdinalIgnoreCase),
            $"UninstallString is {uninstall}, expected {expectedUninstall}.");
        MachineChecks.Record(
            prefix + ".key.quietUninstallString",
            string.Equals(quiet, expectedQuiet, StringComparison.OrdinalIgnoreCase),
            $"QuietUninstallString is {quiet}, expected {expectedQuiet}.");
        MachineChecks.Record(
            prefix + ".key.inPlace",
            (uninstall?.Contains("_?=", StringComparison.Ordinal) ?? false) && (quiet?.Contains("_?=", StringComparison.Ordinal) ?? false),
            "Both uninstall strings carry _?=, so the uninstaller runs from the install folder and never from a copy in %TEMP%.");
        MachineChecks.Record(
            prefix + ".key.notWow6432",
            !run.Wow6432KeyExists(),
            $"HKLM\\Software\\WOW6432Node\\BWC-E2E\\{run.RunId} must not exist (the setup writes the 64-bit view). If it does, remove it by hand.");
    }

    /// <summary>Two all-users shortcuts with their AUMIDs, and no folder for the run in the user's own Start menu.</summary>
    private static void CheckShortcuts(InstallerRun run, string prefix)
    {
        string hub = Path.Combine(run.ShortcutDir, HubShortcut);
        string regedit = Path.Combine(run.ShortcutDir, RegeditShortcut);
        MachineChecks.Record(
            prefix + ".shortcuts.files",
            File.Exists(hub) && File.Exists(regedit) && run.ShortcutCount() == 2,
            $"The all-users folder {run.ShortcutDir} holds {run.ShortcutCount()} shortcut(s); expected the Hub and Registry Editor shortcuts.");
        MachineChecks.Record(prefix + ".shortcuts.hubAumid", ShortcutAumidIs(hub, HubAumid, out string hubNotes), hubNotes);
        MachineChecks.Record(prefix + ".shortcuts.regeditAumid", ShortcutAumidIs(regedit, RegeditAumid, out string regeditNotes), regeditNotes);
        string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BWC-E2E-" + run.RunId);
        MachineChecks.Record(prefix + ".shortcuts.notPerUser", !Directory.Exists(perUser), $"The current user's Start menu has no folder {perUser}.");
    }

    private static bool ShortcutAumidIs(string shortcut, string expected, out string notes)
    {
        try
        {
            string? actual = MachineNative.ShortcutAppUserModelId(shortcut);
            notes = $"{Path.GetFileName(shortcut)} has AppUserModelID '{actual}', expected '{expected}'.";
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            notes = $"The AppUserModelID of {shortcut} could not be read: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The install folder takes its ACL from Program Files: not protected, with inherited entries, and no allow entry that
    /// lets Users, Authenticated Users, or Everyone write it. The icacls text and the SDDL go into the artifact.
    /// </summary>
    private static void CheckFolderAcl(InstallerRun run, string prefix)
    {
        MachineNative.FolderAcl acl = MachineNative.ReadAcl(run.InstallDir);
        MachineChecks.AddEvidence(prefix + ".icacls", MachineNative.IcaclsText(run.InstallDir));
        MachineChecks.AddEvidence(prefix + ".sddl", acl.Sddl);
        MachineChecks.Record(
            prefix + ".acl.inherited",
            !acl.InheritanceProtected && acl.Entries.Any(entry => entry.Inherited),
            $"The install folder inherits from Program Files: protected={acl.InheritanceProtected}, inherited entries={acl.Entries.Count(entry => entry.Inherited)}.");
        IReadOnlyList<string> writers = MachineNative.UserWriteEntries(acl);
        MachineChecks.Record(
            prefix + ".acl.usersNoWrite",
            writers.Count == 0,
            writers.Count == 0 ? "No allow entry gives Users, Authenticated Users, or Everyone a write right on the folder." : "Write entries: " + string.Join("; ", writers));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Upgrade from a per-user A
    // ------------------------------------------------------------------------------------------------------------------

    private static void RunUpgrade(E2EConfig config, MachineInputs inputs, InstallerRun legacy, InstallerRun machine)
    {
        string before = Snapshot(legacy.InstallDir);
        Process regedit = legacy.StartTracked(legacy.RegeditExe, config.DataRootEnvironment("installer-machine-upgrade"));
        Thread.Sleep(RegeditSettle);
        MachineChecks.Record(
            "upg.legacy.regedit.open",
            !regedit.HasExited,
            $"BwcRegedit.exe from the legacy folder runs (pid {regedit.Id}, integrity 0x{MachineNative.IntegrityRid(regedit.Id):X}) before the machine setup.");

        bool installed = InstallMachine(machine, inputs.B, "upg.machine", legacy.InstallDir);
        string? location = machine.ReadUninstallValue("InstallLocation");
        MachineChecks.Record(
            "upg.machine.target",
            installed && string.Equals(location, machine.InstallDir, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(machine.InstallDir, legacy.InstallDir, StringComparison.OrdinalIgnoreCase),
            $"B went to '{location}' (the machine folder), not to the /D= hint '{legacy.InstallDir}'.");
        MachineChecks.Record(
            "upg.machine.version",
            InstallerRun.VersionOf(machine.HubExe) == inputs.B.Version,
            $"The machine Hub exe reports {InstallerRun.VersionOf(machine.HubExe)}, expected {inputs.B.Version}.");
        MachineChecks.Record(
            "upg.machine.noStaging",
            !Directory.Exists(machine.InstallDir + ".new") && !Directory.Exists(machine.InstallDir + ".old"),
            "The machine run left no .new or .old folder.");

        string after = Directory.Exists(legacy.InstallDir) ? Snapshot(legacy.InstallDir) : "(folder gone)";
        MachineChecks.Record(
            "upg.legacy.untouched",
            string.Equals(before, after, StringComparison.Ordinal),
            string.Equals(before, after, StringComparison.Ordinal)
                ? "The legacy folder has the same files, sizes, and write times after the machine setup."
                : "The legacy folder changed. " + FirstDifference(before, after));
        string? legacyVersion = legacy.ReadUninstallValue("DisplayVersion");
        MachineChecks.Record(
            "upg.legacy.keyKept",
            legacyVersion == inputs.A.Version,
            $"The legacy per-user key still shows '{legacyVersion}' (the machine setup never removes it; the Hub does later).");
        bool closed = regedit.WaitForExit(TimeSpan.FromSeconds(10));
        MachineChecks.Record(
            "upg.legacy.regedit.closed",
            closed,
            closed ? "The setup closed the Registry Editor that ran from the legacy folder." : "The Registry Editor of the legacy folder is still running after the setup.");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Elevated update through the real helper
    // ------------------------------------------------------------------------------------------------------------------

    private static void RunChain(E2EConfig config, MachineInputs inputs, InstallerRun run)
    {
        string scope = "installer-machine-chain";
        string tempRoot = config.TempRootFor(scope);
        string stagingRoot = Path.Combine(tempRoot, StagingFolderName);
        string standIn = UpdateFixture.StandInPath(config, config.Rid);
        MachineChecks.Record(
            "chain.noRootOverride",
            Environment.GetEnvironmentVariable(ProtectedRootVariable) is null,
            ProtectedRootVariable + " is not set in the test process and is removed from the helper's environment, so the real <install dir>.upd and the elevated token are used.");

        using var feed = InstallerFixture.Create(config, File.ReadAllBytes(standIn), "installer.exe", inputs.B.Version, config.Rid);
        string firstCopy = string.Empty;
        for (int pass = 1; pass <= 2; pass++)
        {
            string prefix = "chain.apply" + pass.ToString(CultureInfo.InvariantCulture);
            string staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            feed.WriteStagedUpdate(staging);
            string record = Path.Combine(config.DataRootFor(scope), "record-" + pass.ToString(CultureInfo.InvariantCulture) + ".txt");
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TEMP"] = tempRoot,
                ["TMP"] = tempRoot,
                [TrustedKeyVariable] = feed.PublicKeyBase64,
                [RecordVariable] = record,
                [NoRelaunchVariable] = "1",
                [ProtectedRootVariable] = null,
                [ForceElevatedVariable] = null,
            };

            HelperResult helper = RunHelper(run.UpdaterExe, ["--apply-elevated", staging], environment, HelperTimeout);
            MachineChecks.AddEvidence(prefix + ".helper", $"exit {helper.ExitCode}\n{helper.Stdout}\n{helper.Stderr}");
            bool started = HelperReports(helper, "installerStarted");
            MachineChecks.Record(
                prefix + ".helper",
                helper.ExitCode == 0 && started,
                $"--apply-elevated {staging} exited {helper.ExitCode}; stdout: {helper.Stdout.Trim()}");
            if (!started)
            {
                return;
            }

            Dictionary<string, string>? fields = WaitForRecord(record, RecordTimeout, "relaunchSkipped");
            MachineChecks.Record(prefix + ".record", fields is not null, "The stand-in installer wrote its record " + record + ".");
            if (fields is null)
            {
                return;
            }

            string copyFolder = CheckStartedCopy(run, feed, fields, helper.ProcessId, prefix);
            CheckProtectedAcls(run, copyFolder, prefix);
            if (pass == 1)
            {
                firstCopy = copyFolder;
                PlantStaleFolders(run);
            }
            else
            {
                CheckStaleRemoval(run, firstCopy, copyFolder);
            }
        }
    }

    /// <summary>
    /// What the helper started: the verified copy in &lt;install dir&gt;.upd\&lt;32 hex&gt;, its hash, the exact command line,
    /// an elevated token, and TEMP and TMP inside the copy folder. Returns the copy folder.
    /// </summary>
    private static string CheckStartedCopy(InstallerRun run, InstallerFixture feed, Dictionary<string, string> fields, int helperPid, string prefix)
    {
        string processPath = fields.GetValueOrDefault("processPath", string.Empty);
        string copyFolder = Path.GetDirectoryName(processPath) ?? string.Empty;
        bool shape = string.Equals(Path.GetFileName(processPath), "installer.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(copyFolder), run.ProtectedDir, StringComparison.OrdinalIgnoreCase)
            && HexFolder.IsMatch(Path.GetFileName(copyFolder));
        MachineChecks.Record(
            prefix + ".copy.path",
            shape,
            $"The installer ran from '{processPath}', expected {run.ProtectedDir}\\<32 hex>\\installer.exe.");
        MachineChecks.Record(
            prefix + ".copy.hash",
            string.Equals(fields.GetValueOrDefault("sha256"), feed.InstallerSha256, StringComparison.OrdinalIgnoreCase),
            $"The started file has SHA-256 {fields.GetValueOrDefault("sha256")}, the signed one is {feed.InstallerSha256}.");

        string commandLine = fields.GetValueOrDefault("commandLine", string.Empty);
        string expectedTail = " /S /RELAUNCH /PARENTPID=" + helperPid.ToString(CultureInfo.InvariantCulture) + " /D=" + run.InstallDir;
        MachineChecks.Record(
            prefix + ".copy.commandLine",
            commandLine.EndsWith(expectedTail, StringComparison.OrdinalIgnoreCase),
            $"The command line is {commandLine}; it must end with{expectedTail}.");
        MachineChecks.Record(
            prefix + ".copy.elevated",
            fields.GetValueOrDefault("elevated") == "1" && fields.GetValueOrDefault("env.protectedRoot") == string.Empty,
            "The installer inherited the elevated token (no verb) and saw no E2E protected-root override.");

        string expectedTemp = Path.Combine(copyFolder, "tmp");
        string temp = fields.GetValueOrDefault("env.TEMP", string.Empty);
        string tmp = fields.GetValueOrDefault("env.TMP", string.Empty);
        MachineChecks.Record(
            prefix + ".copy.temp",
            SamePath(temp, expectedTemp) && SamePath(tmp, expectedTemp),
            $"The installer's TEMP is '{temp}' and TMP is '{tmp}'; both must be '{expectedTemp}' inside the copy folder.");
        return copyFolder;
    }

    /// <summary>The root, copy folder, and tmp folder: owner Administrators, protected, SYSTEM and Admins only.</summary>
    private static void CheckProtectedAcls(InstallerRun run, string copyFolder, string prefix)
    {
        foreach ((string label, string folder) in new[] { ("root", run.ProtectedDir), ("copy", copyFolder), ("tmp", Path.Combine(copyFolder, "tmp")) })
        {
            if (!Directory.Exists(folder))
            {
                MachineChecks.Record(prefix + ".acl." + label, false, $"{folder} does not exist.");
                continue;
            }

            MachineNative.FolderAcl acl = MachineNative.ReadAcl(folder);
            MachineChecks.AddEvidence(prefix + ".acl." + label + ".sddl", acl.Sddl);
            if (label == "root")
            {
                MachineChecks.AddEvidence(prefix + ".acl.root.icacls", MachineNative.IcaclsText(folder));
            }

            IReadOnlyList<string> problems = MachineNative.ProtectedAclProblems(acl);
            MachineChecks.Record(
                prefix + ".acl." + label,
                problems.Count == 0,
                problems.Count == 0
                    ? $"{folder}: owner Administrators, DACL protected, only SYSTEM and Administrators with full control."
                    : $"{folder}: " + string.Join("; ", problems));
        }
    }

    private static void PlantStaleFolders(InstallerRun run)
    {
        string stale = Path.Combine(run.ProtectedDir, Guid.NewGuid().ToString("N"));
        string upper = Path.Combine(run.ProtectedDir, Guid.NewGuid().ToString("N").ToUpperInvariant());
        string keep = Path.Combine(run.ProtectedDir, "keepme");
        foreach (string folder in new[] { stale, upper, keep })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "old.bin"), "left by a crashed update");
        }

        MachineChecks.AddEvidence("chain.stale.planted", $"stale hex: {stale}\nupper case hex: {upper}\nother name: {keep}");
    }

    private static void CheckStaleRemoval(InstallerRun run, string firstCopy, string secondCopy)
    {
        string[] names = Directory.GetDirectories(run.ProtectedDir).Select(Path.GetFileName).ToArray()!;
        string[] lowerHex = names.Where(name => HexFolder.IsMatch(name)).ToArray();
        MachineChecks.Record(
            "chain.stale.firstCopyRemoved",
            !Directory.Exists(firstCopy) && !string.Equals(firstCopy, secondCopy, StringComparison.OrdinalIgnoreCase),
            $"The first copy folder {firstCopy} is gone after the second update, and the second ran from {secondCopy}.");
        MachineChecks.Record(
            "chain.stale.hexRemoved",
            lowerHex.Length == 1 && string.Equals(lowerHex[0], Path.GetFileName(secondCopy), StringComparison.Ordinal),
            $"Only the new copy is left among the lower-case 32-hex folders: {string.Join(", ", lowerHex)}.");
        MachineChecks.Record(
            "chain.stale.othersKept",
            names.Contains("keepme", StringComparer.Ordinal) && names.Any(name => name.Length == 32 && name == name.ToUpperInvariant() && !HexFolder.IsMatch(name)),
            "A folder named keepme and an upper-case 32-hex folder were left alone. Folders now: " + string.Join(", ", names));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // --launch-hub and --close-running
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BwcUpdate.exe --launch-hub from this elevated process. The Hub exe of the test install is the stand-in (harmless, no
    /// window, no file), so the real Hub never starts and writes nothing to the user's settings folder. The process the shell
    /// started must be at medium integrity while this process is high.
    /// </summary>
    private static void RunLaunchHub(E2EConfig config, InstallerRun run)
    {
        File.Copy(SelfContainedStandIn(config), run.HubExe, overwrite: true);
        MachineChecks.Record("launch.testIsHigh", MachineNative.CurrentIntegrityRid() >= MachineNative.HighIntegrity, $"This process runs at integrity 0x{MachineNative.CurrentIntegrityRid():X}.");

        DateTime beforeUtc = DateTime.UtcNow.AddSeconds(-1);
        HelperResult helper = RunHelper(run.UpdaterExe, ["--launch-hub"], new Dictionary<string, string?>(StringComparer.Ordinal), HelperTimeout);
        MachineChecks.AddEvidence("launch.helper", $"exit {helper.ExitCode}\n{helper.Stdout}\n{helper.Stderr}");
        MachineChecks.Record(
            "launch.helper",
            helper.ExitCode == 0 && HelperReports(helper, "ok"),
            $"--launch-hub exited {helper.ExitCode} (8 means no desktop shell to start it through); stdout: {helper.Stdout.Trim()}");

        using Process? hub = InstallEvidence.WaitForStartedProcess("BetterWindowsConfig", run.HubExe, 0, beforeUtc, HubStartTimeout);
        MachineChecks.Record("launch.started", hub is not null, hub is null ? "No process started from " + run.HubExe + "." : $"The shell started {run.HubExe} (pid {hub.Id}).");
        if (hub is null)
        {
            return;
        }

        run.Track(Process.GetProcessById(hub.Id));
        int rid = MachineNative.IntegrityRid(hub.Id);
        MachineChecks.Record(
            "launch.medium",
            rid == MachineNative.MediumIntegrity,
            $"The started process runs at integrity 0x{rid:X}; medium is 0x{MachineNative.MediumIntegrity:X}, this test is 0x{MachineNative.CurrentIntegrityRid():X}.");

        // Only the process that was just verified (by image path, started after this call) is stopped.
        hub.Kill();
        MachineChecks.Record("launch.stopped", hub.WaitForExit(TimeSpan.FromSeconds(10)), "The started process was stopped.");
    }

    /// <summary>
    /// An elevated Registry Editor from the machine folder is closed by --close-running (exit 0). Then the helper is kept
    /// alive by a stand-in that never closes, and its own token is read: SeDebugPrivilege must be enabled during the scan.
    /// </summary>
    private static void RunCloseRunning(E2EConfig config, InstallerRun run)
    {
        Process regedit = run.StartTracked(run.RegeditExe, config.DataRootEnvironment("installer-machine-close"));
        Thread.Sleep(RegeditSettle);
        int rid = MachineNative.IntegrityRid(regedit.Id);
        MachineChecks.Record(
            "close.regedit.open",
            !regedit.HasExited && rid >= MachineNative.HighIntegrity,
            $"BwcRegedit.exe from {run.InstallDir} runs elevated (pid {regedit.Id}, integrity 0x{rid:X}).");

        HelperResult closed = RunHelper(
            run.UpdaterExe,
            ["--close-running", "--install-dir", run.InstallDir, "--timeout-seconds", "30"],
            new Dictionary<string, string?>(StringComparer.Ordinal),
            HelperTimeout);
        MachineChecks.AddEvidence("close.helper", $"exit {closed.ExitCode}\n{closed.Stdout}\n{closed.Stderr}");
        MachineChecks.Record("close.helper.exit", closed.ExitCode == 0, $"--close-running exited {closed.ExitCode} (0 none left, 3 still running); stdout: {closed.Stdout.Trim()}");
        MachineChecks.Record("close.regedit.closed", regedit.WaitForExit(TimeSpan.FromSeconds(10)), "The elevated Registry Editor was closed by the helper.");

        // SeDebugPrivilege. The helper prints nothing about it, so its own token is read while it waits for a process that
        // never closes (a stand-in named BwcRegedit.exe in a sub folder). This process never enables the privilege itself.
        (bool testHolds, bool testEnabled) = MachineNative.PrivilegeState(Environment.ProcessId, MachineNative.DebugPrivilege);
        MachineChecks.Record(
            "close.debugPrivilege.testToken",
            !testEnabled,
            $"This token holds {MachineNative.DebugPrivilege}: {testHolds}; enabled in this process: {testEnabled} (it must be off, so a child inherits it off).");
        string keepDir = Path.Combine(run.InstallDir, "keepopen");
        Directory.CreateDirectory(keepDir);
        string keepExe = Path.Combine(keepDir, InstallerRun.RegeditExeName);
        File.Copy(SelfContainedStandIn(config), keepExe, overwrite: true);
        using Process keep = Process.Start(new ProcessStartInfo(keepExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = run.Root })
            ?? throw new InvalidOperationException("The stand-in did not start.");
        run.Track(keep);
        Thread.Sleep(TimeSpan.FromSeconds(1));

        using HelperRun helperRun = HelperRun.Start(
            run.UpdaterExe,
            ["--close-running", "--install-dir", run.InstallDir, "--timeout-seconds", "8"],
            new Dictionary<string, string?>(StringComparer.Ordinal));
        bool held = false;
        bool enabled = false;
        int samples = 0;
        DateTime deadline = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < deadline && !helperRun.HasExited && !enabled)
        {
            (bool processHeld, bool processEnabled) = MachineNative.PrivilegeState(helperRun.ProcessId, MachineNative.DebugPrivilege);
            samples++;
            held |= processHeld;
            enabled = processEnabled;
            Thread.Sleep(50);
        }

        HelperResult waited = helperRun.Finish(HelperTimeout);
        MachineChecks.AddEvidence("close.debugPrivilege.helper", $"exit {waited.ExitCode}\n{waited.Stdout}\n{waited.Stderr}");
        MachineChecks.Record(
            "close.debugPrivilege.enabled",
            !testHolds || enabled,
            testHolds
                ? $"The helper's token showed {MachineNative.DebugPrivilege} enabled during the scan (held={held}, samples={samples})."
                : $"This token does not hold {MachineNative.DebugPrivilege}, so the helper cannot enable it (a group policy); not claimed.");
        MachineChecks.Record(
            "close.debugPrivilege.scan",
            waited.ExitCode == 3 && waited.Stdout.Contains("BwcRegedit pid=" + keep.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            $"The helper exited {waited.ExitCode} (3 expected: the stand-in never closes) and named it: {waited.Stdout.Trim()}");
        keep.Kill();
        _ = keep.WaitForExit(TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Uninstall and version order
    // ------------------------------------------------------------------------------------------------------------------

    private static void RunUninstall(InstallerRun run, MachineInputs.SetupFile setup)
    {
        // A copy folder in .upd, as a finished update leaves it: a 32-hex folder with the installer and its plain "tmp" child
        // (installer.md M50, installer-e2e.md 91).
        string copy = Path.Combine(run.ProtectedDir, Guid.NewGuid().ToString("N"));
        string copyTemp = Path.Combine(copy, "tmp");
        Directory.CreateDirectory(copyTemp);
        File.WriteAllText(Path.Combine(copy, "installer.exe"), "stand-in for a protected copy");
        File.WriteAllText(Path.Combine(copyTemp, "x.dat"), "stand-in for the installer's own temp file");
        MachineChecks.Record(
            "unin.planted",
            File.Exists(Path.Combine(copy, "installer.exe")) && File.Exists(Path.Combine(copyTemp, "x.dat")),
            "A protected copy folder with a tmp child exists in " + run.ProtectedDir + " before the uninstall.");

        string[] pendingBefore = ReadPendingRenames();
        int exitCode = run.UninstallMachine(UninstallTimeout, out string commandLine);
        MachineChecks.AddEvidence("unin.command", commandLine);
        MachineChecks.Record("unin.finished", exitCode != -1, $"{commandLine} exited {exitCode} (-1 is a timeout). The exit code is recorded; the state below decides.");

        MachineChecks.Record("unin.key", !run.UninstallKeyExists() && !KeyExists(@"Software\BWC-E2E\" + run.RunId), "The HKLM key and its BWC-E2E run key are gone.");
        MachineChecks.Record(
            "unin.shortcuts",
            !Directory.Exists(run.ShortcutDir),
            Directory.Exists(run.ShortcutDir) ? $"{run.ShortcutDir} is still there." : "The all-users shortcuts and their folder are gone.");
        string[] payload = [run.HubExe, run.RegeditExe, run.UpdaterExe, Path.Combine(run.InstallDir, "installed-files.txt"), Path.Combine(run.InstallDir, "LICENSE")];
        string[] left = payload.Where(File.Exists).ToArray();
        MachineChecks.Record("unin.payload", left.Length == 0, left.Length == 0 ? "The payload files are gone." : "Still there: " + string.Join(", ", left));
        MachineChecks.Record("unin.upd", !Directory.Exists(run.ProtectedDir), "The protected folder <install dir>.upd is gone, with the tmp child.");
        MachineChecks.Record(
            "unin.noStaging",
            !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
            "No .new or .old folder is left.");

        // The in-place uninstaller cannot delete itself or its folder, so those may be queued for the next restart (M38).
        string[] remaining = Directory.Exists(run.InstallDir)
            ? Directory.EnumerateFileSystemEntries(run.InstallDir).Select(path => Path.GetFileName(path)).ToArray()
            : [];
        bool onlyUninstaller = remaining.All(name => string.Equals(name, InstallerRun.UninstallerName, StringComparison.OrdinalIgnoreCase));
        MachineChecks.Record(
            "unin.leftovers",
            onlyUninstaller,
            remaining.Length == 0 ? "The install folder holds nothing." : "Left in the install folder: " + string.Join(", ", remaining) + " (only Uninstall.exe is documented).");

        string[] added = NewEntries(pendingBefore, ReadPendingRenames());
        string[] undocumented = added.Where(entry => entry.Length > 0 && !entry.Contains(run.InstallDir, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool queued = added.Any(entry => entry.Contains(InstallerRun.UninstallerName, StringComparison.OrdinalIgnoreCase));
        bool uninstallerLeft = remaining.Any(name => string.Equals(name, InstallerRun.UninstallerName, StringComparison.OrdinalIgnoreCase));
        MachineChecks.AddEvidence("unin.pendingRenames", added.Length == 0 ? "(no new entries)" : string.Join("\n", added));
        MachineChecks.Record(
            "unin.rebootPending",
            undocumented.Length == 0 && (!uninstallerLeft || queued),
            undocumented.Length > 0
                ? "Undocumented reboot-pending entries: " + string.Join(", ", undocumented)
                : uninstallerLeft && !queued
                    ? "Uninstall.exe is left and is not queued for deletion at the restart."
                    : $"New PendingFileRenameOperations entries: {added.Count(entry => entry.Length > 0)} (all name this run's folder; documented, installer.md M38).");

        CheckReinstallWhilePending(run, setup);
    }

    /// <summary>
    /// installer.md M51, installer-e2e.md 89: while the restart queue still names this run's Uninstall.exe, a new
    /// install of the
    /// same folder is refused before anything changes (a new Uninstall.exe would be deleted at the restart). When the
    /// uninstaller
    /// removed itself, the queue has no such entry and the check records that it did not apply.
    /// </summary>
    private static void CheckReinstallWhilePending(InstallerRun run, MachineInputs.SetupFile setup)
    {
        string queuedName = @"\??\" + run.UninstallerExe;
        bool queued = ReadPendingRenames().Any(entry => string.Equals(entry, queuedName, StringComparison.OrdinalIgnoreCase));
        if (!queued)
        {
            MachineChecks.Record("unin.reinstallPending", true, "Did not apply: the restart queue does not name " + run.UninstallerExe + " (the uninstaller removed itself).");
            return;
        }

        int exit = run.RunSetup(setup.Path, SetupTimeout);
        bool refused = exit > 0 && !File.Exists(Path.Combine(run.InstallDir, "installed-files.txt")) && !run.UninstallKeyExists();
        MachineChecks.Record(
            "unin.reinstallPending",
            refused,
            $"Installing again while the queue names {run.UninstallerExe} exited {exit} (a refusal is a non-zero exit; -1 is a timeout); installed-files.txt exists: {File.Exists(Path.Combine(run.InstallDir, "installed-files.txt"))}; key exists: {run.UninstallKeyExists()}.");
    }

    private static void RunUninstallAnotherFolder(InstallerRun run)
    {
        // A decoy inside the work folder (admin-only) with a payload name. A wrong run would start it or delete it.
        string decoy = Path.Combine(run.Root, "decoy");
        Directory.CreateDirectory(decoy);
        string decoyFile = Path.Combine(decoy, InstallerRun.UpdaterExeName);
        File.WriteAllText(decoyFile, "not a program");
        string before = Snapshot(run.InstallDir);

        int exit = run.UninstallNamingFolder(decoy, UninstallTimeout, out string command);
        MachineChecks.AddEvidence("locn.command", command);
        MachineChecks.Record("locn.refused", exit > 0, $"{command} exited {exit} (a refusal is a non-zero exit; -1 is a timeout).");
        MachineChecks.Record("locn.decoyKept", File.Exists(decoyFile), "The decoy folder and its payload-named file are untouched.");
        MachineChecks.Record(
            "locn.installKept",
            Directory.Exists(run.InstallDir) && string.Equals(before, Snapshot(run.InstallDir), StringComparison.Ordinal) && run.UninstallKeyExists(),
            "The real install folder has the same files and the HKLM key still exists.");
    }

    private static void RunVersionOrder(InstallerRun run, MachineInputs inputs)
    {
        int olderExit = run.RunSetup(inputs.A.Path, SetupTimeout);
        string? keptKey = run.ReadUninstallValue("DisplayVersion");
        string? keptFile = InstallerRun.VersionOf(run.HubExe);
        MachineChecks.Record("order.older.refused", olderExit > 0, $"Installing {inputs.A.Version} over {inputs.B.Version} exited {olderExit}; a refusal is a non-zero exit (-1 is a timeout).");
        MachineChecks.Record(
            "order.older.untouched",
            keptKey == inputs.B.Version && keptFile == inputs.B.Version,
            $"After the refusal HKLM shows '{keptKey}' and the Hub exe '{keptFile}', expected {inputs.B.Version}.");
        MachineChecks.Record(
            "order.older.noStaging",
            !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
            "The refused setup left no .new or .old folder.");

        int sameExit = run.RunSetup(inputs.B.Path, SetupTimeout);
        string? repairedKey = run.ReadUninstallValue("DisplayVersion");
        string? repairedFile = InstallerRun.VersionOf(run.HubExe);
        MachineChecks.Record(
            "order.same.repair",
            sameExit == 0 && repairedKey == inputs.B.Version && repairedFile == inputs.B.Version,
            $"Installing {inputs.B.Version} over itself exited {sameExit}; key '{repairedKey}', Hub exe '{repairedFile}'.");
        MachineChecks.Record(
            "order.same.noStaging",
            !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
            "The repair left no .new or .old folder.");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The stand-in that runs inside an install folder. Only the self-contained x64 bundle does: the framework-dependent
    /// ARM64 one finds hostfxr.dll of the app beside it and exits 0x80008096. x64 runs on an ARM64 host under emulation.
    /// </summary>
    private static string SelfContainedStandIn(E2EConfig config) => UpdateFixture.StandInPath(config, "win-x64");

    private static void AssertNoFailures(int start)
    {
        IReadOnlyList<string> failures = MachineChecks.FailuresSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            "Machine installer checks failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static bool SamePath(string left, string right) =>
        left.Length > 0 && right.Length > 0
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Every file and folder under the folder with its size and write time, sorted, one per line.</summary>
    private static string Snapshot(string folder) =>
        string.Join(
            "\n",
            Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    bool isFolder = Directory.Exists(path);
                    return $"{Path.GetRelativePath(folder, path)}|{(isFolder ? "dir" : info.Length.ToString(CultureInfo.InvariantCulture))}|{info.LastWriteTimeUtc.Ticks}";
                })
                .OrderBy(line => line, StringComparer.Ordinal));

    private static string FirstDifference(string before, string after)
    {
        HashSet<string> left = [.. before.Split('\n')];
        HashSet<string> right = [.. after.Split('\n')];
        string? changed = left.Except(right).Concat(right.Except(left)).FirstOrDefault();
        return changed is null ? "No single line differs." : "First difference: " + changed;
    }

    private static bool KeyExists(string path)
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = hklm.OpenSubKey(path);
        return key is not null;
    }

    private static string[] ReadPendingRenames()
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = hklm.OpenSubKey(PendingRenamesKey);
        return key?.GetValue(PendingRenamesValue) is string[] values ? values : [];
    }

    /// <summary>The entries of <paramref name="after"/> that are not in <paramref name="before"/>, counting repeats.</summary>
    private static string[] NewEntries(string[] before, string[] after)
    {
        var remaining = new List<string>(before);
        var added = new List<string>();
        foreach (string entry in after)
        {
            int index = remaining.FindIndex(old => string.Equals(old, entry, StringComparison.Ordinal));
            if (index >= 0)
            {
                remaining.RemoveAt(index);
            }
            else
            {
                added.Add(entry);
            }
        }

        return [.. added];
    }

    /// <summary>True when the helper's last stdout line is JSON with the property set to true.</summary>
    private static bool HelperReports(HelperResult helper, string property)
    {
        string? line = helper.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (line is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a record the stand-in installer writes with CreateNew. The file exists before it is complete, so the read
    /// retries until the named last field is there.
    /// </summary>
    private static Dictionary<string, string>? WaitForRecord(string path, TimeSpan timeout, string lastField)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    Dictionary<string, string> fields = InstallEvidence.ReadRecord(path);
                    if (fields.ContainsKey(lastField))
                    {
                        return fields;
                    }
                }
            }
            catch (IOException)
            {
                // The stand-in still holds the file open for writing.
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static HelperResult RunHelper(string exe, string[] arguments, IReadOnlyDictionary<string, string?> environment, TimeSpan timeout)
    {
        using HelperRun run = HelperRun.Start(exe, arguments, environment);
        return run.Finish(timeout);
    }

    private sealed record HelperResult(int ProcessId, int ExitCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>
    /// A helper process started by the test with the system folder as its working directory, like the real chain. Its stdout is
    /// read. The environment changes are applied to the child only; a null value removes the variable. A helper that runs past
    /// the timeout is stopped (it is the process this test started).
    /// </summary>
    private sealed class HelperRun : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource _stdoutEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private HelperRun(Process process)
        {
            _process = process;
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public static HelperRun Start(string exe, string[] arguments, IReadOnlyDictionary<string, string?> environment)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            foreach (KeyValuePair<string, string?> pair in environment)
            {
                if (pair.Value is null)
                {
                    _ = info.Environment.Remove(pair.Key);
                }
                else
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }

            Process process = Process.Start(info) ?? throw new InvalidOperationException("The helper did not start: " + exe);
            var run = new HelperRun(process);
            process.OutputDataReceived += run.OnOutput;
            process.ErrorDataReceived += run.OnError;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return run;
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                _stdoutEnded.TrySetResult();
                return;
            }

            lock (_stdout)
            {
                _ = _stdout.AppendLine(e.Data);
            }
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (_stderr)
                {
                    _ = _stderr.AppendLine(e.Data);
                }
            }
        }

        public HelperResult Finish(TimeSpan timeout)
        {
            bool timedOut = !_process.WaitForExit(timeout);
            if (timedOut)
            {
                _process.Kill(entireProcessTree: true);
                _ = _process.WaitForExit(TimeSpan.FromSeconds(5));
            }

            // A process the helper starts can hold the output pipes open after the helper exits, so the end of the stream is
            // not waited for long. Everything the helper wrote before it exited is already in the pipe.
            _ = _stdoutEnded.Task.Wait(TimeSpan.FromSeconds(5));
            lock (_stdout)
            {
                lock (_stderr)
                {
                    return new HelperResult(_process.Id, timedOut ? -1 : _process.ExitCode, _stdout.ToString(), _stderr.ToString(), timedOut);
                }
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
