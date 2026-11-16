using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bwc.E2E.Harness;
using Bwc.E2E.Hub;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Installer;

/// <summary>
/// The legacy per-user cleanup of the 0.1.2 Hub (docs/failure-modes/hub.md, "Legacy per-user cleanup (0.1.2)", LC1 to LC20;
/// installer.md M13 to M17 and M41 to M43). The Hub reads the old per-user uninstall key once per run and runs the old
/// Uninstall.exe unelevated. In the E2E flavor the key moves to HKCU\Software\BWC-E2E\&lt;run id&gt;\Uninstall and the
/// shortcuts to a sandbox folder (BWC_E2E_LEGACY_RUNID and BWC_E2E_LEGACY_SHORTCUTS), so no case reads the real key or the
/// real Start menu. Refusal cases use fakes that are never programs: a text file named Uninstall.exe cannot run, so a
/// wrong start fails with a Windows error and no prompt. The real case installs the E2E installer A as the "old install"
/// (scripts/test-installer-e2e.js supplies it) and starts the Hub from the publish folder, a different folder.
/// Every check goes to artifacts/e2e/migration-&lt;rid&gt;.json.
/// </summary>
[TestClass]
public sealed class MigrationTests
{
    private const string RunIdVariable = "BWC_E2E_LEGACY_RUNID";
    private const string ShortcutsVariable = "BWC_E2E_LEGACY_SHORTCUTS";
    private const string ForceElevatedVariable = "BWC_E2E_UPDATE_FORCE_ELEVATED";
    private const string StartedLine = "Legacy cleanup started";
    private const string FinishedLine = "Legacy cleanup finished";
    private const string StaleRemovedLine = "Legacy cleanup stale entry removed";
    private const string SettingsJson = "{\n  \"CheckForUpdatesOnStartup\": false,\n  \"SettingsVersion\": 2\n}";
    private const string UninstallerName = "Uninstall.exe";
    private const string RealShortcutName = "Better Windows Config.lnk";
    private const string RegeditShortcutName = "Registry Editor (BWC).lnk";
    private const string OtherShortcutName = "Some Other Tool.lnk";
    private static readonly string[] OutcomeMarkers =
    [
        "Legacy cleanup skipped (",
        "Legacy cleanup refused (",
        FinishedLine,
        StaleRemovedLine,
        "Legacy cleanup timeout",
        "Legacy cleanup failed (",
    ];

    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromSeconds(120);

    [ClassCleanup]
    public static void ClassCleanup()
    {
        E2EConfig config = GuiGate.Config;
        MigrationChecks.Write(Path.Combine(config.ArtifactsDir, $"migration-{config.Rid}.json"), config.Rid);
    }

    /// <summary>LC9: the E2E installer A is the old per-user install. The Hub removes it and keeps running.</summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_RealOldInstallIsRemoved()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The real old install case is started by scripts/test-installer-e2e.js.");
            return;
        }

        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;
        MigrationChecks.MarkRealInstallRan();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "migreal");
            int setupExit = run.RunSetup(inputs.InstallerA, SetupTimeout);
            MigrationChecks.Record("migration.realUninstall.oldInstalled", setupExit == 0, $"Installer A (the old per-user install) exited {setupExit}.");
            if (setupExit != 0)
            {
                return;
            }

            bool installedState = run.UninstallKeyExists() && Directory.Exists(run.InstallDir) && File.Exists(run.UninstallerExe) && run.ShortcutCount() > 0;
            MigrationChecks.Record(
                "migration.realUninstall.oldState",
                installedState,
                $"The old install has its key ({run.UninstallKeyExists()}), folder ({Directory.Exists(run.InstallDir)}), Uninstall.exe ({File.Exists(run.UninstallerExe)}), and {run.ShortcutCount()} shortcut(s).");
            MigrationChecks.Record(
                "migration.realUninstall.differentFolder",
                !string.Equals(Path.TrimEndingDirectorySeparator(run.InstallDir), Path.TrimEndingDirectorySeparator(config.PublishDir), StringComparison.OrdinalIgnoreCase),
                $"The Hub starts from {config.PublishDir}, the old install is in {run.InstallDir}.");

            using Process hub = StartHub(config, "mig-real", run, seam: true, extra: null);
            try
            {
                bool done = WaitUntil(
                    () => !run.UninstallKeyExists() && !Directory.Exists(run.InstallDir) && run.ShortcutCount() == 0,
                    UninstallTimeout);
                string log = ReadHubLog(config, "mig-real");
                MigrationChecks.Record("migration.realUninstall.startedLogged", log.Contains(StartedLine, StringComparison.Ordinal), "The Hub logged that it started the old uninstaller.");
                MigrationChecks.Record("migration.realUninstall.keyGone", !run.UninstallKeyExists(), "The sandbox uninstall key of the old install is gone.");
                MigrationChecks.Record("migration.realUninstall.folderGone", !Directory.Exists(run.InstallDir), "The old install folder is gone: " + Leftovers(run.InstallDir));
                MigrationChecks.Record("migration.realUninstall.shortcutsGone", run.ShortcutCount() == 0, $"{run.ShortcutCount()} sandbox shortcut(s) remain.");
                bool finished = WaitUntil(() => ReadHubLog(config, "mig-real").Contains(FinishedLine, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
                MigrationChecks.Record("migration.realUninstall.finishedLogged", finished, "The Hub logged that the uninstall entry is gone.");
                MigrationChecks.Record("migration.realUninstall.done", done, "The old install was removed within " + UninstallTimeout.TotalSeconds + " s.");
                MigrationChecks.Record("migration.realUninstall.hubStillRunning", !hub.HasExited, "The Hub kept running during and after the cleanup.");
            }
            finally
            {
                InstallEvidence.StopProcess(hub);
            }
        }
        catch (Exception ex)
        {
            MigrationChecks.Record("migration.realUninstall.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    /// <summary>LC3 to LC8: a key that names a place the Hub must never run an uninstaller from is
    /// refused, and nothing changes.</summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_UnsafeInstallLocationsAreRefused()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;

        string hubFolder = Path.TrimEndingDirectorySeparator(config.PublishDir);
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var cases = new (string Id, string? Location, string Code)[]
        {
            ("noLocation", null, "no-install-location"),
            ("emptyLocation", string.Empty, "no-install-location"),
            ("ownFolder", hubFolder, "own-folder"),
            ("ownFolderUpperTrailing", hubFolder.ToUpperInvariant() + @"\", "own-folder"),
            ("driveRoot", systemRoot, "drive-root"),
            ("systemFolder.windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system-folder"),
            ("systemFolder.system32", Environment.SystemDirectory, "system-folder"),
            ("systemFolder.programData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "system-folder"),
            ("systemFolder.profile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "system-folder"),
            ("programFiles", Path.Combine(programFiles, "BWC-Migration-Test-No-Such-Folder"), "program-files"),
            ("programFilesRoot", programFiles, "program-files"),
            ("badPath.relative", @"bwc-old\app", "bad-path"),
            ("badPath.unc", @"\\localhost\share\bwc", "bad-path"),
            ("badPath.device", @"\\?\C:\bwc-old", "bad-path"),
            ("badPath.dotdot", systemRoot + @"bwc-old\..\Windows", "bad-path"),
            ("badPath.forwardSlash", systemRoot.TrimEnd('\\') + "/bwc-old/app", "bad-path"),
            ("badPath.quote", systemRoot + "bwc-\"old", "bad-path"),
            ("badPath.trailingSpace", systemRoot + @"bwc-old \app", "bad-path"),
        };

        foreach ((string id, string? location, string code) in cases)
        {
            try
            {
                using InstallerRun run = InstallerRun.Create(config, "migu");
                run.SeedUninstallKey(location);
                string scope = "mig-unsafe";
                using Process hub = StartHub(config, scope, run, seam: true, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    string checkId = "migration." + id + ".refused";
                    bool refused = outcome is not null && outcome.Contains($"refused ({code})", StringComparison.Ordinal);
                    MigrationChecks.Record(checkId, refused, $"InstallLocation '{location ?? "(no value)"}' gave: {outcome ?? "(no outcome in the log)"}. Expected refused ({code}).");
                    MigrationChecks.Record(checkId + ".keyKept", run.UninstallKeyExists(), "The key was not changed.");
                    MigrationChecks.Record(checkId + ".noStart", !ReadHubLog(config, scope).Contains(StartedLine, StringComparison.Ordinal), "No uninstaller was started.");
                    MigrationChecks.Record(checkId + ".hubRunning", !hub.HasExited, "The Hub kept running.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, "mig-unsafe");
                }
            }
            catch (Exception ex)
            {
                MigrationChecks.Record("migration." + id + ".exception", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        AssertNoFailures(start);
    }

    /// <summary>LC10, LC11: an Uninstall.exe that requests elevation, has no manifest, or is not a
    /// plain file is never started.</summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_ElevatedManifestUninstallerIsRefused()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;

        const string elevatedTemplate = "<?xml version=\"1.0\"?><assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">" +
            "<trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v3\"><security><requestedPrivileges>" +
            "<requestedExecutionLevel level=\"{0}\" uiAccess=\"false\" /></requestedPrivileges></security></trustInfo></assembly>";
        var cases = new (string Id, Action<string> Make, string Code)[]
        {
            ("elevatedManifest", path => File.WriteAllText(path, string.Format(elevatedTemplate, "requireAdministrator")), "uninstaller-elevation"),
            ("highestAvailableManifest", path => File.WriteAllText(path, string.Format(elevatedTemplate, "highestAvailable")), "uninstaller-elevation"),
            ("elevatedUtf16Manifest", path => File.WriteAllBytes(path, Encoding.Unicode.GetBytes(string.Format(elevatedTemplate, "requireAdministrator"))), "uninstaller-elevation"),
            ("mixedManifest", path => File.WriteAllText(path, string.Format(elevatedTemplate, "asInvoker") + string.Format(elevatedTemplate, "requireAdministrator")), "uninstaller-elevation"),
            ("noManifest", path => File.WriteAllText(path, "this file has no application manifest at all"), "uninstaller-elevation"),
            ("uninstallerEmpty", path => File.WriteAllBytes(path, []), "uninstaller-invalid"),
            ("uninstallerInvalid", path => Directory.CreateDirectory(path), "uninstaller-invalid"),
        };

        foreach ((string id, Action<string> make, string code) in cases)
        {
            try
            {
                using InstallerRun run = InstallerRun.Create(config, "migm");
                string oldDir = Path.Combine(run.Root, "old-install");
                Directory.CreateDirectory(oldDir);
                string fake = Path.Combine(oldDir, UninstallerName);
                make(fake);
                string before = File.Exists(fake) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fake))) : "(not a file)";
                run.SeedUninstallKey(oldDir);
                int shortcutsBefore = SeedShortcuts(run.ShortcutDir);
                string scope = "mig-manifest";
                using Process hub = StartHub(config, scope, run, seam: true, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    string checkId = "migration." + id + ".refused";
                    MigrationChecks.Record(checkId, outcome is not null && outcome.Contains($"refused ({code})", StringComparison.Ordinal), $"The log said: {outcome ?? "(no outcome in the log)"}. Expected refused ({code}).");
                    string log = ReadHubLog(config, scope);
                    MigrationChecks.Record(checkId + ".neverStarted", !log.Contains(StartedLine, StringComparison.Ordinal) && !log.Contains("(start-failed)", StringComparison.Ordinal), "The fake was not started (no start line and no start failure).");
                    string after = File.Exists(fake) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fake))) : "(not a file)";
                    MigrationChecks.Record(checkId + ".untouched", before == after && Directory.Exists(oldDir), "The old folder and the fake are unchanged.");
                    MigrationChecks.Record(checkId + ".keyAndShortcutsKept", run.UninstallKeyExists() && run.ShortcutCount() == shortcutsBefore, "The key and the shortcuts are unchanged.");
                    MigrationChecks.Record(checkId + ".hubRunning", !hub.HasExited, "The Hub kept running.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }
            catch (Exception ex)
            {
                MigrationChecks.Record("migration." + id + ".exception", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// LC23, LC24 (security review 2, finding 8): the key is a user-writable value, so an `asInvoker` Uninstall.exe
    /// in a folder that
    /// is not a Better Windows Config install is never started, and a folder on a drive that is not there is not "gone". The
    /// fake is a text file, so a wrong start would fail with a Windows error and show no prompt.
    /// </summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_NotBwcInstallAndOfflineDriveAreRefused()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;

        const string asInvokerManifest = "<?xml version=\"1.0\"?><assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">" +
            "<trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v3\"><security><requestedPrivileges>" +
            "<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\" /></requestedPrivileges></security></trustInfo></assembly>";
        string? offlineDrive = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => (char)letter + @":\")
            .FirstOrDefault(root => !Directory.Exists(root));
        var cases = new (string Id, Func<InstallerRun, string> Prepare, string Code, bool FolderExists)[]
        {
            ("notBwc", run => MakeOldFolder(run, asInvokerManifest, manifest: false, hub: false), "not-bwc-install", true),
            ("notBwc.manifestOnly", run => MakeOldFolder(run, asInvokerManifest, manifest: true, hub: false), "not-bwc-install", true),
            ("notBwc.hubOnly", run => MakeOldFolder(run, asInvokerManifest, manifest: false, hub: true), "not-bwc-install", true),
            ("driveMissing", _ => offlineDrive is null ? string.Empty : offlineDrive + "bwc-old-install-offline", "drive-missing", false),
        };

        foreach ((string id, Func<InstallerRun, string> prepare, string code, bool folderExists) in cases)
        {
            try
            {
                using InstallerRun run = InstallerRun.Create(config, "migb");
                string location = prepare(run);
                if (location.Length == 0)
                {
                    MigrationChecks.Record("migration." + id + ".refused", false, "No unused drive letter exists on this machine, so the offline-drive case cannot be built.");
                    continue;
                }

                run.SeedUninstallKey(location);
                int shortcutsBefore = SeedShortcuts(run.ShortcutDir);
                string before = folderExists ? Leftovers(location) : "(no folder)";
                string scope = "mig-notbwc";
                using Process hub = StartHub(config, scope, run, seam: true, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    string checkId = "migration." + id + ".refused";
                    MigrationChecks.Record(checkId, outcome is not null && outcome.Contains($"refused ({code})", StringComparison.Ordinal), $"The log said: {outcome ?? "(no outcome in the log)"}. Expected refused ({code}).");
                    string log = ReadHubLog(config, scope);
                    MigrationChecks.Record(checkId + ".neverStarted", !log.Contains(StartedLine, StringComparison.Ordinal) && !log.Contains("(start-failed)", StringComparison.Ordinal), "The fake was not started (no start line and no start failure).");
                    MigrationChecks.Record(checkId + ".untouched", (folderExists ? Leftovers(location) : "(no folder)") == before && Directory.Exists(location) == folderExists, "The old folder and its files are unchanged.");
                    MigrationChecks.Record(checkId + ".keyAndShortcutsKept", run.UninstallKeyExists() && run.ShortcutCount() == shortcutsBefore, "The key and the shortcuts are unchanged, so the next start looks again.");
                    MigrationChecks.Record(checkId + ".hubRunning", !hub.HasExited, "The Hub kept running.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }
            catch (Exception ex)
            {
                MigrationChecks.Record("migration." + id + ".exception", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// An old-install look-alike: an asInvoker Uninstall.exe, and optionally the manifest and the Hub exe (text
    /// files).
    /// </summary>
    private static string MakeOldFolder(InstallerRun run, string uninstallerText, bool manifest, bool hub)
    {
        string folder = Path.Combine(run.Root, "old-look-alike");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, UninstallerName), uninstallerText);
        if (manifest)
        {
            File.WriteAllText(Path.Combine(folder, "installed-files.txt"), "BetterWindowsConfig.exe\r\n");
        }

        if (hub)
        {
            File.WriteAllText(Path.Combine(folder, "BetterWindowsConfig.exe"), "not a program");
        }

        return folder;
    }

    /// <summary>LC12, LC13: no Uninstall.exe. With the folder gone the stale key and the two shortcuts
    /// go; with the folder there nothing does.</summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_MissingUninstallerCleansStaleEntry()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;

        try
        {
            using (InstallerRun run = InstallerRun.Create(config, "migs"))
            {
                string gone = Path.Combine(run.Root, "old-install-that-is-gone");
                run.SeedUninstallKey(gone);
                int total = SeedShortcuts(run.ShortcutDir);
                MigrationChecks.Record("migration.staleEntry.setup", total == 3 && !Directory.Exists(gone), $"{total} shortcut files, the old folder does not exist.");
                string scope = "mig-stale";
                using Process hub = StartHub(config, scope, run, seam: true, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    MigrationChecks.Record("migration.staleEntry.logged", outcome is not null && outcome.Contains(StaleRemovedLine, StringComparison.Ordinal), $"The log said: {outcome ?? "(no outcome in the log)"}.");
                    MigrationChecks.Record("migration.staleEntry.keyGone", !run.UninstallKeyExists(), "The stale uninstall key was removed.");
                    MigrationChecks.Record(
                        "migration.staleEntry.shortcutsGone",
                        !File.Exists(Path.Combine(run.ShortcutDir, RealShortcutName)) && !File.Exists(Path.Combine(run.ShortcutDir, RegeditShortcutName)),
                        "Both old shortcuts were removed by their fixed names.");
                    MigrationChecks.Record("migration.staleEntry.otherShortcutKept", File.Exists(Path.Combine(run.ShortcutDir, OtherShortcutName)), "A shortcut with another name stayed.");
                    MigrationChecks.Record("migration.staleEntry.noUninstallerStarted", !ReadHubLog(config, scope).Contains(StartedLine, StringComparison.Ordinal), "No uninstaller was started.");
                    MigrationChecks.Record("migration.staleEntry.hubRunning", !hub.HasExited, "The Hub kept running.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }

            using (InstallerRun run = InstallerRun.Create(config, "migp"))
            {
                string present = Path.Combine(run.Root, "old-install-folder-present");
                Directory.CreateDirectory(present);
                run.SeedUninstallKey(present);
                int total = SeedShortcuts(run.ShortcutDir);
                string scope = "mig-present";
                using Process hub = StartHub(config, scope, run, seam: true, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    MigrationChecks.Record(
                        "migration.uninstallerMissingFolderPresent.refused",
                        outcome is not null && outcome.Contains("refused (uninstaller-missing)", StringComparison.Ordinal),
                        $"The log said: {outcome ?? "(no outcome in the log)"}.");
                    MigrationChecks.Record(
                        "migration.uninstallerMissingFolderPresent.kept",
                        run.UninstallKeyExists() && run.ShortcutCount() == total && Directory.Exists(present),
                        "The key, the shortcuts, and the folder are unchanged, so the next start tries again.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }
        }
        catch (Exception ex)
        {
            MigrationChecks.Record("migration.staleEntry.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    /// <summary>LC1: an elevated Hub (forced by the E2E seam) never reads the key. The stale entry
    /// would otherwise be removed.</summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_ElevatedHubSkipsCleanup()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "mige");
            run.SeedUninstallKey(Path.Combine(run.Root, "old-install-that-is-gone"));
            int total = SeedShortcuts(run.ShortcutDir);
            string scope = "mig-elevated";
            var extra = new Dictionary<string, string>(StringComparer.Ordinal) { [ForceElevatedVariable] = "1" };
            using Process hub = StartHub(config, scope, run, seam: true, extra: extra);
            try
            {
                string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                MigrationChecks.Record("migration.elevated.skipped", outcome is not null && outcome.Contains("skipped (elevated)", StringComparison.Ordinal), $"The log said: {outcome ?? "(no outcome in the log)"}.");
                Thread.Sleep(1500);
                MigrationChecks.Record("migration.elevated.nothingChanged", run.UninstallKeyExists() && run.ShortcutCount() == total, "The key and the shortcuts are unchanged.");
                MigrationChecks.Record("migration.elevated.hubRunning", !hub.HasExited, "The Hub kept running.");
            }
            finally
            {
                InstallEvidence.StopProcess(hub);
                ResetHubLog(config, scope);
            }
        }
        catch (Exception ex)
        {
            MigrationChecks.Record("migration.elevated.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    /// <summary>LC2, LC19, LC20: no key is a quiet skip, and the E2E Hub without a valid seam is off
    /// (it never reads the real key).</summary>
    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Migration_NoKeyAndSeamOffCasesSkip()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        int start = MigrationChecks.Count;
        try
        {
            using (InstallerRun run = InstallerRun.Create(config, "mign"))
            {
                string scope = "mig-nokey";
                using Process hub = StartHub(config, scope, run, seam: true, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    MigrationChecks.Record("migration.nokey.skipped", outcome is not null && outcome.Contains("skipped (no-key)", StringComparison.Ordinal), $"The log said: {outcome ?? "(no outcome in the log)"}.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }

            using (InstallerRun run = InstallerRun.Create(config, "migo"))
            {
                run.SeedUninstallKey(Path.Combine(run.Root, "old-install-that-is-gone"));
                string scope = "mig-noseam";
                using Process hub = StartHub(config, scope, run, seam: false, extra: null);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    MigrationChecks.Record("migration.noSeam.skipped", outcome is not null && outcome.Contains("skipped (e2e-no-seam)", StringComparison.Ordinal), $"The log said: {outcome ?? "(no outcome in the log)"}.");
                    MigrationChecks.Record("migration.noSeam.keyKept", run.UninstallKeyExists(), "The sandbox key was not touched.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }

            var badSeams = new (string Id, string? RunId, string? Shortcuts)[]
            {
                ("badRunId", "bad id!", Path.Combine(Path.GetTempPath(), "bwc-migration-seam")),
                ("dotsRunId", "..", Path.Combine(Path.GetTempPath(), "bwc-migration-seam")),
                ("noShortcuts", "valid-run-id", null),
                ("relativeShortcuts", "valid-run-id", @"relative\folder"),
            };
            foreach ((string id, string? runId, string? shortcuts) in badSeams)
            {
                string scope = "mig-badseam";
                var extra = new Dictionary<string, string>(StringComparer.Ordinal);
                if (runId is not null)
                {
                    extra[RunIdVariable] = runId;
                }

                if (shortcuts is not null)
                {
                    extra[ShortcutsVariable] = shortcuts;
                }

                using Process hub = StartRaw(config, scope, extra);
                try
                {
                    string? outcome = WaitForOutcome(config, scope, OutcomeTimeout);
                    MigrationChecks.Record(
                        "migration.badSeam." + id + ".skipped",
                        outcome is not null && outcome.Contains("skipped (e2e-seam-invalid)", StringComparison.Ordinal),
                        $"The log said: {outcome ?? "(no outcome in the log)"}.");
                }
                finally
                {
                    InstallEvidence.StopProcess(hub);
                    ResetHubLog(config, scope);
                }
            }
        }
        catch (Exception ex)
        {
            MigrationChecks.Record("migration.seamOff.exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        AssertNoFailures(start);
    }

    private static void AssertNoFailures(int start)
    {
        IReadOnlyList<string> failures = MigrationChecks.FailuresSince(start);
        Assert.AreEqual(0, failures.Count, "Migration checks failed:" + System.Environment.NewLine + string.Join(System.Environment.NewLine, failures));
    }

    /// <summary>Two fixed-name shortcut files and one other file in the sandbox shortcut folder.
    /// Returns how many files exist there.</summary>
    private static int SeedShortcuts(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string name in new[] { RealShortcutName, RegeditShortcutName, OtherShortcutName })
        {
            File.WriteAllText(Path.Combine(folder, name), "not a real shortcut");
        }

        return Directory.GetFiles(folder, "*.lnk").Length;
    }

    /// <summary>Starts the Hub from the publish folder with the migration seam pointing at the run's sandbox.</summary>
    private static Process StartHub(E2EConfig config, string scope, InstallerRun run, bool seam, IReadOnlyDictionary<string, string>? extra)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (seam)
        {
            environment[RunIdVariable] = run.RunId;
            environment[ShortcutsVariable] = run.ShortcutDir;
        }

        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> pair in extra)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        return StartRaw(config, scope, environment);
    }

    /// <summary>Starts the Hub with its own data root and temp folder, the update check off, and the
    /// compatibility layer that never prompts.</summary>
    private static Process StartRaw(E2EConfig config, string scope, IReadOnlyDictionary<string, string> environment)
    {
        string dataRoot = config.DataRootFor(scope);
        string settings = Path.Combine(dataRoot, "hub", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, SettingsJson);

        var startInfo = new ProcessStartInfo(config.HubExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
        };
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = dataRoot;
        startInfo.Environment["BWC_E2E_UPDATE_MANIFEST_URL"] = "none";
        foreach (KeyValuePair<string, string> pair in config.TempEnvironment(scope))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned no process for " + config.HubExePath);
    }

    private static string HubLogFolder(E2EConfig config, string scope) => Path.Combine(config.DataRootFor(scope), "hub", "logs");

    /// <summary>All text of the Hub's daily logs under the scope's data root, read without a write lock.</summary>
    private static string ReadHubLog(E2EConfig config, string scope)
    {
        string folder = HubLogFolder(config, scope);
        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(folder, "log-*.txt"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text.Append(reader.ReadToEnd());
            }
            catch (IOException)
            {
                // The Hub is writing the file right now; the next poll reads it.
            }
        }

        return text.ToString();
    }

    /// <summary>Removes the log files, so the next Hub that shares the scope's data root starts from an empty log.</summary>
    private static void ResetHubLog(E2EConfig config, string scope)
    {
        string folder = HubLogFolder(config, scope);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "log-*.txt"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that is still held by a process that is going away is overwritten by the next write.
            }
        }
    }

    /// <summary>The first cleanup line that ends the run (a skip, a refusal, a finish, a removal, a
    /// timeout, a failure), or null.</summary>
    private static string? WaitForOutcome(E2EConfig config, string scope, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            foreach (string line in ReadHubLog(config, scope).Split('\n'))
            {
                if (OutcomeMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
                {
                    return line.Trim();
                }
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(300);
        }
        while (DateTime.UtcNow < deadline);

        return condition();
    }

    private static string Leftovers(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "(folder gone)";
        }

        try
        {
            string[] left = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(folder, path))
                .Take(20)
                .ToArray();
            return left.Length == 0 ? "(empty folder)" : string.Join(", ", left);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return "(could not list: " + ex.Message + ")";
        }
    }
}
