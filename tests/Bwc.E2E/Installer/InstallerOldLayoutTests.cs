using System.Diagnostics;
using Bwc.E2E.Harness;
using Bwc.E2E.Hub;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Installer;

/// <summary>
/// The update from the 0.1.x layout to the current version (docs/failure-modes/installer-e2e.md 107 to 124). The old
/// setup is built from the current tree by scripts/test-installer-e2e.js with no BwcGpedit files, no Local Group Policy
/// Editor shortcut, and version 0.1.99. It is installed silently, the real update to B is pressed in its Hub, and the
/// result must be the full three-tool layout. 0.1.x carries no migration code, so this proves a clean install over
/// the old layout and nothing more.
/// Unelevated, per-user sandbox, like the other update cases.
/// </summary>
public sealed partial class InstallerUpdateTests
{
    private const string OldCaseId = "old";
    private const string ManifestFileName = "installed-files.txt";
    private const string InstalledText = "Installed";
    private const string NotInstalledText = "Not installed";

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Update_From01xLayoutAddsGpedit()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        if (inputs.OldInstaller is null || inputs.OldVersion is null)
        {
            Assert.Inconclusive("The driver passed no old-layout setup (BWC_INSTALLER_OLD); scripts/test-installer-e2e.js builds it.");
            return;
        }

        RunOldLayoutUpdateCase(inputs, inputs.OldInstaller, inputs.OldVersion);
    }

    private static void RunOldLayoutUpdateCase(InstallerInputs inputs, string oldSetup, string oldVersion)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(oldSetup);
        RequireFile(inputs.InstallerB);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        string installerStaging = InstallEvidence.StagingRoot(config, "installer-" + OldCaseId);
        HashSet<string> stagingBefore = InstallEvidence.StagingFolders(installerStaging);
        try
        {
            using InstallerRun run = InstallerRun.Create(config, OldCaseId);
            if (InstallSilently(run, oldSetup, oldVersion, OldCaseId + ".a", InstallerRun.OldLayoutShortcuts))
            {
                CheckOldLayoutBeforeUpdate(run, oldVersion);
                string keepPath = Path.Combine(config.DataRootFor("installer-" + OldCaseId), "e2e-user-data", "keep.bin");
                WriteBytes(keepPath, 512);
                string keepHash = Hash(keepPath);

                RunUpdateFromHub(
                    config,
                    run,
                    inputs,
                    OldCaseId,
                    holdRegedit: false,
                    afterHubStart: (automation, pid) => CheckHubToolRows(automation, pid, OldCaseId + ".hub.rows.before", gpeditInstalled: false),
                    afterRelaunch: (automation, pid) => CheckHubToolRows(automation, pid, OldCaseId + ".after.hub.rows", gpeditInstalled: true));

                CheckLayoutAfterUpdate(config, run, inputs, keepPath, keepHash);
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record(OldCaseId + ".exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallEvidence.RemoveStagingSince(installerStaging, stagingBefore);
            InstallerChecks.Duration(OldCaseId, stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>Before the update: no BwcGpedit.exe, no third shortcut, a file list without it, the old Hub version.</summary>
    private static void CheckOldLayoutBeforeUpdate(InstallerRun run, string oldVersion)
    {
        InstallerChecks.Record(
            OldCaseId + ".a.noGpeditExe",
            !File.Exists(run.GpeditExe),
            File.Exists(run.GpeditExe) ? InstallerRun.GpeditExeName + " is in the install folder of the old layout." : InstallerRun.GpeditExeName + " is not in the install folder.");

        string gpeditShortcut = InstallerRun.ProductShortcuts.Single(shortcut => shortcut.ExeName == InstallerRun.GpeditExeName).Name;
        InstallerChecks.Record(
            OldCaseId + ".a.gpeditShortcutAbsent",
            !File.Exists(Path.Combine(run.ShortcutDir, gpeditShortcut)),
            "The shortcut '" + gpeditShortcut + "' " + (File.Exists(Path.Combine(run.ShortcutDir, gpeditShortcut)) ? "exists" : "does not exist") + " before the update.");

        string[] listed = ReadManifest(run);
        bool manifestOk = !listed.Contains(InstallerRun.GpeditExeName, StringComparer.OrdinalIgnoreCase)
            && new[] { InstallerRun.HubExeName, InstallerRun.RegeditExeName, InstallerRun.UpdaterExeName }
                .All(name => listed.Contains(name, StringComparer.OrdinalIgnoreCase));
        InstallerChecks.Record(
            OldCaseId + ".a.manifest",
            manifestOk,
            $"{ManifestFileName} lists {listed.Length} file(s); it must name the Hub, Registry Editor and updater exes and not {InstallerRun.GpeditExeName}.");

        string? hubVersion = InstallerRun.VersionOf(run.HubExe);
        InstallerChecks.Record(
            OldCaseId + ".a.hubVersion",
            hubVersion == oldVersion,
            $"The old {InstallerRun.HubExeName} reports {hubVersion}, expected {oldVersion}.");
    }

    /// <summary>After the update: three shortcuts, BwcGpedit.exe present and starting, data kept.</summary>
    private static void CheckLayoutAfterUpdate(E2EConfig config, InstallerRun run, InstallerInputs inputs, string keepPath, string keepHash)
    {
        IReadOnlyList<InstallerRun.ProductShortcut> table = InstallerRun.ProductShortcuts;
        string[] found = Directory.Exists(run.ShortcutDir)
            ? Directory.GetFiles(run.ShortcutDir, "*.lnk", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(run.ShortcutDir, path)).ToArray()
            : [];
        bool exactSet = found.Length == table.Count
            && table.All(shortcut => found.Contains(shortcut.Name, StringComparer.OrdinalIgnoreCase));
        InstallerChecks.Record(
            OldCaseId + ".after.shortcuts",
            exactSet,
            $"{found.Length} shortcut(s) after the update: [{string.Join(", ", found)}]; expected exactly [{string.Join(", ", table.Select(shortcut => shortcut.Name))}].");
        InstallerChecks.Record(OldCaseId + ".after.targets", ShortcutTargetsAre(run, table, out string targetNotes), targetNotes);
        InstallerChecks.Record(OldCaseId + ".after.aumid", ShortcutAumidsAre(run, out string aumidNotes, table), aumidNotes);
        InstallerChecks.Record(OldCaseId + ".after.icons", ShortcutIconsAre(run, out string iconNotes, table), iconNotes);

        string? gpeditVersion = InstallerRun.VersionOf(run.GpeditExe);
        InstallerChecks.Record(
            OldCaseId + ".after.gpedit.version",
            gpeditVersion == inputs.VersionB,
            gpeditVersion is null
                ? InstallerRun.GpeditExeName + " is missing from the install folder after the update."
                : $"The installed {InstallerRun.GpeditExeName} reports {gpeditVersion}, expected {inputs.VersionB}.");

        string[] listed = ReadManifest(run);
        InstallerChecks.Record(
            OldCaseId + ".after.manifest",
            new[] { InstallerRun.HubExeName, InstallerRun.RegeditExeName, InstallerRun.GpeditExeName, InstallerRun.UpdaterExeName }
                .All(name => listed.Contains(name, StringComparer.OrdinalIgnoreCase)),
            $"{ManifestFileName} lists {listed.Length} file(s); it must name all four exes.");

        bool keptExists = File.Exists(keepPath);
        InstallerChecks.Record(
            OldCaseId + ".after.userData",
            keptExists && Hash(keepPath) == keepHash,
            keptExists ? "The seeded user file is unchanged (same SHA-256)." : "The seeded user file is gone after the update.");

        CheckGpeditStarts(config, run);
    }

    /// <summary>Starts BwcGpedit.exe from the install folder and requires a live process with the editor's window.</summary>
    private static void CheckGpeditStarts(E2EConfig config, InstallerRun run)
    {
        if (!File.Exists(run.GpeditExe))
        {
            InstallerChecks.Record(OldCaseId + ".after.gpedit.starts", false, InstallerRun.GpeditExeName + " is missing, so it cannot start.");
            return;
        }

        Process gpedit = StartGpedit(config, run, OldCaseId);
        using var automation = new UIA3Automation();
        bool alive = !gpedit.HasExited;
        AutomationElement? window = alive ? HubUi.FindWindowInProcess(automation, gpedit.Id, GpeditName, DialogTimeout) : null;
        InstallerChecks.Record(
            OldCaseId + ".after.gpedit.starts",
            alive && window is not null,
            alive
                ? (window is null ? $"{InstallerRun.GpeditExeName} (pid {gpedit.Id}) ran but showed no '{GpeditName}' window." : $"{InstallerRun.GpeditExeName} (pid {gpedit.Id}) started and showed its '{GpeditName}' window.")
                : $"{InstallerRun.GpeditExeName} exited with code {gpedit.ExitCode} right after it started.");
    }

    /// <summary>
    /// The Hub lists the two tools by row id and shows each one's install status. The Registry Editor is installed in both
    /// layouts; the Local Group Policy Editor is "Not installed" before the update and "Installed" after.
    /// </summary>
    private static void CheckHubToolRows(UIA3Automation automation, int hubPid, string checkId, bool gpeditInstalled)
    {
        var notes = new List<string>();
        bool all = true;
        foreach ((string rowId, bool installed) in new[] { ("ToolRow_regedit", true), ("ToolRow_gpedit", gpeditInstalled) })
        {
            AutomationElement? row = HubUi.FindInProcess(automation, hubPid, rowId, ElementTimeout);
            string[] texts = row is null
                ? []
                : row.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)).Select(text => text.Name ?? string.Empty).ToArray();
            string expected = installed ? InstalledText : NotInstalledText;
            bool ok = row is not null && texts.Contains(expected, StringComparer.Ordinal);
            all &= ok;
            notes.Add(row is null ? $"{rowId} was not found" : $"{rowId} cells [{string.Join(" | ", texts)}], expected '{expected}'");
        }

        InstallerChecks.Record(checkId, all, string.Join("; ", notes) + ".");
    }

    /// <summary>Every shortcut starts its own exe in the install folder.</summary>
    private static bool ShortcutTargetsAre(InstallerRun run, IReadOnlyList<InstallerRun.ProductShortcut> table, out string notes)
    {
        var lines = new List<string>();
        bool all = true;
        foreach ((string name, _, string exeName) in table)
        {
            string path = Path.Combine(run.ShortcutDir, name);
            string expected = Path.Combine(run.InstallDir, exeName);
            try
            {
                string target = MachineNative.ShortcutTarget(path);
                bool same = string.Equals(Path.GetFullPath(target), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
                lines.Add($"{name}: '{target}', expected '{expected}'");
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

    /// <summary>The relative paths in the install folder's installed-files list, or none when it cannot be read.</summary>
    private static string[] ReadManifest(InstallerRun run)
    {
        string path = Path.Combine(run.InstallDir, ManifestFileName);
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray()
            : [];
    }
}
