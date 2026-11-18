using System.Diagnostics;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.Hub;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Installer;

/// <summary>
/// The real in-app update. A silent install of version A runs in a temp folder. The installed Hub checks a loopback
/// feed that offers B, Download and Install is pressed with the mouse, and the helper runs the real B installer, which
/// replaces the files and starts the new Hub. Driven by scripts/test-installer-e2e.js, which builds A and B and reads
/// artifacts/e2e/installer-update.json. Without that driver the tests are Inconclusive.
/// </summary>
[TestClass]
public sealed class InstallerUpdateTests
{
    private const string HubTitle = "Better Windows Config";
    private const string AvailableTitle = "Update Available";
    private const string RefusalTitle = "Check for Updates";
    private const string RegeditName = "Registry Editor";
    private const string GpeditName = "Local Group Policy Editor";
    private const string CheckMenuId = "MenuItem_CheckForUpdates";
    private const string InstallButtonId = "UpdateInstall";
    private const string HubProcessName = "BetterWindowsConfig";
    private const string ManifestVariable = "BWC_E2E_UPDATE_MANIFEST_URL";
    private const string TrustedKeyVariable = "BWC_E2E_UPDATE_TRUSTED_KEY";
    private const string AllowHttpVariable = "BWC_E2E_UPDATE_ALLOW_HTTP";
    private const string ProtectedRootVariable = "BWC_E2E_UPDATE_PROTECTED_ROOT";
    private const string InstallerRunIdVariable = "BWC_E2E_INSTALLER_RUNID";
    private const string InstallerShortcutsVariable = "BWC_E2E_INSTALLER_SHORTCUTS";
    private const string SettingsJson = "{\n  \"CheckForUpdatesOnStartup\": false,\n  \"SettingsVersion\": 2\n}";
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RelaunchTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RegeditSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InstallStartTimeout = TimeSpan.FromSeconds(30);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;

        // Window rectangles and mouse presses are only exact in a per-monitor DPI aware process.
        ControlTestSupport.PrepareDpi();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is not null)
        {
            InstallerChecks.Write(inputs.ResultPath, GuiGate.Config.Rid, inputs);
        }
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Update_HubInstallsRealNewerInstaller()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUpdateCase(inputs, "update", holdRegedit: false);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Update_ClosesOpenRegeditAndInstalls()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUpdateCase(inputs, "regedit", holdRegedit: true);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_SameVersion_RepairsAndOlderIsRefused()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunVersionOrderCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_MachineMode_UnelevatedAbortsWithoutChanges()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunMachineAbortCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Uninstall_RemovesFilesShortcutsAndKey()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUninstallCase(inputs);
    }

    private static void RunUpdateCase(InstallerInputs inputs, string caseId, bool holdRegedit)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);
        RequireFile(inputs.InstallerB);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        string installerStaging = InstallEvidence.StagingRoot(config, "installer-" + caseId);
        HashSet<string> stagingBefore = InstallEvidence.StagingFolders(installerStaging);
        try
        {
            using InstallerRun run = InstallerRun.Create(config, caseId);
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, caseId + ".a"))
            {
                RunUpdateFromHub(config, run, inputs, caseId, holdRegedit);
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record(caseId + ".exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallEvidence.RemoveStagingSince(installerStaging, stagingBefore);
            InstallerChecks.Duration(caseId, stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// Presses Check for Updates, Download and Install in the Hub, then checks what the update did: the Hub closed,
    /// the feed redirect was followed with the query intact, the installer ran (its uninstall key shows B), the new Hub
    /// started from the install folder, and no staging folder is left.
    /// </summary>
    private static void RunUpdateFromHub(
        E2EConfig config,
        InstallerRun run,
        InstallerInputs inputs,
        string caseId,
        bool holdRegedit)
    {
        if (holdRegedit)
        {
            _ = StartRegedit(config, run, caseId);
            _ = StartGpedit(config, run, caseId);
        }
        string installerName = Path.GetFileName(inputs.InstallerB);
        byte[] installerBytes = File.ReadAllBytes(inputs.InstallerB);
        using var server = new RedirectFeedServer(installerBytes, installerName);
        using var feed = InstallerFixture.Create(config, installerBytes, installerName, inputs.VersionB, config.Rid);
        (byte[] manifest, byte[] signature) = feed.BuildManifest(server.InstallerUrl);
        server.Offer(manifest, signature);

        using var hub = new AppSession(config, run.HubExe, environment: HubEnvironment(config, caseId, run, server, feed));
        int hubPid = hub.ProcessId;
        using Process hubProcess = Process.GetProcessById(hubPid);
        InstallEvidence.CacheHandle(hubProcess);
        using var automation = new UIA3Automation();

        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? item = HubUi.FindInProcess(automation, hubPid, CheckMenuId, ElementTimeout);
        HubUi.ClickMenuItem(item);
        AutomationElement? available = HubUi.FindWindowInProcess(automation, hubPid, AvailableTitle, DialogTimeout);
        InstallerChecks.Record(caseId + ".hub.available", available is not null, "The Update Available window shows " + inputs.VersionB + ".");
        if (available is null)
        {
            return;
        }

        AutomationElement? install = HubUi.WaitWithin(available, InstallButtonId, DialogTimeout);
        InstallerChecks.Record(caseId + ".hub.installButton", install is not null, "Download and Install is shown.");
        if (install is null)
        {
            return;
        }

        DateTime pressedUtc = DateTime.UtcNow;
        bool pressed = HubUi.ClickDialogButton(install, available);
        InstallerChecks.Record(caseId + ".hub.press", pressed, "Download and Install was pressed with the mouse.");
        if (!pressed)
        {
            return;
        }

        // With BwcRegedit open the Hub refuses before the helper starts, so the installer's close step never runs.
        if (holdRegedit)
        {
            CheckRegeditRefusal(automation, hubPid, caseId);
            return;
        }

        // The press worked only when the Hub logs the install start. A press that did nothing is retried through
        // UI Automation, while the dialog is still there, and the method is recorded.
        string hubData = config.DataRootFor("installer-" + caseId);
        bool mouseWorked = WaitForHubLog(hubData, InstallEvidence.HubLogLine, InstallStartTimeout);
        if (!mouseWorked && HubUi.FindWindowInProcess(automation, hubPid, AvailableTitle, TimeSpan.FromSeconds(1)) is not null)
        {
            HubUi.InvokeButton(install);
            mouseWorked = WaitForHubLog(hubData, InstallEvidence.HubLogLine, InstallStartTimeout);
        }

        string shotDir = Path.Combine(config.ArtifactsDir, "installer");
        Directory.CreateDirectory(shotDir);
        string shot;
        try
        {
            shot = HubUi.CaptureWindow(hub.MainHwnd, Path.Combine(shotDir, caseId + "-after-press.png"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            shot = "not captured: " + ex.Message;
        }

        bool stillOpen = HubUi.FindWindowInProcess(automation, hubPid, AvailableTitle, TimeSpan.FromSeconds(1)) is not null;
        InstallerChecks.Record(
            caseId + ".hub.pressMethod",
            mouseWorked,
            $"The Hub logged the install start after Download and Install (dialog still open: {stillOpen}). Screen: {shot}");

        bool hubExited = hubProcess.WaitForExit(InstallTimeout);
        InstallerChecks.Record(caseId + ".hub.exited", hubExited, "The Hub closed so the helper could run the installer.");

        InstallerChecks.Record(
            caseId + ".feed.redirected",
            server.Redirects > 0,
            $"The installer URL answered with {server.Redirects} redirect(s) to the second loopback path.");
        InstallerChecks.Record(
            caseId + ".feed.queryIntact",
            server.QueryIntact,
            $"The redirected request came back with the same query: {server.Downloads} download(s), {server.Mismatches} changed.");

        Process? relaunched = InstallEvidence.WaitForStartedProcess(HubProcessName, run.HubExe, hubPid, pressedUtc, RelaunchTimeout);
        if (relaunched is not null)
        {
            run.Track(relaunched);
        }

        string? displayVersion = run.ReadUninstallValue("DisplayVersion");
        InstallerChecks.Record(
            caseId + ".installer.ran",
            displayVersion == inputs.VersionB,
            $"After the update the uninstall key shows DisplayVersion '{displayVersion}', expected {inputs.VersionB}.");
        InstallerChecks.Record(
            caseId + ".installer.noStaging",
            !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
            "No <install folder>.new or .old is left after the update.");

        string? fileVersion = InstallerRun.VersionOf(run.HubExe);
        InstallerChecks.Record(
            caseId + ".installed.version",
            fileVersion == inputs.VersionB,
            $"The installed {InstallerRun.HubExeName} reports {fileVersion}, expected {inputs.VersionB}.");
        InstallerChecks.Record(
            caseId + ".relaunch.started",
            relaunched is not null,
            relaunched is null
                ? "The installer did not start the new Hub from the install folder."
                : $"The installer started the Hub from the install folder (pid {relaunched.Id}).");

        if (relaunched is not null)
        {
            CheckRelaunchedWindow(automation, relaunched.Id, caseId);
        }

    }

    /// <summary>Up to 20 paths left in a folder, relative to it, so a failed uninstall names what stayed.</summary>
    private static string Leftovers(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "(folder gone)";
        }

        string[] left = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path))
            .Take(20)
            .ToArray();
        return left.Length == 0 ? "(empty folder)" : string.Join(", ", left);
    }

    /// <summary>
    /// The installer refuses to go over a newer version and treats the same version as a repair. B is installed first;
    /// then the older A must exit non-zero and leave B untouched; then B again must succeed, leaving no staging folder.
    /// The E2E per-user flavor reads its sandbox key, which is the same code path the per-machine HKLM key uses.
    /// </summary>
    private static void RunVersionOrderCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);
        RequireFile(inputs.InstallerB);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "version");
            if (InstallSilently(run, inputs.InstallerB, inputs.VersionB, "version.b"))
            {
                int olderExit = run.RunSetup(inputs.InstallerA, SetupTimeout);
                string? keptKey = run.ReadUninstallValue("DisplayVersion");
                string? keptFile = InstallerRun.VersionOf(run.HubExe);
                InstallerChecks.Record(
                    "version.older.refused",
                    olderExit > 0,
                    $"Installing {inputs.VersionA} over {inputs.VersionB} exited {olderExit}; a refusal is a non-zero exit.");
                InstallerChecks.Record(
                    "version.older.untouched",
                    keptKey == inputs.VersionB && keptFile == inputs.VersionB,
                    $"After the refusal the key shows '{keptKey}' and the Hub exe '{keptFile}', expected {inputs.VersionB}.");
                InstallerChecks.Record(
                    "version.older.noStaging",
                    !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
                    "The refused setup left no <install folder>.new or .old.");

                int sameExit = run.RunSetup(inputs.InstallerB, SetupTimeout);
                string? repairedKey = run.ReadUninstallValue("DisplayVersion");
                string? repairedFile = InstallerRun.VersionOf(run.HubExe);
                InstallerChecks.Record(
                    "version.same.repair",
                    sameExit == 0 && repairedKey == inputs.VersionB && repairedFile == inputs.VersionB,
                    $"Installing {inputs.VersionB} over itself exited {sameExit}; key '{repairedKey}', Hub exe '{repairedFile}'.");
                InstallerChecks.Record(
                    "version.same.noStaging",
                    !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
                    "The repair left no <install folder>.new or .old.");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("version.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("version", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// The E2E /MACHINE switch without an elevated token. The setup must stop in .onInit with a non-zero exit, never
    /// ask for elevation, and write nothing: no Program Files folder, no HKLM or HKCU key of the run, no all-users
    /// shortcut folder. Only HKLM reads and folder checks happen here; nothing is written outside the run's names.
    /// </summary>
    private static void RunMachineAbortCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Assert.Inconclusive("The test process is elevated, so the unelevated refusal cannot be shown. Run from a normal shell.");
            return;
        }

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        string runId = "mach-" + Guid.NewGuid().ToString("N")[..10];
        string root = Path.Combine(Path.GetTempPath(), "bwc-installer-e2e-" + runId);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string machineDir = Path.Combine(programFiles, "BWC-E2E", runId);
        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "BWC-E2E-" + runId);
        string runKey = @"Software\BWC-E2E\" + runId;
        try
        {
            Directory.CreateDirectory(root);
            var info = new ProcessStartInfo(inputs.InstallerA)
            {
                Arguments = "/S /RUNID=" + runId + " /MACHINE",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root,
            };
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("The setup did not start.");
            bool exited = process.WaitForExit(SetupTimeout);
            int exitCode = exited ? process.ExitCode : -1;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
            }

            InstallerChecks.Record(
                "machine.abort.exit",
                exited && exitCode > 0,
                $"The unelevated /MACHINE setup exited {exitCode} (a refusal is a non-zero exit; -1 is a timeout).");

            using Microsoft.Win32.RegistryKey hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using Microsoft.Win32.RegistryKey? machineKey = hklm.OpenSubKey(runKey);
            using Microsoft.Win32.RegistryKey? userKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey);
            InstallerChecks.Record("machine.abort.noKey", machineKey is null && userKey is null, "No HKLM or HKCU key exists for the run.");
            InstallerChecks.Record(
                "machine.abort.noFolder",
                !Directory.Exists(machineDir) && !Directory.Exists(machineDir + ".new") && !Directory.Exists(machineDir + ".upd"),
                "No Program Files folder, staging folder, or protected folder exists for the run.");
            InstallerChecks.Record("machine.abort.noShortcuts", !Directory.Exists(startMenu), "No all-users Start menu folder exists for the run.");
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("machine.abort.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The run's temp folder is empty; a locked folder is left for the next cleanup.
            }

            InstallerChecks.Duration("machine.abort", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    private static void RunUninstallCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "uninstall");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "uninstall.a"))
            {
                bool gone = run.Uninstall(UninstallTimeout);
                InstallerChecks.Record(
                    "uninstall.files",
                    gone && !File.Exists(run.HubExe),
                    gone ? "The install folder is gone." : "The install folder was still there 60 s after /S. Left: " + Leftovers(run.InstallDir));
                InstallerChecks.Record(
                    "uninstall.shortcuts",
                    run.ShortcutCount() == 0,
                    $"{run.ShortcutCount()} shortcut(s) remain in the shortcut folder.");
                InstallerChecks.Record(
                    "uninstall.key",
                    !run.UninstallKeyExists(),
                    "The run's uninstall key is removed.");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("uninstall.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("uninstall", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// Silent install of one setup into the run's folders. Records the exit code, the uninstall key's DisplayVersion,
    /// and the shortcut count. Returns true only when the install ran and shows the expected version.
    /// </summary>
    private static bool InstallSilently(InstallerRun run, string setupPath, string version, string prefix)
    {
        int exitCode = run.RunSetup(setupPath, SetupTimeout);
        bool hubPresent = File.Exists(run.HubExe);
        bool ran = exitCode == 0 && hubPresent;
        InstallerChecks.Record(
            prefix + ".setup",
            ran,
            $"The silent setup exited {exitCode}; the Hub exe is {(hubPresent ? "present" : "missing")}.");

        string? displayVersion = run.ReadUninstallValue("DisplayVersion");
        bool sameVersion = displayVersion == version;
        InstallerChecks.Record(
            prefix + ".displayVersion",
            sameVersion,
            $"The uninstall key shows DisplayVersion '{displayVersion}', expected {version}.");

        int shortcuts = run.ShortcutCount();
        int expectedShortcuts = InstallerRun.ProductShortcuts.Count;
        InstallerChecks.Record(
            prefix + ".shortcuts",
            shortcuts == expectedShortcuts,
            $"{shortcuts} shortcut(s) were written; expected {expectedShortcuts} (" + string.Join(", ", InstallerRun.ProductShortcuts.Select(shortcut => shortcut.Name)) + ").");
        if (ran)
        {
            bool aumid = ShortcutAumidsAre(run, out string aumidNotes);
            InstallerChecks.Record(prefix + ".aumid", aumid, aumidNotes);
            bool icons = ShortcutIconsAre(run, out string iconNotes);
            InstallerChecks.Record(prefix + ".icons", icons, iconNotes);
        }

        return ran && sameVersion;
    }

    /// <summary>
    /// Every shortcut of <see cref="InstallerRun.ProductShortcuts"/> carries its AppUserModelID, read back through the shell
    /// property store (installer.md M45, M54: the installed BwcUpdate.exe --set-aumid wrote them). The installer only warns
    /// on a miss, so this is where a broken write fails.
    /// </summary>
    private static bool ShortcutAumidsAre(InstallerRun run, out string notes)
    {
        var lines = new List<string>();
        bool all = true;
        foreach ((string name, string id, _) in InstallerRun.ProductShortcuts)
        {
            string path = Path.Combine(run.ShortcutDir, name);
            try
            {
                string? actual = MachineNative.ShortcutAppUserModelId(path);
                lines.Add($"{name}: '{actual}', expected '{id}'");
                all &= string.Equals(actual, id, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or FileNotFoundException)
            {
                lines.Add($"{name}: could not be read ({ex.GetType().Name})");
                all = false;
            }
        }

        notes = string.Join("; ", lines) + ".";
        return all;
    }

    /// <summary>
    /// Every shortcut shows the icon of its own exe: the icon location is that exe in the install folder at index 0
    /// (installer.md M53). The exe embeds its own icon (bwc-gpo.ico for the Local Group Policy Editor), so this keeps the
    /// Start menu entry from showing the Hub icon or a missing path.
    /// </summary>
    private static bool ShortcutIconsAre(InstallerRun run, out string notes)
    {
        var lines = new List<string>();
        bool all = true;
        foreach ((string name, _, string exeName) in InstallerRun.ProductShortcuts)
        {
            string path = Path.Combine(run.ShortcutDir, name);
            string expected = Path.Combine(run.InstallDir, exeName);
            try
            {
                (string icon, int index) = MachineNative.ShortcutIconLocation(path);
                bool same = string.Equals(Path.GetFullPath(icon), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) && index == 0;
                lines.Add($"{name}: '{icon}',{index}, expected '{expected}',0");
                all &= same;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException)
            {
                lines.Add($"{name}: could not be read ({ex.GetType().Name})");
                all = false;
            }
        }

        notes = string.Join("; ", lines) + ".";
        return all;
    }

    /// <summary>Starts BwcRegedit from the install folder, unelevated, and keeps it open for the installer to close.</summary>
    private static Process StartRegedit(E2EConfig config, InstallerRun run, string caseId)
    {
        Process regedit = run.StartTracked(run.RegeditExe, config.DataRootEnvironment("installer-" + caseId));
        Thread.Sleep(RegeditSettle);
        InstallerChecks.Record(
            caseId + ".regedit.open",
            !regedit.HasExited,
            "BwcRegedit.exe from the install folder is open before the update.");
        return regedit;
    }

    /// <summary>Starts BwcGpedit from the install folder, unelevated, beside the open Registry Editor (installer-e2e.md 95).
    /// </summary>
    private static Process StartGpedit(E2EConfig config, InstallerRun run, string caseId)
    {
        Process gpedit = run.StartTracked(run.GpeditExe, config.DataRootEnvironment("installer-" + caseId));
        Thread.Sleep(RegeditSettle);
        InstallerChecks.Record(
            caseId + ".gpedit.open",
            !gpedit.HasExited,
            "BwcGpedit.exe from the install folder is open before the update.");
        return gpedit;
    }

    /// <summary>
    /// With BwcRegedit and BwcGpedit open, the Hub shows "Close these running tools ... Registry Editor, Local Group Policy
    /// Editor" in a Check for Updates window and does not start the update. The check reads that message from the Hub's
    /// own dialog.
    /// </summary>
    private static void CheckRegeditRefusal(UIA3Automation automation, int hubPid, string caseId)
    {
        AutomationElement? window = HubUi.FindWindowInProcess(automation, hubPid, RefusalTitle, DialogTimeout);
        AutomationElement? message = window is null ? null : HubUi.WaitWithin(window, "DialogMessage", DialogTimeout);
        string text = message is null ? string.Empty : HubUi.TextOf(message);
        bool refused = text.Contains(RegeditName, StringComparison.Ordinal) && text.Contains(GpeditName, StringComparison.Ordinal);
        InstallerChecks.Record(
            caseId + ".hub.refusesWhileToolsOpen",
            refused,
            refused
                ? "The Hub refused the update while BwcRegedit and BwcGpedit were open: " + text
                : "The Hub did not name both the Registry Editor and the Local Group Policy Editor. Message: '" + text + "'.");
    }

    private static void CheckRelaunchedWindow(UIA3Automation automation, int pid, string caseId)
    {
        AutomationElement? window = HubUi.FindWindowInProcess(automation, pid, HubTitle, DialogTimeout);
        InstallerChecks.Record(
            caseId + ".relaunch.window",
            window is not null,
            window is null ? "The relaunched Hub showed no main window." : "The relaunched Hub showed its main window.");
    }

    /// <summary>
    /// The Hub environment: a scoped data root, startup check off, the loopback feed with its key, and the run id and
    /// shortcut folder the E2E installer falls back to when the helper starts it without those switches.
    /// </summary>
    private static Dictionary<string, string> HubEnvironment(
        E2EConfig config,
        string caseId,
        InstallerRun run,
        RedirectFeedServer server,
        InstallerFixture feed)
    {
        string scope = "installer-" + caseId;
        string settingsPath = Path.Combine(config.DataRootFor(scope), "hub", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, SettingsJson);

        var environment = new Dictionary<string, string>(config.DataRootEnvironment(scope), StringComparer.Ordinal)
        {
            [ManifestVariable] = server.ManifestUrl,
            [TrustedKeyVariable] = feed.PublicKeyBase64,
            [AllowHttpVariable] = "1",

            // The update helper's elevated step runs unelevated in this flavor: it copies the installer under this
            // folder of the run's temp, in place of <install folder>.upd, and the installer runs from that copy.
            [ProtectedRootVariable] = InstallEvidence.ProtectedRoot(config, scope),
            [InstallerRunIdVariable] = run.RunId,
            [InstallerShortcutsVariable] = run.ShortcutDir,
        };
        foreach (KeyValuePair<string, string> pair in config.TempEnvironment(scope))
        {
            environment[pair.Key] = pair.Value;
        }

        return environment;
    }

    /// <summary>Waits until one of the Hub's log lines under the data root contains the text, or the timeout passes.</summary>
    private static bool WaitForHubLog(string dataRoot, string text, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (InstallEvidence.HubLogContains(dataRoot, text))
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return InstallEvidence.HubLogContains(dataRoot, text);
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The installer named by scripts/test-installer-e2e.js is missing.", path);
        }
    }

    private static void AssertNoFailures(int start)
    {
        IReadOnlyList<string> failures = InstallerChecks.FailuresSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            "Installer E2E checks failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}
