using System.Diagnostics;
using Bwc.Oracle.Capture.Gpedit.Probe;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit;

// `Bwc.Oracle.Capture gpedit <subcommand>`: probe, restore, status, selftest. Exit codes: 0 done, 1 failed, 2 refused or bad usage.
internal static class GpeditCommands
{
    internal const string Usage =
        "usage: Bwc.Oracle.Capture gpedit <subcommand>\n" +
        "  probe [--dry-run]  drive classic gpedit.msc and record how it can be automated and how Registry.pol is ordered (probe.json)\n" +
        "  restore            undo an unfinished capture from its backup manifest; says so when there is nothing to restore\n" +
        "  status             print whether a backup manifest exists and what it holds\n" +
        "  selftest           run the session code against a sandbox folder (no administrator rights, no mmc.exe), selftest.json\n" +
        "  probe needs an elevated terminal on the interactive desktop. Take a VM snapshot first.";

    internal static int Run(RepoPaths repo, string[] args)
    {
        string[] rest = args.SkipWhile(a => !string.Equals(a, "gpedit", StringComparison.Ordinal)).Skip(1).ToArray();
        string sub = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
        bool dryRun = rest.Contains("--dry-run", StringComparer.Ordinal);
        GpeditDpi.Ensure();

        try
        {
            switch (sub)
            {
                case "probe":
                    if (dryRun)
                    {
                        ProbeCommand.DryRun(repo);
                    }
                    else
                    {
                        ProbeCommand.Run(repo);
                    }

                    return 0;

                case "restore":
                    return Restore(repo);

                case "status":
                    return Status(repo);

                case "selftest":
                    return SelfTest.Run(repo);

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (GpeditRefusal refusal)
        {
            Console.Error.WriteLine(refusal.Message);
            return 2;
        }
        catch (GpeditRestoreException failure)
        {
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"gpedit {sub}: cancelled. Everything was restored (see the lines above).");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED (gpedit {sub}): {ex}");
            return 1;
        }
    }

    private static int Restore(RepoPaths repo)
    {
        GpeditPaths paths = GpeditPaths.Real(repo);
        if (!File.Exists(paths.ManifestPath))
        {
            Console.WriteLine($"restore: nothing to restore (no manifest at {paths.ManifestPath})");
            return 0;
        }

        if (!SessionGuards.IsElevated())
        {
            throw new GpeditRefusal(SessionGuards.ElevationMessage);
        }

        RestoreResult result = GuiLock.Run(() => GpeditRestorer.Restore(paths, Console.WriteLine));
        if (result.NothingToRestore)
        {
            Console.WriteLine("restore: nothing to restore (the manifest was already gone)");
            return 0;
        }

        if (!result.Ok)
        {
            throw new GpeditRestoreException(result.Errors);
        }

        Console.WriteLine("restore: done");
        return 0;
    }

    private static int Status(RepoPaths repo)
    {
        GpeditPaths paths = GpeditPaths.Real(repo);
        Console.WriteLine($"elevated: {(SessionGuards.IsElevated() ? "yes" : "no")}");
        Console.WriteLine($"manifest: {paths.ManifestPath}");
        if (!File.Exists(paths.ManifestPath))
        {
            Console.WriteLine("backup: none (no manifest). A capture may start.");
            return 0;
        }

        Manifest manifest;
        try
        {
            manifest = Manifest.Read(paths.ManifestPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"backup: manifest EXISTS but cannot be read: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"backup: EXISTS, a capture did not finish. Run `gpedit restore` from an administrator terminal.");
        Console.WriteLine($"  written by: gpedit {manifest.Command} at {manifest.CreatedUtc}");
        Console.WriteLine($"  GroupPolicy {manifest.GroupPolicy.Root}: {Describe(manifest.GroupPolicy)}");
        Console.WriteLine($"  MMC state {manifest.MmcState.Root}: {Describe(manifest.MmcState)}");
        foreach (TemplateEntry template in manifest.Templates)
        {
            string path = Path.Combine(paths.PolicyDefinitionsDir, template.RelativePath);
            Console.WriteLine($"  template {path}: {(File.Exists(path) ? "present" : "not present")}");
        }

        foreach (StartedProcess started in manifest.Processes)
        {
            Console.WriteLine($"  started {started.Name} pid {started.Pid}: {(IsAlive(started) ? "still running" : "not running")}");
        }

        foreach (string key in manifest.RegistryKeys)
        {
            Console.WriteLine($"  registry key deleted on restore: {key}");
        }

        return 0;
    }

    private static string Describe(TreeSnapshot snapshot) => snapshot.Kind switch
    {
        "absent" => "did not exist",
        "file" => "a file, " + snapshot.Entries[0].Size + " bytes",
        _ => $"{snapshot.FileCount} files, {snapshot.Entries.Count(e => e.IsDirectory && e.Path.Length > 0)} subfolders",
    };

    private static bool IsAlive(StartedProcess started)
    {
        try
        {
            using Process process = Process.GetProcessById(started.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started.StartUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
