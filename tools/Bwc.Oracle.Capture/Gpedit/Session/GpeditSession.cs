using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// Wraps every gpedit capture that lets classic gpedit save into the real local group policy (docs/failure-modes/oracle-gpedit-session.md).
// Order: refuse unless elevated, interactive and no manifest; take the GUI mutex; back up GroupPolicy and the MMC state; write the manifest;
// copy the capture templates; run the body; always restore from the manifest, then verify, then delete the manifest.
internal static class GpeditSession
{
    private const uint CtrlC = 0;
    private const uint CtrlBreak = 1;

    private delegate bool CtrlHandler(uint ctrlType);

    private static CtrlHandler? rootedHandler;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(CtrlHandler? handler, bool add);

    internal static void Run(string name, Action<GpeditContext> body) => Run(name, TemplateSet.Empty, body);

    internal static void Run(string name, TemplateSet templates, Action<GpeditContext> body) =>
        RunCore(GpeditPaths.Real(RepoPaths.Locate()), name, templates, body, simulateCrash: false);

    // simulateCrash skips the restore after the body, as a killed process would. Only the self test passes true.
    internal static void RunCore(GpeditPaths paths, string name, TemplateSet templates, Action<GpeditContext> body, bool simulateCrash)
    {
        Preflight(paths, templates);
        GuiLock.Run(() => RunLocked(paths, name, templates, body, simulateCrash));
    }

    // Read-only checks that decide whether a session may start. Also used by `probe --dry-run` through Describe.
    internal static void Preflight(GpeditPaths paths, TemplateSet templates)
    {
        if (!paths.Sandbox)
        {
            if (!SessionGuards.IsElevated())
            {
                throw new GpeditRefusal(SessionGuards.ElevationMessage);
            }

            string? problem = SessionGuards.InteractiveProblem();
            if (problem is not null)
            {
                throw new GpeditRefusal(problem);
            }
        }

        RequireNoManifest(paths);
        RequireTemplatesFree(paths, templates);
    }

    internal static void RequireNoManifest(GpeditPaths paths)
    {
        if (File.Exists(paths.ManifestPath))
        {
            string when = File.GetLastWriteTime(paths.ManifestPath).ToString("yyyy-MM-dd HH:mm:ss");
            throw new GpeditRefusal(
                $"refused: a backup manifest exists ({paths.ManifestPath}, written {when}). A previous capture did not finish. " +
                "Run `gpedit restore` from an administrator terminal first. `gpedit status` shows what it holds.");
        }
    }

    internal static void RequireTemplatesFree(GpeditPaths paths, TemplateSet templates)
    {
        if (templates.Items.Count == 0)
        {
            return;
        }

        if (!Directory.Exists(paths.PolicyDefinitionsDir))
        {
            throw new GpeditRefusal($"refused: {paths.PolicyDefinitionsDir} does not exist. The session does not create it.");
        }

        if (templates.Items.Any(i => i.RelativePath.Contains('\\', StringComparison.Ordinal)) && !Directory.Exists(paths.PolicyDefinitionsEnUsDir))
        {
            throw new GpeditRefusal($"refused: {paths.PolicyDefinitionsEnUsDir} does not exist. The session does not create it.");
        }

        foreach (TemplateSet.Item item in templates.Items)
        {
            string dst = Path.Combine(paths.PolicyDefinitionsDir, item.RelativePath);
            if (File.Exists(dst) || Directory.Exists(dst))
            {
                throw new GpeditRefusal(
                    $"refused: {dst} already exists and the session cannot prove it is its own. It is never overwritten or deleted. " +
                    "If it is a leftover of the capture templates, delete it by hand and start again.");
            }

            if (!File.Exists(item.SourcePath))
            {
                throw new GpeditRefusal($"refused: capture template source {item.SourcePath} is missing.");
            }
        }
    }

    private static void RunLocked(GpeditPaths paths, string name, TemplateSet templates, Action<GpeditContext> body, bool simulateCrash)
    {
        Directory.CreateDirectory(paths.OutputDir);
        RequireNoManifest(paths);
        RequireTemplatesFree(paths, templates);

        using var cts = new CancellationTokenSource();
        rootedHandler = type =>
        {
            cts.Cancel();
            if (type is CtrlC or CtrlBreak)
            {
                Console.WriteLine($"{name}: Ctrl+C received, finishing the current step and restoring");
                return true;
            }

            Console.WriteLine($"{name}: console closing, restoring now");
            try
            {
                GpeditRestorer.Restore(paths, Console.WriteLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name}: restore from the control handler failed: {ex.Message}");
            }

            return true;
        };
        SetConsoleCtrlHandler(rootedHandler, add: true);

        try
        {
            Manifest manifest = Backup(paths, name, templates);
            Console.WriteLine($"{name}: backup verified, manifest written to {paths.ManifestPath}");

            Exception? failure = null;
            RestoreResult? restored = null;
            try
            {
                InstallTemplates(paths, manifest, templates);
                var context = new GpeditContext(paths, name, manifest, cts.Token);
                body(context);
                cts.Token.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (simulateCrash)
                {
                    Console.WriteLine($"{name}: self test: restore skipped on purpose");
                }
                else
                {
                    restored = GpeditRestorer.Restore(paths, Console.WriteLine);
                }
            }

            if (restored is { Ok: false })
            {
                if (failure is not null)
                {
                    Console.Error.WriteLine($"{name}: the capture also failed: {failure.Message}");
                }

                throw new GpeditRestoreException(restored.Errors);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            SetConsoleCtrlHandler(rootedHandler, add: false);
            rootedHandler = null;
        }
    }

    // Reads GroupPolicy and the MMC state, copies both into the backup folder, proves every copy by hash, checks the source did not change
    // while copying, and writes the manifest last. Nothing outside artifacts/ is changed here.
    private static Manifest Backup(GpeditPaths paths, string name, TemplateSet templates)
    {
        try
        {
            if (Directory.Exists(paths.BackupDir))
            {
                FileOps.DeleteTree(paths.BackupDir);
            }

            Directory.CreateDirectory(paths.BackupDir);

            TreeSnapshot groupPolicy = TreeSnapshot.Scan("GroupPolicy", paths.GroupPolicyDir);
            TreeSnapshot mmcState = TreeSnapshot.Scan("MmcState", paths.MmcStatePath);
            groupPolicy.CopyToBackup(Manifest.BackupLocation(paths, "GroupPolicy"));
            mmcState.CopyToBackup(Manifest.BackupLocation(paths, "MmcState"));

            var changed = TreeSnapshot.Compare(groupPolicy, TreeSnapshot.Scan("GroupPolicy", paths.GroupPolicyDir));
            changed.AddRange(TreeSnapshot.Compare(mmcState, TreeSnapshot.Scan("MmcState", paths.MmcStatePath)));
            if (changed.Count > 0)
            {
                throw new InvalidOperationException("the source changed while it was being backed up, so the backup is not trusted: " + string.Join("; ", changed));
            }

            var manifest = new Manifest
            {
                Command = name,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Sandbox = paths.Sandbox,
                GroupPolicy = groupPolicy,
                MmcState = mmcState,
                RegistryKeys = paths.RegistryTargets.Select(t => t.Display).ToList(),
            };
            foreach (TemplateSet.Item item in templates.Items)
            {
                manifest.Templates.Add(new TemplateEntry
                {
                    RelativePath = item.RelativePath,
                    Sha256 = FileOps.Sha256(item.SourcePath),
                    Size = new FileInfo(item.SourcePath).Length,
                });
            }

            manifest.Write(paths.ManifestPath);
            return manifest;
        }
        catch
        {
            // The live folders were only read, so a half backup is just scratch. Never leave it without a manifest to mislead.
            if (!File.Exists(paths.ManifestPath) && Directory.Exists(paths.BackupDir))
            {
                FileOps.DeleteTree(paths.BackupDir);
            }

            throw;
        }
    }

    private static void InstallTemplates(GpeditPaths paths, Manifest manifest, TemplateSet templates)
    {
        for (int i = 0; i < manifest.Templates.Count; i++)
        {
            TemplateEntry entry = manifest.Templates[i];
            string dst = Path.Combine(paths.PolicyDefinitionsDir, entry.RelativePath);
            FileOps.CopyNew(templates.Items[i].SourcePath, dst);
            if (!string.Equals(FileOps.Sha256(dst), entry.Sha256, StringComparison.Ordinal))
            {
                FileOps.DeleteFile(dst);
                throw new IOException($"template copy {dst} does not match its source hash, it was removed.");
            }

            Console.WriteLine($"{manifest.Command}: installed template {dst}");
        }
    }

}
