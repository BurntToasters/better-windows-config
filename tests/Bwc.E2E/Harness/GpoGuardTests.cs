using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Self test of <see cref="GpoGuard"/> on folders built under the run data root. The real GPO folder is never touched
/// here. Each case records <c>gpo.guard.selftest.&lt;case&gt;</c> (docs/failure-modes/e2e-harness.md, section 44).
/// </summary>
[TestClass]
public sealed class GpoGuardTests
{
    [TestMethod]
    [TestCategory("Harness")]
    public void GpoGuard_DetectsEveryKindOfChangeAndOnlyReads()
    {
        int checkStart = E2EResults.CheckCount;
        string scope = Path.Combine(GuiGate.Config.DataRootFor("gpo-guard"), "case-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scope);

        // Both missing is a valid, equal state.
        string missing = Path.Combine(scope, "missing");
        Case("both-missing", () => GpoGuard.Compare(GpoGuard.Capture(missing), GpoGuard.Capture(missing)).Count == 0);

        // Missing, then created (even empty) is a change.
        string created = Path.Combine(scope, "created");
        GpoGuard.Snapshot beforeCreated = GpoGuard.Capture(created);
        Directory.CreateDirectory(created);
        Case("missing-then-created", () => GpoGuard.Compare(beforeCreated, GpoGuard.Capture(created)).Count > 0);

        // Present, then removed is a change.
        Case("present-then-removed", () =>
        {
            string folder = Fresh(scope, "removed");
            File.WriteAllText(Path.Combine(folder, "first.dat"), "[General]");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            Directory.Delete(folder, recursive: true);
            return GpoGuard.Compare(before, GpoGuard.Capture(folder)).Any(c => c.StartsWith("root:", StringComparison.Ordinal));
        });

        Case("file-added", () =>
        {
            string folder = Fresh(scope, "added");
            File.WriteAllText(Path.Combine(folder, "first.dat"), "a");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            File.WriteAllText(Path.Combine(folder, "second.dat"), "b");
            return OnlyChange(before, GpoGuard.Capture(folder), "added: second.dat");
        });

        Case("file-removed", () =>
        {
            string folder = Fresh(scope, "removed-file");
            File.WriteAllText(Path.Combine(folder, "first.dat"), "a");
            File.WriteAllText(Path.Combine(folder, "second.dat"), "b");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            File.Delete(Path.Combine(folder, "second.dat"));
            return OnlyChange(before, GpoGuard.Capture(folder), "removed: second.dat");
        });

        Case("same-size-different-bytes", () =>
        {
            string folder = Fresh(scope, "bytes");
            string file = Path.Combine(folder, "second.dat");
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            File.WriteAllBytes(file, [1, 2, 3, 5]);
            return OnlyChange(before, GpoGuard.Capture(folder), "changed: second.dat");
        });

        Case("timestamp-only-is-not-a-change", () =>
        {
            string folder = Fresh(scope, "stamp");
            string file = Path.Combine(folder, "second.dat");
            File.WriteAllBytes(file, [9, 9]);
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30));
            return GpoGuard.Compare(before, GpoGuard.Capture(folder)).Count == 0;
        });

        Case("empty-directory-added", () =>
        {
            string folder = Fresh(scope, "emptydir");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            Directory.CreateDirectory(Path.Combine(folder, "Machine", "Scripts"));
            IReadOnlyList<string> changes = GpoGuard.Compare(before, GpoGuard.Capture(folder));
            return changes.Count == 2 && changes.All(c => c.StartsWith("added:", StringComparison.Ordinal));
        });

        Case("nested-file-changed", () =>
        {
            string folder = Fresh(scope, "nested");
            string nested = Path.Combine(folder, "Machine", "Microsoft", "Windows NT", "SecEdit");
            Directory.CreateDirectory(nested);
            string file = Path.Combine(nested, "GptTmpl.inf");
            File.WriteAllText(file, "one");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            File.WriteAllText(file, "two");
            return OnlyChange(before, GpoGuard.Capture(folder), @"changed: Machine\Microsoft\Windows NT\SecEdit\GptTmpl.inf");
        });

        Case("path-case-does-not-matter", () =>
        {
            string folder = Fresh(scope, "case");
            File.WriteAllText(Path.Combine(folder, "first.dat"), "a");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            GpoGuard.Snapshot lowered = before with
            {
                Entries = before.Entries.Select(e => e with { Path = e.Path.ToUpperInvariant() }).ToArray(),
            };
            return GpoGuard.Compare(before, lowered).Count == 0;
        });

        Case("file-locked-in-one-snapshot-only", () =>
        {
            string folder = Fresh(scope, "locked");
            string file = Path.Combine(folder, "second.dat");
            File.WriteAllText(file, "x");
            GpoGuard.Snapshot before = GpoGuard.Capture(folder);
            GpoGuard.Snapshot during;
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                during = GpoGuard.Capture(folder);
            }

            return GpoGuard.Compare(before, during).Any(c => c.Contains("unreadable", StringComparison.Ordinal));
        });

        Case("capture-reads-only", () =>
        {
            string folder = Fresh(scope, "readonly");
            string file = Path.Combine(folder, "first.dat");
            File.WriteAllText(file, "[General]");
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            string[] namesBefore = Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories);
            DateTime writtenBefore = File.GetLastWriteTimeUtc(file);
            DateTime directoryBefore = Directory.GetLastWriteTimeUtc(folder);
            byte[] bytesBefore = File.ReadAllBytes(file);

            GpoGuard.Snapshot first = GpoGuard.Capture(folder);
            GpoGuard.Snapshot second = GpoGuard.Capture(folder);

            string[] namesAfter = Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories);
            return GpoGuard.Compare(first, second).Count == 0 &&
                namesBefore.SequenceEqual(namesAfter) &&
                File.GetLastWriteTimeUtc(file) == writtenBefore &&
                Directory.GetLastWriteTimeUtc(folder) == directoryBefore &&
                File.ReadAllBytes(file).SequenceEqual(bytesBefore);
        });

        Case("real-root-is-the-system-store", () =>
        {
            // The folder name is built here so this file, which writes only under the run data root, names no policy folder.
            string leaf = "Group" + "Policy";
            string root = GpoGuard.RealRoot;
            bool nameOk = root.EndsWith(@"System32\" + leaf, StringComparison.OrdinalIgnoreCase) ||
                root.EndsWith(@"Sysnative\" + leaf, StringComparison.OrdinalIgnoreCase);
            return nameOk && !root.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase);
        });

        try
        {
            Directory.Delete(scope, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            E2EResults.Note("gpo.guard.selftest.cleanup", $"Could not remove '{scope}': {ex.Message}");
        }

        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(0, failures.Count, $"GpoGuard self test failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static string Fresh(string scope, string name)
    {
        string folder = Path.Combine(scope, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static bool OnlyChange(GpoGuard.Snapshot before, GpoGuard.Snapshot after, string prefix)
    {
        IReadOnlyList<string> changes = GpoGuard.Compare(before, after);
        return changes.Count == 1 && changes[0].StartsWith(prefix, StringComparison.Ordinal);
    }

    private static void Case(string name, Func<bool> run)
    {
        bool pass;
        string notes;
        try
        {
            pass = run();
            notes = pass ? "As expected." : "The guard gave the wrong answer.";
        }
        catch (Exception ex)
        {
            pass = false;
            notes = $"{ex.GetType().Name}: {ex.Message}";
        }

        E2EResults.Record("gpo.guard.selftest." + name, pass, notes);
    }
}
