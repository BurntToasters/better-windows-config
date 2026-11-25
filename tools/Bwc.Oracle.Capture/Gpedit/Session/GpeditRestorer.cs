using System.Diagnostics;

namespace Bwc.Oracle.Capture.Gpedit.Session;

internal sealed class RestoreResult
{
    internal bool NothingToRestore { get; set; }

    internal List<string> Errors { get; } = new();

    internal bool Ok => Errors.Count == 0;
}

// Undoes a session from the manifest on disk: close the mmc.exe it started, make GroupPolicy and the MMC state equal to the backup,
// delete the templates it copied, delete the four registry keys, verify all of it, and only then delete the manifest.
// Idempotent. The same code runs from the finally block, the console control handler and `gpedit restore`.
internal static class GpeditRestorer
{
    private static readonly object RestoreLock = new();

    internal static RestoreResult Restore(GpeditPaths paths, Action<string> log)
    {
        lock (RestoreLock)
        {
            var result = new RestoreResult();
            if (!File.Exists(paths.ManifestPath))
            {
                result.NothingToRestore = true;
                FileOps.DeleteFile(paths.ManifestPath + ".tmp");
                return result;
            }

            Manifest manifest;
            try
            {
                manifest = Manifest.Read(paths.ManifestPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"manifest {paths.ManifestPath} cannot be read ({ex.GetType().Name}: {ex.Message}). The backup copies are in {paths.BackupDir}.");
                return result;
            }

            if (manifest.Sandbox != paths.Sandbox
                || !SamePath(manifest.GroupPolicy.Root, paths.GroupPolicyDir)
                || !SamePath(manifest.MmcState.Root, paths.MmcStatePath))
            {
                result.Errors.Add("the manifest was written for other folders (another user, machine or the self test). Restore it where it was written.");
                return result;
            }

            RunSteps(paths, manifest, log, result);
            if (result.Ok)
            {
                FileOps.DeleteFile(paths.ManifestPath);
                FileOps.DeleteFile(paths.ManifestPath + ".tmp");
                log("restore: verified clean, manifest deleted");
            }

            return result;
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void RunSteps(GpeditPaths paths, Manifest manifest, Action<string> log, RestoreResult result)
    {
        List<string> errors = result.Errors;

        void Step(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors.Add($"{what}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Step("close mmc", () => CloseStarted(manifest, log, errors));

        log($"restore: GroupPolicy ({manifest.GroupPolicy.Kind})");
        Step("GroupPolicy", () => manifest.GroupPolicy.Restore(paths.GroupPolicyDir, Manifest.BackupLocation(paths, "GroupPolicy"), errors));
        log($"restore: MMC state ({manifest.MmcState.Kind})");
        Step("MMC state", () => manifest.MmcState.Restore(paths.MmcStatePath, Manifest.BackupLocation(paths, "MmcState"), errors));

        foreach (TemplateEntry template in manifest.Templates)
        {
            string path = Path.Combine(paths.PolicyDefinitionsDir, template.RelativePath);
            Step($"template {template.RelativePath}", () =>
            {
                if (!File.Exists(path))
                {
                    return;
                }

                if (string.Equals(FileOps.Sha256(path), template.Sha256, StringComparison.Ordinal))
                {
                    FileOps.DeleteFile(path);
                    log($"restore: deleted template {path}");
                }
                else
                {
                    errors.Add($"template {path} no longer matches what the session copied. It was left in place. Delete it by hand, then run `gpedit restore` again.");
                }
            });
        }

        foreach (string key in manifest.RegistryKeys)
        {
            RegistryTarget? target = paths.RegistryTargets.FirstOrDefault(t => string.Equals(t.Display, key, StringComparison.Ordinal));
            if (target is null)
            {
                errors.Add($"manifest names registry key {key}, which this session may not delete.");
                continue;
            }

            Step($"registry {key}", () => GpeditRegistry.Delete(target));
        }

        log("restore: verifying");
        Step("verify GroupPolicy", () =>
        {
            errors.AddRange(TreeSnapshot.Compare(manifest.GroupPolicy, TreeSnapshot.Scan("GroupPolicy", paths.GroupPolicyDir)));
        });
        Step("verify MMC state", () =>
        {
            errors.AddRange(TreeSnapshot.Compare(manifest.MmcState, TreeSnapshot.Scan("MmcState", paths.MmcStatePath)));
        });
        foreach (TemplateEntry template in manifest.Templates)
        {
            string path = Path.Combine(paths.PolicyDefinitionsDir, template.RelativePath);
            if (File.Exists(path))
            {
                errors.Add($"verify: template {path} is still there");
            }
        }

        foreach (RegistryTarget target in paths.RegistryTargets)
        {
            Step($"verify registry {target.Display}", () =>
            {
                if (GpeditRegistry.Exists(target))
                {
                    errors.Add($"verify: registry key {target.Display} still exists");
                }
            });
        }
    }

    // Closes only the mmc.exe processes the session recorded (pid and start time must both match).
    private static void CloseStarted(Manifest manifest, Action<string> log, List<string> errors)
    {
        foreach (StartedProcess recorded in manifest.Processes)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(recorded.Pid);
                if (process.HasExited
                    || !string.Equals(process.ProcessName, recorded.Name, StringComparison.OrdinalIgnoreCase)
                    || process.StartTime.ToUniversalTime().Ticks != recorded.StartUtcTicks)
                {
                    continue;
                }

                log($"restore: closing {recorded.Name} pid {recorded.Pid}");
                process.CloseMainWindow();
                if (!process.WaitForExit(5000))
                {
                    log($"restore: {recorded.Name} pid {recorded.Pid} did not close in 5 seconds, ending it");
                    process.Kill();
                }

                if (!process.WaitForExit(10000))
                {
                    errors.Add($"{recorded.Name} pid {recorded.Pid} did not exit.");
                }
            }
            catch (ArgumentException)
            {
                // No process with that id: it already exited.
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                errors.Add($"close {recorded.Name} pid {recorded.Pid}: {ex.Message}");
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
