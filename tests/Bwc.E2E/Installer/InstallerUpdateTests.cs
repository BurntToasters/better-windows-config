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
    private const string CheckMenuId = "MenuItem_CheckForUpdates";
    private const string InstallButtonId = "UpdateInstall";
    private const string HubProcessName = "BetterWindowsConfig";
    private const string ManifestVariable = "BWC_E2E_UPDATE_MANIFEST_URL";
    private const string TrustedKeyVariable = "BWC_E2E_UPDATE_TRUSTED_KEY";
    private const string AllowHttpVariable = "BWC_E2E_UPDATE_ALLOW_HTTP";
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
        InstallerChecks.Record(prefix + ".shortcuts", shortcuts > 0, $"{shortcuts} shortcut(s) were written.");
        return ran && sameVersion;
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

    /// <summary>
    /// With BwcRegedit open, the Hub shows "Close these running tools ... Registry Editor" in a Check for Updates
    /// window and does not start the update. The check reads that message from the Hub's own dialog.
    /// </summary>
    private static void CheckRegeditRefusal(UIA3Automation automation, int hubPid, string caseId)
    {
        AutomationElement? window = HubUi.FindWindowInProcess(automation, hubPid, RefusalTitle, DialogTimeout);
        AutomationElement? message = window is null ? null : HubUi.WaitWithin(window, "DialogMessage", DialogTimeout);
        string text = message is null ? string.Empty : HubUi.TextOf(message);
        bool refused = text.Contains(RegeditName, StringComparison.Ordinal);
        InstallerChecks.Record(
            caseId + ".hub.refusesWhileRegeditOpen",
            refused,
            refused
                ? "The Hub refused the update while BwcRegedit was open: " + text
                : "The Hub did not show the Registry Editor refusal. Message: '" + text + "'.");
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
