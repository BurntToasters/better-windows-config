using System.Diagnostics;
using System.Security.Cryptography;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Installer;

/// <summary>
/// Audit fixes of the installer (docs/failure-modes/installer.md M60 to M67, installer-e2e.md 97 to 106): a first
/// install that fails after the move-in must leave nothing behind, and the uninstaller removes the imported policy
/// templates, keeps the policy backups, and never follows a link. Everything runs in the unelevated per-user sandbox.
/// The uninstaller's data root is a folder inside the run's folder (<see cref="InstallerRun.ProgramDataRoot"/>),
/// never the real %ProgramData%.
/// </summary>
public sealed partial class InstallerUpdateTests
{
    private const string StoreName = "PolicyDefinitions";
    private const string DataRootName = "BetterWindowsConfig";

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_FirstInstallFailure_RollsBackAndRerunSucceeds()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunFirstInstallFailureCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Uninstall_RemovesTemplatesKeepsBackupsAndOthers()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUninstallDataCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Uninstall_TemplatesOnlyRemovesTheDataRoot()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUninstallCleanCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Uninstall_LinkedStoreFolderIsNotFollowed()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunLinkedStoreCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Uninstall_LinkedLanguageFolderSkipsTheStep()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunLinkedLanguageCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_UpgradeFailure_RollsBackPreviousVersionIntact()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUpgradeFailureCase(inputs);
    }

    [TestMethod]
    [TestCategory("Installer")]
    [TestCategory("E2EOnly")]
    public void Installer_Upgrade_RemovesStaleManifestFilesAndKeepsUnlisted()
    {
        InstallerInputs? inputs = InstallerInputs.TryRead();
        if (inputs is null)
        {
            Assert.Inconclusive("The installer E2E is started by scripts/test-installer-e2e.js.");
            return;
        }

        RunUpgradeStaleFilesCase(inputs);
    }

    /// <summary>
    /// A folder where the last shortcut file goes makes CreateShortCut fail after the payload is in place, so the first
    /// install rolls back. The install folder must be gone (it held empty payload folders before the fix), and a second
    /// run of the same setup must succeed (the leftover folder made BwcCheckInstDir refuse it).
    /// </summary>
    private static void RunFirstInstallFailureCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "firstfail");
            string blocker = Path.Combine(run.ShortcutDir, InstallerRun.ProductShortcuts[^1].Name);
            Directory.CreateDirectory(blocker);

            int exit = run.RunSetup(inputs.InstallerA, SetupTimeout);
            InstallerChecks.Record(
                "firstfail.setup.exit",
                exit > 0,
                $"The setup with a folder in the place of a shortcut file exited {exit}; a rolled back install is a non-zero exit (-1 is a timeout).");
            InstallerChecks.Record(
                "firstfail.installDir",
                !Directory.Exists(run.InstallDir),
                Directory.Exists(run.InstallDir)
                    ? "The install folder was left behind. Left: " + Leftovers(run.InstallDir)
                    : "The failed first install left no install folder.");
            InstallerChecks.Record(
                "firstfail.noKey",
                !run.UninstallKeyExists(),
                "A rolled back first install leaves no uninstall key.");
            InstallerChecks.Record(
                "firstfail.noShortcuts",
                run.ShortcutCount() == 0,
                $"{run.ShortcutCount()} shortcut file(s) remain after the rollback.");
            InstallerChecks.Record(
                "firstfail.noLeftovers",
                !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
                "No <install folder>.new or .old is left after the rollback.");

            Directory.Delete(blocker, recursive: true);
            _ = InstallSilently(run, inputs.InstallerA, inputs.VersionA, "firstfail.rerun");
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("firstfail.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("firstfail", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// Installs A, blocks a shortcut with a directory so B fails after BwcCommit, and checks that BwcRollback puts every
    /// file of A back with its original hash and version before a rerun of B succeeds.
    /// </summary>
    private static void RunUpgradeFailureCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);
        RequireFile(inputs.InstallerB);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "upfail");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "upfail.a"))
            {
                Dictionary<string, string> before = HashTree(run.InstallDir);
                string blocker = Path.Combine(run.ShortcutDir, InstallerRun.ProductShortcuts[^1].Name);
                File.Delete(blocker);
                Directory.CreateDirectory(blocker);

                int exit = run.RunSetup(inputs.InstallerB, SetupTimeout);
                InstallerChecks.Record(
                    "upfail.setup.exit",
                    exit > 0,
                    $"The failed upgrade exited {exit}; a rolled back upgrade is a non-zero exit (-1 is a timeout).");
                InstallerChecks.Record(
                    "upfail.noLeftovers",
                    !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
                    "No <install folder>.new or .old stays after the upgrade rollback.");
                string? exeVersion = InstallerRun.VersionOf(run.HubExe);
                InstallerChecks.Record(
                    "upfail.versionRestored",
                    exeVersion == inputs.VersionA,
                    $"Restored Hub version is '{exeVersion}', expected '{inputs.VersionA}'.");
                string? displayVersion = run.ReadUninstallValue("DisplayVersion");
                InstallerChecks.Record(
                    "upfail.displayVersion",
                    displayVersion == inputs.VersionA,
                    $"Uninstall DisplayVersion is '{displayVersion}', expected '{inputs.VersionA}'.");
                Dictionary<string, string> after = HashTree(run.InstallDir);
                bool filesRestored = after.Count == before.Count
                    && before.All(pair => after.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
                InstallerChecks.Record(
                    "upfail.filesRestored",
                    filesRestored,
                    $"{after.Count} of {before.Count} file(s) of version A match SHA-256 after rollback.");

                Directory.Delete(blocker, recursive: true);
                _ = InstallSilently(run, inputs.InstallerB, inputs.VersionB, "upfail.rerun");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("upfail.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("upfail", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// Installs A, records a nested stale file in installed-files.txt plus an unlisted user file, upgrades to B, and
    /// checks that the listed stale file and its empty parent folders are removed while the unlisted file stays.
    /// </summary>
    private static void RunUpgradeStaleFilesCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);
        RequireFile(inputs.InstallerB);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "upstale");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "upstale.a"))
            {
                string staleRelative = @"stale-old\nested\removed.dat";
                string staleFile = Path.Combine(run.InstallDir, staleRelative);
                WriteBytes(staleFile, 128);
                string manifest = Path.Combine(run.InstallDir, "installed-files.txt");
                File.AppendAllText(manifest, staleRelative + "\r\n");

                string userFile = Path.Combine(run.InstallDir, "user-keep.txt");
                WriteBytes(userFile, 128);
                string userHash = Hash(userFile);

                if (InstallSilently(run, inputs.InstallerB, inputs.VersionB, "upstale.b"))
                {
                    InstallerChecks.Record(
                        "upstale.staleFileGone",
                        !File.Exists(staleFile),
                        File.Exists(staleFile)
                            ? "The stale file listed in the previous manifest was left behind."
                            : "The stale file listed in the previous manifest was removed.");
                    bool dirsGone = !Directory.Exists(Path.Combine(run.InstallDir, "stale-old", "nested"))
                        && !Directory.Exists(Path.Combine(run.InstallDir, "stale-old"));
                    InstallerChecks.Record(
                        "upstale.staleDirsGone",
                        dirsGone,
                        dirsGone
                            ? "Empty parent folders of the stale file were removed."
                            : "An empty parent folder of the stale file was left behind.");
                    InstallerChecks.Record(
                        "upstale.unlistedKept",
                        File.Exists(userFile) && Hash(userFile) == userHash,
                        "The unlisted file remained with the same SHA-256.");
                    InstallerChecks.Record(
                        "upstale.noLeftovers",
                        !Directory.Exists(run.InstallDir + ".new") && !Directory.Exists(run.InstallDir + ".old"),
                        "No <install folder>.new or .old stays after the upgrade.");
                }
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("upstale.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("upstale", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// Templates and backups side by side. The uninstall removes every .admx and .adml, the language folder it empties, and
    /// nothing else: an unrelated file in the store and in a language folder stays (with its folders), and the backups stay
    /// byte for byte.
    /// </summary>
    private static void RunUninstallDataCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "uninstallData");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "uninstallData.a"))
            {
                string dataRoot = Path.Combine(run.ProgramDataRoot, DataRootName);
                string store = Path.Combine(dataRoot, StoreName);
                string backup = Path.Combine(dataRoot, "gpedit", "backup");
                string[] templates =
                [
                    Path.Combine(store, "alpha.admx"),
                    Path.Combine(store, "BETA.ADMX"),
                    Path.Combine(store, "en-US", "alpha.adml"),
                    Path.Combine(store, "en-US", "BETA.ADML"),
                    Path.Combine(store, "de-DE", "alpha.adml"),
                ];
                string[] others =
                [
                    Path.Combine(store, "readme.txt"),
                    Path.Combine(store, "en-US", "notes.txt"),
                ];
                foreach (string path in templates.Concat(others))
                {
                    WriteBytes(path, 300);
                }

                WriteBytes(Path.Combine(backup, "2026-11-18T10-00-00", "machine", "Registry.pol"), 4096);
                WriteBytes(Path.Combine(backup, "2026-11-18T10-00-00", "user", "Registry.pol"), 777);
                WriteBytes(Path.Combine(backup, "index.json"), 64);
                Dictionary<string, string> backupsBefore = HashTree(backup);
                Dictionary<string, string> othersBefore = HashFiles(others);

                bool gone = run.Uninstall(UninstallTimeout);
                InstallerChecks.Record("uninstallData.files", gone, gone ? "The install folder is gone." : "Left: " + Leftovers(run.InstallDir));
                InstallerChecks.Record(
                    "uninstallData.templatesGone",
                    templates.All(path => !File.Exists(path)),
                    "Every .admx and .adml is removed. Still there: " + string.Join(", ", templates.Where(File.Exists).Select(path => Path.GetRelativePath(dataRoot, path))) + ".");
                InstallerChecks.Record(
                    "uninstallData.foldersEmptyOnly",
                    !Directory.Exists(Path.Combine(store, "de-DE")) && Directory.Exists(Path.Combine(store, "en-US")) && Directory.Exists(store),
                    "The language folder that held only templates is removed; the one with notes.txt and the store folder stay.");
                InstallerChecks.Record(
                    "uninstallData.othersKept",
                    HashFiles(others).OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(othersBefore.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
                    "readme.txt and notes.txt are still there with the same bytes.");
                Dictionary<string, string> backupsAfter = HashTree(backup);
                InstallerChecks.Record(
                    "uninstallData.backupsKept",
                    backupsAfter.Count == backupsBefore.Count
                        && backupsBefore.All(pair => backupsAfter.TryGetValue(pair.Key, out string? hash) && hash == pair.Value),
                    $"{backupsAfter.Count} of {backupsBefore.Count} backup file(s) remain with the same SHA-256.");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("uninstallData.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("uninstallData", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>Only templates in the store: the store, the language folders, and then the empty data root all go.</summary>
    private static void RunUninstallCleanCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "uninstallClean");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "uninstallClean.a"))
            {
                string dataRoot = Path.Combine(run.ProgramDataRoot, DataRootName);
                string store = Path.Combine(dataRoot, StoreName);
                WriteBytes(Path.Combine(store, "alpha.admx"), 200);
                WriteBytes(Path.Combine(store, "en-US", "alpha.adml"), 200);
                WriteBytes(Path.Combine(store, "fr-FR", "alpha.adml"), 200);

                bool gone = run.Uninstall(UninstallTimeout);
                InstallerChecks.Record("uninstallClean.files", gone, gone ? "The install folder is gone." : "Left: " + Leftovers(run.InstallDir));
                InstallerChecks.Record(
                    "uninstallClean.rootRemoved",
                    !Directory.Exists(dataRoot) && Directory.Exists(run.ProgramDataRoot),
                    Directory.Exists(dataRoot)
                        ? "The data root was left behind. Left: " + Leftovers(dataRoot)
                        : "The store, its language folders, and the empty data root are gone; the stand-in %ProgramData% folder itself is untouched.");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("uninstallClean.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            InstallerChecks.Duration("uninstallClean", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>The store folder is a junction to another folder of the run: nothing behind the junction is deleted.</summary>
    private static void RunLinkedStoreCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        string? link = null;
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "uninstallLinkStore");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "uninstallLinkStore.a"))
            {
                string target = Path.Combine(run.Root, "linktarget");
                string[] behind = [Path.Combine(target, "x.admx"), Path.Combine(target, "en-US", "x.adml"), Path.Combine(target, "keep.txt")];
                foreach (string path in behind)
                {
                    WriteBytes(path, 150);
                }

                string dataRoot = Path.Combine(run.ProgramDataRoot, DataRootName);
                Directory.CreateDirectory(dataRoot);
                link = Path.Combine(dataRoot, StoreName);
                CreateJunction(link, target);
                Dictionary<string, string> before = HashTree(target);

                bool gone = run.Uninstall(UninstallTimeout);
                InstallerChecks.Record("uninstallLinkStore.files", gone, gone ? "The install folder is gone." : "Left: " + Leftovers(run.InstallDir));
                Dictionary<string, string> after = HashTree(target);
                InstallerChecks.Record(
                    "uninstallLinkStore.targetUntouched",
                    after.Count == before.Count && before.All(pair => after.TryGetValue(pair.Key, out string? hash) && hash == pair.Value),
                    $"{after.Count} of {before.Count} file(s) behind the junction remain with the same SHA-256.");
                InstallerChecks.Record(
                    "uninstallLinkStore.linkKept",
                    IsJunction(link),
                    "The store folder is still the junction.");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("uninstallLinkStore.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            RemoveLink(link);
            InstallerChecks.Duration("uninstallLinkStore", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>
    /// A language folder is a junction. The whole step is skipped (installer.md M64), so the plain .admx also stays.
    /// </summary>
    private static void RunLinkedLanguageCase(InstallerInputs inputs)
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        RequireFile(inputs.InstallerA);

        int start = InstallerChecks.Count;
        var stopwatch = Stopwatch.StartNew();
        string? link = null;
        try
        {
            using InstallerRun run = InstallerRun.Create(config, "uninstallLinkLang");
            if (InstallSilently(run, inputs.InstallerA, inputs.VersionA, "uninstallLinkLang.a"))
            {
                string target = Path.Combine(run.Root, "langtarget");
                string behind = Path.Combine(target, "a.adml");
                WriteBytes(behind, 150);
                string store = Path.Combine(run.ProgramDataRoot, DataRootName, StoreName);
                string plain = Path.Combine(store, "a.admx");
                WriteBytes(plain, 150);
                link = Path.Combine(store, "en-US");
                CreateJunction(link, target);
                string plainHash = Hash(plain);
                string behindHash = Hash(behind);

                bool gone = run.Uninstall(UninstallTimeout);
                InstallerChecks.Record("uninstallLinkLang.files", gone, gone ? "The install folder is gone." : "Left: " + Leftovers(run.InstallDir));
                InstallerChecks.Record(
                    "uninstallLinkLang.skipped",
                    File.Exists(plain) && Hash(plain) == plainHash && File.Exists(behind) && Hash(behind) == behindHash && IsJunction(link),
                    "With a junction as a language folder, a.admx in the store, the junction, and a.adml behind it all stay unchanged.");
            }
        }
        catch (Exception ex)
        {
            InstallerChecks.Record("uninstallLinkLang.exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            RemoveLink(link);
            InstallerChecks.Duration("uninstallLinkLang", stopwatch.ElapsedMilliseconds);
        }

        AssertNoFailures(start);
    }

    /// <summary>The real %ProgramData%\BetterWindowsConfig. Only read, to show an uninstall without the seam skips it.
    /// </summary>
    private static string RealProgramDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DataRootName);

    /// <summary>Names, sizes, and write times of everything below a folder, as one text. Read only.</summary>
    private static string ListFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "(absent)";
        }

        return string.Join(
            "\n",
            Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return Path.GetRelativePath(folder, path) + "|" + (info.Attributes.HasFlag(FileAttributes.Directory) ? "dir" : info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "|" + info.LastWriteTimeUtc.Ticks;
                }));
    }

    private static void WriteBytes(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(length));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static Dictionary<string, string> HashFiles(IEnumerable<string> paths) =>
        paths.Where(File.Exists).ToDictionary(path => path, Hash, StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA-256 of every file below a folder, by relative path.</summary>
    private static Dictionary<string, string> HashTree(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(folder, path), Hash, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes a junction with mklink /J, which needs no elevation.</summary>
    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("cmd.exe did not start.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !IsJunction(link))
        {
            throw new InvalidOperationException("mklink /J failed (" + process.ExitCode + "): " + output.Trim());
        }
    }

    private static bool IsJunction(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Removes a junction as a link (no recursion), so the run's own recursive delete never meets it.</summary>
    private static void RemoveLink(string? link)
    {
        if (link is null || !Directory.Exists(link) || !IsJunction(link))
        {
            return;
        }

        try
        {
            Directory.Delete(link, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InstallerChecks.Record("cleanup.link", false, "The junction could not be removed: " + link + " (" + ex.Message + ")");
        }
    }
}
