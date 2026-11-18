using System.Security.Cryptography;
using System.Text;

namespace Bwc.E2E.Harness;

/// <summary>
/// Proves that an E2E run did not write the machine's real Local Group Policy store. The folder
/// <c>%SystemRoot%\System32\GroupPolicy</c> is hashed before the first test and after the last, and the check
/// <c>gpo.real-unchanged</c> fails when anything differs. The guard only reads: files open with read access and shared
/// read, write and delete, and nothing is created, touched or deleted below the root
/// (docs/failure-modes/e2e-harness.md, section 44).
/// </summary>
public static class GpoGuard
{
    /// <summary>The check recorded in the run artifact.</summary>
    public const string CheckId = "gpo.real-unchanged";

    private const int ReadAttempts = 3;
    private const int MaxListedChanges = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly object Sync = new();
    private static Snapshot? _baseline;
    private static string? _baselineError;
    private static bool _begun;

    /// <summary>The real folder. A 32-bit process on a 64-bit system reads it through Sysnative, never SysWOW64.</summary>
    public static string RealRoot
    {
        get
        {
            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string system = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32";
            return Path.Combine(systemRoot, system, "GroupPolicy");
        }
    }

    /// <summary>Takes the baseline. Never throws: a failure becomes a failed check in <see cref="Finish"/>.</summary>
    public static void Begin()
    {
        lock (Sync)
        {
            _begun = true;
            _baseline = null;
            _baselineError = null;
            try
            {
                _baseline = Capture(RealRoot);
            }
            catch (Exception ex)
            {
                _baselineError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Takes the final snapshot, compares it with the baseline and records <see cref="CheckId"/>. Never throws, so the
    /// artifact is still written. A missing baseline or a failed final capture is a failed check, never a pass.
    /// </summary>
    public static void Finish()
    {
        try
        {
            Snapshot? before;
            string? beforeError;
            bool begun;
            lock (Sync)
            {
                before = _baseline;
                beforeError = _baselineError;
                begun = _begun;
            }

            if (!begun)
            {
                E2EResults.Record(CheckId, false, "The baseline of the real GPO folder was never taken (GpoGuard.Begin did not run).");
                return;
            }

            if (before is null)
            {
                E2EResults.Record(CheckId, false, $"The baseline of '{RealRoot}' could not be captured: {beforeError ?? "unknown error"}.");
                return;
            }

            Snapshot after = Capture(before.Root);
            IReadOnlyList<string> changes = Compare(before, after);
            if (changes.Count == 0)
            {
                string state = after.Exists
                    ? $"{after.Entries.Count} entries, digest {after.Digest[..16]}"
                    : "folder missing before and after";
                E2EResults.Record(CheckId, true, $"The real GPO folder '{after.Root}' is unchanged: {state}.");
                return;
            }

            E2EResults.Record(
                CheckId,
                false,
                $"The real GPO folder '{after.Root}' changed during the run ({changes.Count} differences, before digest " +
                $"{before.Digest[..16]}, after {after.Digest[..16]}): {string.Join("; ", changes.Take(MaxListedChanges))}" +
                (changes.Count > MaxListedChanges ? $"; and {changes.Count - MaxListedChanges} more" : string.Empty) + ".");
        }
        catch (Exception ex)
        {
            E2EResults.Record(CheckId, false, $"The final snapshot of the real GPO folder failed: {ex.GetType().Name}: {ex.Message}.");
        }
    }

    /// <summary>Reads every file and directory below <paramref name="root"/>. A missing root is a valid, empty state.</summary>
    internal static Snapshot Capture(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var entries = new List<Entry>();
        bool exists = Directory.Exists(root);
        if (exists)
        {
            Walk(root, string.Empty, entries);
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return new Snapshot(root, exists, entries, DigestOf(exists, entries));
    }

    /// <summary>Lists the differences between two snapshots. An empty list means equal.</summary>
    internal static IReadOnlyList<string> Compare(Snapshot before, Snapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<string>();
        if (before.Exists != after.Exists)
        {
            changes.Add(before.Exists ? "root: present -> missing" : "root: missing -> present");
        }

        var beforeByPath = before.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var afterByPath = after.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in before.Entries)
        {
            if (!afterByPath.TryGetValue(entry.Path, out Entry? other))
            {
                changes.Add($"removed: {entry.Path} ({Describe(entry)})");
            }
            else if (!string.Equals(entry.Kind, other.Kind, StringComparison.Ordinal) ||
                entry.Size != other.Size ||
                !string.Equals(entry.Hash, other.Hash, StringComparison.Ordinal))
            {
                changes.Add($"changed: {entry.Path} ({Describe(entry)} -> {Describe(other)})");
            }
        }

        foreach (Entry entry in after.Entries)
        {
            if (!beforeByPath.ContainsKey(entry.Path))
            {
                changes.Add($"added: {entry.Path} ({Describe(entry)})");
            }
        }

        return changes;
    }

    private static string Describe(Entry entry) =>
        entry.Kind == "file"
            ? $"file, {entry.Size} bytes, sha256 {entry.Hash[..Math.Min(12, entry.Hash.Length)]}"
            : entry.Kind;

    private static void Walk(string directory, string relative, List<Entry> entries)
    {
        List<FileSystemInfo>? children = null;
        Exception? failure = null;
        for (int attempt = 1; attempt <= ReadAttempts && children is null; attempt++)
        {
            try
            {
                children = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        if (children is null)
        {
            // The folder itself is already an entry in its parent. Its failed listing is a second, distinct entry.
            entries.Add(new Entry(relative + "\\", $"unreadable:{failure!.GetType().Name}", 0, string.Empty));
            return;
        }

        foreach (FileSystemInfo child in children)
        {
            string path = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Never followed and never hashed: a link could lead outside the folder or around in a loop.
                entries.Add(new Entry(path, "reparse", 0, ((int)child.Attributes).ToString("X", System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (child is DirectoryInfo)
            {
                entries.Add(new Entry(path, "dir", 0, string.Empty));
                Walk(child.FullName, path, entries);
            }
            else
            {
                entries.Add(ReadFile(child.FullName, path));
            }
        }
    }

    private static Entry ReadFile(string fullPath, string path)
    {
        Exception? failure = null;
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.SequentialScan);
                long length = stream.Length;
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                return new Entry(path, "file", length, hash);
            }
            catch (FileNotFoundException ex)
            {
                // Removed between the listing and the read: the entry is gone, which the other snapshot shows.
                failure = ex;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        return new Entry(path, $"unreadable:{failure!.GetType().Name}", 0, string.Empty);
    }

    private static string DigestOf(bool exists, List<Entry> entries)
    {
        var text = new StringBuilder();
        text.Append(exists ? "present" : "missing").Append('\n');
        foreach (Entry entry in entries)
        {
            text.Append(entry.Kind).Append('|')
                .Append(entry.Path.ToLowerInvariant()).Append('|')
                .Append(entry.Size).Append('|')
                .Append(entry.Hash).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>One file or directory: path relative to the root, kind, size and SHA-256 (files only).</summary>
    internal sealed record Entry(string Path, string Kind, long Size, string Hash);

    /// <summary>The state of the folder at one moment.</summary>
    internal sealed record Snapshot(string Root, bool Exists, IReadOnlyList<Entry> Entries, string Digest);
}
