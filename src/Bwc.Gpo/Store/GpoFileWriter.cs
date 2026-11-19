using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bwc.Gpo.Store;

/// <summary>What the writer did, reported to the test hook after each step.</summary>
public enum GpoWriteKind
{
    /// <summary>A file is now in place (created or replaced).</summary>
    FileWritten,

    /// <summary>A line was appended to a file.</summary>
    LineAppended,

    /// <summary>A file was copied into a backup folder.</summary>
    BackupCopied,

    /// <summary>An absent-marker was written into a backup folder for a file that does not exist.</summary>
    BackupAbsentMarked,

    /// <summary>An old backup folder was removed.</summary>
    BackupPruned,
}

/// <summary>One step the writer completed.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Path">The full path that was written, copied to, or removed.</param>
public readonly record struct GpoWriteEvent(GpoWriteKind Kind, string Path);

/// <summary>
/// The only code in Bwc.Gpo that writes, replaces, copies or deletes files (scripts/check-policy-writer.js enforces
/// this). Every public method asks the write policy first. Writes are atomic: a temp file next to the target, flushed,
/// then swapped in. Not thread safe: the store serializes saves with its named mutex.
/// </summary>
public sealed class GpoFileWriter
{
    /// <summary>The number of backup folders kept.</summary>
    public const int BackupsToKeep = 10;

    private static readonly Regex BackupFolderName = new(@"^(\d{8}-\d{6}-\d{3})(?:-(\d+))?$", RegexOptions.CultureInvariant);
    private static readonly FileAttributes CarriedAttributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed;

    private readonly IGpoStorePolicy _policy;
    private readonly Action<GpoWriteEvent>? _observer;

    /// <summary>Creates a writer bound to a policy.</summary>
    /// <param name="policy">The write policy; asked before every operation.</param>
    /// <param name="observer">A test hook told after each completed step; may be null.</param>
    public GpoFileWriter(IGpoStorePolicy policy, Action<GpoWriteEvent>? observer = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _observer = observer;
    }

    /// <summary>How many times a replace is retried after a sharing or lock violation. Default 5.</summary>
    public int RetryCount { get; set; } = 5;

    /// <summary>The wait between retries. Default 50 ms.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Asks the policy and checks the target is not read-only, without touching anything. The store runs this for every
    /// file of a save before the first write, so a read-only file cannot leave a half-written save.
    /// </summary>
    /// <param name="path">The file about to be written.</param>
    /// <exception cref="GpoStoreException">The policy refuses, or the file is read-only.</exception>
    public void EnsureWritable(string path)
    {
        _policy.EnsureWriteAllowed(path);
        var full = Path.GetFullPath(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw GpoStoreException.FromIo("Inspect", full, e);
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new GpoStoreException(
                GpoStoreFailure.ReadOnlyTarget, "Write", full, 0, $"'{full}' has the read-only attribute; it is not cleared.");
        }
    }

    /// <summary>Writes a file atomically: temp file beside it, flush, replace (or move in when it is new).</summary>
    /// <param name="path">The target file.</param>
    /// <param name="content">The complete new content.</param>
    /// <exception cref="GpoStoreException">The policy refuses, the file is read-only or in use, or I/O fails.</exception>
    public void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        EnsureWritable(path);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        EnsureDirectory(directory);
        SweepStaleTemps(directory);

        var temp = full + ".bwc-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            var existed = File.Exists(full);
            var carried = existed ? File.GetAttributes(full) & CarriedAttributes : 0;

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }

            File.SetAttributes(temp, carried == 0 ? FileAttributes.Normal : carried);
            Swap(temp, full, existed);
        }
        catch (GpoStoreException)
        {
            RemoveQuietly(temp);
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RemoveQuietly(temp);
            throw GpoStoreException.FromIo("Write", full, e);
        }

        _observer?.Invoke(new GpoWriteEvent(GpoWriteKind.FileWritten, full));
    }

    /// <summary>Appends one line (UTF-8, no byte order mark, CRLF) to a file, creating it and its folder when needed.</summary>
    /// <param name="path">The file.</param>
    /// <param name="line">The line, without a line ending.</param>
    /// <exception cref="GpoStoreException">The policy refuses or an I/O error occurs.</exception>
    public void AppendLine(string path, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _policy.EnsureWriteAllowed(path);
        var full = Path.GetFullPath(path);
        EnsureDirectory(Path.GetDirectoryName(full)!);
        var bytes = new UTF8Encoding(false).GetBytes(line + "\r\n");
        try
        {
            using var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw GpoStoreException.FromIo("Append", full, e);
        }

        _observer?.Invoke(new GpoWriteEvent(GpoWriteKind.LineAppended, full));
    }

    /// <summary>
    /// Copies the given GPO files into a new timestamped folder under the backup root, keeping their paths relative to
    /// the GPO root. A file that does not exist gets an empty <c>&lt;name&gt;.absent</c> marker instead.
    /// </summary>
    /// <param name="files">Files inside the GPO root.</param>
    /// <returns>The new backup folder.</returns>
    /// <exception cref="GpoStoreException">The policy refuses, a file is outside the root, or the copy fails.</exception>
    public string BackupFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var gpoRoot = Path.GetFullPath(_policy.GpoRoot);
        var relatives = new List<(string Source, string Relative)>();
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            if (!GpoPathChecks.IsUnder(gpoRoot.TrimEnd(Path.DirectorySeparatorChar), full))
            {
                throw GpoPathChecks.Denied("Backup", full, "the file is outside the GPO folder");
            }

            relatives.Add((full, Path.GetRelativePath(gpoRoot, full)));
        }

        // The system policy's backup root sits in the BWC data root, which a user could pre-create. Create and check the
        // chain first, so nothing is read or created through a folder that is not protected.
        (_policy as SystemGpoStorePolicy)?.EnsureBackupRoot("Backup");
        var backupRoot = Path.GetFullPath(_policy.BackupRoot);
        var folder = NextBackupFolder(backupRoot);
        _policy.EnsureWriteAllowed(folder);

        try
        {
            Directory.CreateDirectory(folder);
            foreach (var (source, relative) in relatives)
            {
                var destination = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(source))
                {
                    CopyWithRetry(source, destination);
                    File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                    _observer?.Invoke(new GpoWriteEvent(GpoWriteKind.BackupCopied, destination));
                }
                else
                {
                    var marker = destination + ".absent";
                    using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                    }

                    _observer?.Invoke(new GpoWriteEvent(GpoWriteKind.BackupAbsentMarked, marker));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw GpoStoreException.FromIo("Backup", folder, e, GpoStoreFailure.BackupFailed);
        }

        return folder;
    }

    /// <summary>
    /// Removes the oldest backup folders so that <paramref name="keep"/> remain. Only folders named like a backup
    /// (<c>yyyyMMdd-HHmmss-fff</c>, optional <c>-n</c>) are considered. A reparse point is removed as a link only.
    /// A folder that cannot be removed does not stop the others.
    /// </summary>
    /// <param name="keep">How many to keep, newest first.</param>
    /// <returns>The first removal failure, or null.</returns>
    /// <exception cref="GpoStoreException">The policy refuses a removal.</exception>
    public GpoStoreException? PruneBackups(int keep = BackupsToKeep)
    {
        (_policy as SystemGpoStorePolicy)?.VerifyBackupRoot("Prune");
        var backupRoot = Path.GetFullPath(_policy.BackupRoot);
        if (!Directory.Exists(backupRoot))
        {
            return null;
        }

        var candidates = new List<(string Path, string Stamp, int Suffix)>();
        foreach (var directory in Directory.GetDirectories(backupRoot))
        {
            var match = BackupFolderName.Match(System.IO.Path.GetFileName(directory));
            if (match.Success)
            {
                var suffix = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                candidates.Add((directory, match.Groups[1].Value, suffix));
            }
        }

        candidates.Sort((a, b) =>
        {
            var byStamp = string.CompareOrdinal(a.Stamp, b.Stamp);
            return byStamp != 0 ? byStamp : a.Suffix.CompareTo(b.Suffix);
        });

        GpoStoreException? firstFailure = null;
        for (var i = 0; i < candidates.Count - Math.Max(keep, 0); i++)
        {
            var directory = candidates[i].Path;
            _policy.EnsureRemoveAllowed(directory);
            try
            {
                var isLink = (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
                Directory.Delete(directory, !isLink);
                _observer?.Invoke(new GpoWriteEvent(GpoWriteKind.BackupPruned, directory));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= GpoStoreException.FromIo("Prune", directory, e, GpoStoreFailure.Io);
            }
        }

        return firstFailure;
    }

    private void Swap(string temp, string target, bool targetExisted)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (targetExisted)
                {
                    File.Replace(temp, target, null, false);
                }
                else
                {
                    File.Move(temp, target);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var code = GpoStoreException.Win32Of(e);
                if (code is 32 or 33 && attempt < RetryCount)
                {
                    Thread.Sleep(RetryDelay);
                    continue;
                }

                throw GpoStoreException.FromIo(targetExisted ? "Replace" : "Move", target, e);
            }
        }
    }

    private void CopyWithRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, false);
                return;
            }
            catch (IOException e) when (GpoStoreException.Win32Of(e) is 32 or 33 && attempt < RetryCount)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw GpoStoreException.FromIo("CreateFolder", directory, e);
        }
    }

    private void SweepStaleTemps(string directory)
    {
        string[] stale;
        try
        {
            stale = Directory.GetFiles(directory, "*.bwc-*.tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in stale)
        {
            try
            {
                _policy.EnsureRemoveAllowed(file);
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or GpoStoreException)
            {
                // Best effort: a stale temp file we cannot remove does not block the save.
            }
        }
    }

    private static void RemoveQuietly(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The temp file is named and swept on the next write.
        }
    }

    private static string NextBackupFolder(string backupRoot)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(backupRoot, stamp);
        for (var n = 1; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = Path.Combine(backupRoot, stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
        }

        return candidate;
    }
}
