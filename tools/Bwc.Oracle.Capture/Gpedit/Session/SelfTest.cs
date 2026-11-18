using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// `gpedit selftest`: the session code against a sandbox folder under artifacts/oracle/gpedit/selftest/ and one HKCU key.
// It never touches the real GroupPolicy, PolicyDefinitions or MMC state, never starts mmc.exe and needs no elevation.
// Output: selftest.json, one pass or fail line per case (docs/failure-modes/oracle-gpedit-session.md, section 19).
internal static class SelfTest
{
    private const string AdmxName = "BwcSelftest.admx";
    private const string AdmlName = "en-US\\BwcSelftest.adml";

    internal static int Run(RepoPaths repo)
    {
        GpeditPaths paths = GpeditPaths.ForSandbox(repo);
        var cases = new JsonArray();
        bool allPass = true;

        void Case(string name, Action body)
        {
            string detail = "ok";
            bool pass = true;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                pass = false;
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            Console.WriteLine($"selftest: {(pass ? "PASS" : "FAIL")} {name}{(pass ? string.Empty : " - " + detail)}");
            cases.Add(new JsonObject { ["case"] = name, ["pass"] = pass, ["detail"] = detail });
            allPass &= pass;
        }

        Case("normal run restores everything", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            Before before = Snapshot(paths);
            GpeditSession.RunCore(paths, "selftest", templates, ctx =>
            {
                Expect(File.Exists(Path.Combine(paths.PolicyDefinitionsDir, AdmxName)), "template was not installed during the body");
                Mutate(paths);
            }, simulateCrash: false);
            AssertRestored(paths, before);
        });

        Case("body exception still restores", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            Before before = Snapshot(paths);
            bool threw = false;
            try
            {
                GpeditSession.RunCore(paths, "selftest", templates, ctx =>
                {
                    Mutate(paths);
                    throw new InvalidOperationException("boom");
                }, simulateCrash: false);
            }
            catch (InvalidOperationException ex) when (ex.Message == "boom")
            {
                threw = true;
            }

            Expect(threw, "the body exception did not reach the caller");
            AssertRestored(paths, before);
        });

        Case("folders that did not exist are removed again", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: false, mmcState: false);
            Before before = Snapshot(paths);
            GpeditSession.RunCore(paths, "selftest", templates, ctx =>
            {
                Directory.CreateDirectory(Path.Combine(paths.GroupPolicyDir, "Machine"));
                File.WriteAllText(Path.Combine(paths.GroupPolicyDir, "Machine", "Registry.pol"), "new");
                Directory.CreateDirectory(Path.GetDirectoryName(paths.MmcStatePath)!);
                File.WriteAllText(paths.MmcStatePath, "new state");
            }, simulateCrash: false);
            AssertRestored(paths, before);
            Expect(!Directory.Exists(paths.GroupPolicyDir), "GroupPolicy was not removed");
            Expect(!File.Exists(paths.MmcStatePath), "MMC state was not removed");
        });

        Case("MMC state as a folder", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: false);
            Directory.CreateDirectory(Path.Combine(paths.MmcStatePath, "sub"));
            File.WriteAllText(Path.Combine(paths.MmcStatePath, "sub", "a.txt"), "a");
            Before before = Snapshot(paths);
            GpeditSession.RunCore(paths, "selftest", templates, ctx =>
            {
                File.WriteAllText(Path.Combine(paths.MmcStatePath, "sub", "a.txt"), "changed");
                File.WriteAllText(Path.Combine(paths.MmcStatePath, "b.txt"), "extra");
            }, simulateCrash: false);
            AssertRestored(paths, before);
        });

        Case("re-run with a manifest is refused and changes nothing", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            Before before = Snapshot(paths);
            GpeditSession.RunCore(paths, "selftest", templates, ctx => Mutate(paths), simulateCrash: true);
            Expect(File.Exists(paths.ManifestPath), "no manifest after the simulated crash");
            string mutatedHash = FileOps.Sha256(Path.Combine(paths.GroupPolicyDir, "Machine", "Registry.pol"));
            bool refused = false;
            try
            {
                GpeditSession.RunCore(paths, "selftest", templates, ctx => throw new InvalidOperationException("must not run"), simulateCrash: false);
            }
            catch (GpeditRefusal)
            {
                refused = true;
            }

            Expect(refused, "second run was not refused");
            Expect(string.Equals(mutatedHash, FileOps.Sha256(Path.Combine(paths.GroupPolicyDir, "Machine", "Registry.pol")), StringComparison.Ordinal), "the refused run changed a file");

            RestoreResult first = GpeditRestorer.Restore(paths, _ => { });
            Expect(first.Ok && !first.NothingToRestore, "restore after the crash failed: " + string.Join("; ", first.Errors));
            AssertRestored(paths, before);
            RestoreResult second = GpeditRestorer.Restore(paths, _ => { });
            Expect(second.Ok && second.NothingToRestore, "second restore is not a no-op");
        });

        Case("template that is not ours is kept and fails restore", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            Before before = Snapshot(paths);
            GpeditSession.RunCore(paths, "selftest", templates, ctx => Mutate(paths), simulateCrash: true);
            string template = Path.Combine(paths.PolicyDefinitionsDir, AdmxName);
            File.AppendAllText(template, "<!-- edited -->");
            RestoreResult failed = GpeditRestorer.Restore(paths, _ => { });
            Expect(!failed.Ok && File.Exists(paths.ManifestPath) && File.Exists(template), "a modified template did not keep the manifest");
            File.Delete(template);
            RestoreResult fixedUp = GpeditRestorer.Restore(paths, _ => { });
            Expect(fixedUp.Ok, "restore did not finish after the template was deleted: " + string.Join("; ", fixedUp.Errors));
            AssertRestored(paths, before);
        });

        Case("existing template name is refused and untouched", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            string foreign = Path.Combine(paths.PolicyDefinitionsDir, AdmxName);
            File.WriteAllText(foreign, "not ours");
            Before before = Snapshot(paths);
            bool refused = false;
            try
            {
                GpeditSession.RunCore(paths, "selftest", templates, ctx => throw new InvalidOperationException("must not run"), simulateCrash: false);
            }
            catch (GpeditRefusal)
            {
                refused = true;
            }

            Expect(refused, "a foreign file with the template name did not refuse");
            Expect(File.ReadAllText(foreign) == "not ours", "the foreign file was changed");
            Expect(!File.Exists(paths.ManifestPath), "a manifest was written");
            Expect(TreeSnapshot.Compare(before.GroupPolicy, TreeSnapshot.Scan("GroupPolicy", paths.GroupPolicyDir)).Count == 0, "GroupPolicy changed");
        });

        Case("locked file keeps the manifest until it is released", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            Before before = Snapshot(paths);
            GpeditSession.RunCore(paths, "selftest", templates, ctx => Mutate(paths), simulateCrash: true);
            RestoreResult locked;
            using (new FileStream(Path.Combine(paths.GroupPolicyDir, "Machine", "Registry.pol"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                locked = GpeditRestorer.Restore(paths, _ => { });
            }

            Expect(!locked.Ok && File.Exists(paths.ManifestPath), "a locked file did not keep the manifest");
            RestoreResult released = GpeditRestorer.Restore(paths, _ => { });
            Expect(released.Ok, "restore failed after the lock was released: " + string.Join("; ", released.Errors));
            AssertRestored(paths, before);
        });

        Case("junction inside GroupPolicy is refused", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            string target = Path.Combine(paths.OutputDir, "junction-target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            string link = Path.Combine(paths.GroupPolicyDir, "Link");
            RunCmd($"mklink /J \"{link}\" \"{target}\"");
            bool refused = false;
            try
            {
                GpeditSession.RunCore(paths, "selftest", templates, ctx => throw new InvalidOperationException("must not run"), simulateCrash: false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("reparse point", StringComparison.Ordinal))
            {
                refused = true;
            }
            finally
            {
                FileOps.DeleteTree(link);
            }

            Expect(refused, "a junction was not refused");
            Expect(File.Exists(Path.Combine(target, "keep.txt")), "the junction target was touched");
            Expect(!File.Exists(paths.ManifestPath) && !Directory.Exists(paths.BackupDir), "a manifest or partial backup was left");
        });

        Case("only started processes are closed", () =>
        {
            TemplateSet templates = Reset(paths, groupPolicy: true, mmcState: true);
            using Process ours = StartSleeper();
            using Process other = StartSleeper();
            try
            {
                GpeditSession.RunCore(paths, "selftest", templates, ctx =>
                {
                    ctx.RegisterProcess(ours, ours.StartTime.ToUniversalTime().Ticks);
                    ctx.RegisterProcess(other, other.StartTime.ToUniversalTime().Ticks + TimeSpan.TicksPerSecond);
                }, simulateCrash: true);
                RestoreResult result = GpeditRestorer.Restore(paths, _ => { });
                Expect(result.Ok, "restore failed: " + string.Join("; ", result.Errors));
                Expect(ours.WaitForExit(2000), "the recorded process was not closed");
                Expect(!other.HasExited, "a process whose start time did not match was closed");
            }
            finally
            {
                if (!other.HasExited)
                {
                    other.Kill();
                }

                if (!ours.HasExited)
                {
                    ours.Kill();
                }
            }
        });

        Case("registry: only listed keys, deleted and verified", () =>
        {
            Reset(paths, groupPolicy: true, mmcState: true);
            Expect(Throws(() => GpeditRegistry.Delete(new RegistryTarget(true, @"Software\Microsoft"))), "an unlisted HKLM key was not refused");
            Expect(Throws(() => GpeditRegistry.Delete(new RegistryTarget(false, @"Software\BWC-Oracle"))), "the regedit oracle key was not refused");
            Expect(Throws(() => GpeditRegistry.Delete(new RegistryTarget(true, GpeditPaths.SelftestKey))), "the self test key under HKLM was not refused");
            Expect(!GpeditRegistry.Exists(paths.RegistryTargets[0]), "the self test key exists after a reset");
        });

        var report = new JsonObject
        {
            ["tool"] = "gpedit selftest",
            ["allPass"] = allPass,
            ["cases"] = cases,
        };
        Report.WriteJson(Path.GetFullPath(Path.Combine(paths.OutputDir, "..", "selftest.json")), report);
        Console.WriteLine(allPass ? "selftest: all cases passed" : "selftest: FAILED");
        return allPass ? 0 : 1;
    }

    private sealed record Before(TreeSnapshot GroupPolicy, TreeSnapshot MmcState, string DecoyHash);

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static Before Snapshot(GpeditPaths paths) => new(
        TreeSnapshot.Scan("GroupPolicy", paths.GroupPolicyDir),
        TreeSnapshot.Scan("MmcState", paths.MmcStatePath),
        FileOps.Sha256(Path.Combine(paths.PolicyDefinitionsDir, "Decoy.admx")));

    private static void AssertRestored(GpeditPaths paths, Before before)
    {
        var diffs = TreeSnapshot.Compare(before.GroupPolicy, TreeSnapshot.Scan("GroupPolicy", paths.GroupPolicyDir));
        diffs.AddRange(TreeSnapshot.Compare(before.MmcState, TreeSnapshot.Scan("MmcState", paths.MmcStatePath)));
        Expect(diffs.Count == 0, "not restored: " + string.Join("; ", diffs));
        Expect(!File.Exists(Path.Combine(paths.PolicyDefinitionsDir, AdmxName)), "template admx left behind");
        Expect(!File.Exists(Path.Combine(paths.PolicyDefinitionsEnUsDir, "BwcSelftest.adml")), "template adml left behind");
        Expect(string.Equals(before.DecoyHash, FileOps.Sha256(Path.Combine(paths.PolicyDefinitionsDir, "Decoy.admx")), StringComparison.Ordinal), "a file that is not ours was changed");
        Expect(!GpeditRegistry.Exists(paths.RegistryTargets[0]), "registry key left behind");
        Expect(!File.Exists(paths.ManifestPath), "manifest left behind");
    }

    // Every kind of change a capture can make: edit, delete, add, new folder, remove an empty folder, console state, registry value.
    private static void Mutate(GpeditPaths paths)
    {
        string machine = Path.Combine(paths.GroupPolicyDir, "Machine");
        File.WriteAllText(Path.Combine(machine, "Registry.pol"), "changed by capture");
        File.Delete(Path.Combine(paths.GroupPolicyDir, "gpt.ini"));
        File.WriteAllText(Path.Combine(machine, "comment.cmtx"), "new");
        Directory.CreateDirectory(Path.Combine(paths.GroupPolicyDir, "Extra", "Deep"));
        File.WriteAllText(Path.Combine(paths.GroupPolicyDir, "Extra", "Deep", "x.txt"), "x");
        Directory.Delete(Path.Combine(paths.GroupPolicyDir, "User"));
        File.WriteAllText(paths.MmcStatePath, "console state changed");
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(GpeditPaths.SelftestKey);
        key.SetValue("Leftover", 1, RegistryValueKind.DWord);
    }

    // Wipes the sandbox, rebuilds it, and returns the template set the session will install.
    private static TemplateSet Reset(GpeditPaths paths, bool groupPolicy, bool mmcState)
    {
        if (Directory.Exists(paths.OutputDir))
        {
            FileOps.DeleteTree(paths.OutputDir);
        }

        Registry.CurrentUser.DeleteSubKeyTree(GpeditPaths.SelftestKey, throwOnMissingSubKey: false);

        Directory.CreateDirectory(paths.PolicyDefinitionsEnUsDir);
        File.WriteAllText(Path.Combine(paths.PolicyDefinitionsDir, "Decoy.admx"), "decoy");
        File.WriteAllText(Path.Combine(paths.PolicyDefinitionsEnUsDir, "Decoy.adml"), "decoy");

        if (groupPolicy)
        {
            string machine = Path.Combine(paths.GroupPolicyDir, "Machine");
            Directory.CreateDirectory(machine);
            Directory.CreateDirectory(Path.Combine(paths.GroupPolicyDir, "User"));
            File.WriteAllBytes(Path.Combine(machine, "Registry.pol"), new byte[] { 0x50, 0x52, 0x65, 0x67, 1, 0, 0, 0 });
            File.WriteAllText(Path.Combine(paths.GroupPolicyDir, "gpt.ini"), "[General]\r\nVersion=1\r\n", Encoding.ASCII);
            File.SetAttributes(Path.Combine(paths.GroupPolicyDir, "gpt.ini"), FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(paths.GroupPolicyDir, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory);
        }

        if (mmcState)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MmcStatePath)!);
            File.WriteAllText(paths.MmcStatePath, "console state");
        }

        string source = Path.Combine(paths.OutputDir, "template-source");
        Directory.CreateDirectory(Path.Combine(source, "en-US"));
        File.WriteAllText(Path.Combine(source, AdmxName), "<policyDefinitions />");
        File.WriteAllText(Path.Combine(source, AdmlName), "<policyDefinitionResources />");
        return TemplateSet.FromDirectory(source);
    }

    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("ping.exe did not start");
    }

    private static void RunCmd(string commandLine)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + commandLine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start");
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Expect(process.ExitCode == 0, "cmd failed: " + commandLine);
    }
}
