namespace Bwc.Oracle.Capture.Gpedit.Session;

internal sealed class SnapshotEntry
{
    // Relative to the root, with backslashes. Empty for the root itself.
    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public string? Sha256 { get; set; }

    public int Attributes { get; set; }

    public long LastWriteUtcTicks { get; set; }
}

// A recorded file, folder tree or absent path: what existed, with size, SHA-256, attributes and last write time of every item.
// Scan records it, Compare says what differs, Restore makes a path equal to it again.
internal sealed class TreeSnapshot
{
    // Hidden, system and read-only are compared. Archive, indexed and similar bits change by themselves and are not.
    private const int AttributeMask = (int)(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

    public string Label { get; set; } = string.Empty;

    public string Root { get; set; } = string.Empty;

    // "absent", "file" or "directory".
    public string Kind { get; set; } = "absent";

    public List<SnapshotEntry> Entries { get; set; } = new();

    internal static TreeSnapshot Scan(string label, string root)
    {
        var snap = new TreeSnapshot { Label = label, Root = root };
        if (Directory.Exists(root))
        {
            var dir = new DirectoryInfo(root);
            FileOps.RefuseReparse(dir);
            snap.Kind = "directory";
            snap.Entries.Add(Describe(string.Empty, dir));
            ScanDirectory(snap, dir, string.Empty);
        }
        else if (File.Exists(root))
        {
            var file = new FileInfo(root);
            FileOps.RefuseReparse(file);
            snap.Kind = "file";
            snap.Entries.Add(Describe(string.Empty, file));
        }

        return snap;
    }

    private static void ScanDirectory(TreeSnapshot snap, DirectoryInfo dir, string relative)
    {
        foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            FileOps.RefuseReparse(child);
            string childRelative = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
            snap.Entries.Add(Describe(childRelative, child));
            if (child is DirectoryInfo sub)
            {
                ScanDirectory(snap, sub, childRelative);
            }
        }
    }

    private static SnapshotEntry Describe(string relative, FileSystemInfo info)
    {
        var entry = new SnapshotEntry
        {
            Path = relative,
            IsDirectory = info is DirectoryInfo,
            Attributes = (int)info.Attributes,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
        };
        if (info is FileInfo file)
        {
            entry.Size = file.Length;
            entry.Sha256 = FileOps.Sha256(file.FullName);
        }

        return entry;
    }

    internal static string AbsolutePath(string root, SnapshotEntry entry) => entry.Path.Length == 0 ? root : System.IO.Path.Combine(root, entry.Path);

    // Where an entry's backup copy lives under the backup location of this snapshot.
    internal string BackupPath(string backupLocation, SnapshotEntry entry) =>
        Kind == "file" ? System.IO.Path.Combine(backupLocation, "root.file") : System.IO.Path.Combine(backupLocation, "tree", entry.Path);

    internal int FileCount => Entries.Count(e => !e.IsDirectory);

    // Rejects a manifest whose entries could point outside the root.
    internal void ValidatePaths()
    {
        foreach (SnapshotEntry entry in Entries)
        {
            if (entry.Path.Length == 0)
            {
                continue;
            }

            if (System.IO.Path.IsPathRooted(entry.Path) || entry.Path.Split('\\', '/').Any(p => p.Length == 0 || p == "." || p == ".."))
            {
                throw new InvalidDataException($"manifest entry '{entry.Path}' in {Label} is not a plain relative path.");
            }
        }
    }

    // Differences between what was recorded and what is there now. Empty means equal.
    internal static List<string> Compare(TreeSnapshot expected, TreeSnapshot actual)
    {
        var diffs = new List<string>();
        if (!string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal))
        {
            diffs.Add($"{expected.Label}: expected {expected.Kind}, found {actual.Kind} at {actual.Root}");
            return diffs;
        }

        var now = actual.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (SnapshotEntry want in expected.Entries)
        {
            if (!now.Remove(want.Path, out SnapshotEntry? have))
            {
                diffs.Add($"{expected.Label}: missing '{want.Path}'");
            }
            else if (want.IsDirectory != have.IsDirectory)
            {
                diffs.Add($"{expected.Label}: '{want.Path}' changed between file and folder");
            }
            else if (!want.IsDirectory && (want.Size != have.Size || !string.Equals(want.Sha256, have.Sha256, StringComparison.Ordinal)))
            {
                diffs.Add($"{expected.Label}: content differs '{want.Path}'");
            }
            else if ((want.Attributes & AttributeMask) != (have.Attributes & AttributeMask))
            {
                diffs.Add($"{expected.Label}: attributes differ '{want.Path}'");
            }
        }

        foreach (string extra in now.Keys)
        {
            diffs.Add($"{expected.Label}: extra '{extra}'");
        }

        return diffs;
    }

    // Copies every file of this snapshot (read at Root) into the backup location and proves each copy by hash.
    internal void CopyToBackup(string backupLocation)
    {
        if (Directory.Exists(backupLocation))
        {
            FileOps.DeleteTree(backupLocation);
        }

        Directory.CreateDirectory(backupLocation);
        foreach (SnapshotEntry entry in Entries)
        {
            string dst = BackupPath(backupLocation, entry);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(dst);
            }
            else
            {
                FileOps.CopyVerified(AbsolutePath(Root, entry), dst, entry.Sha256 ?? string.Empty);
            }
        }
    }

    // Makes the path at root equal to this snapshot. Every step is tried; failures are appended to errors.
    internal void Restore(string root, string backupLocation, List<string> errors)
    {
        void Attempt(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors.Add($"{Label}: {what}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (Kind == "absent")
        {
            Attempt("delete what the session created", () =>
            {
                if (Directory.Exists(root))
                {
                    FileOps.DeleteTree(root);
                }
                else if (File.Exists(root))
                {
                    FileOps.DeleteFile(root);
                }
            });
            return;
        }

        if (Kind == "file")
        {
            SnapshotEntry only = Entries[0];
            Attempt("restore file", () =>
            {
                if (Directory.Exists(root))
                {
                    FileOps.DeleteTree(root);
                }

                RestoreFile(root, BackupPath(backupLocation, only), only);
            });
            return;
        }

        Attempt("ensure folder", () =>
        {
            if (File.Exists(root))
            {
                FileOps.DeleteFile(root);
            }

            Directory.CreateDirectory(root);
        });

        var wanted = new HashSet<string>(Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        Attempt("delete extra items", () => DeleteExtras(new DirectoryInfo(root), string.Empty, wanted, errors));

        foreach (SnapshotEntry entry in Entries.Where(e => e.IsDirectory && e.Path.Length > 0))
        {
            string path = AbsolutePath(root, entry);
            Attempt($"recreate folder '{entry.Path}'", () =>
            {
                if (File.Exists(path))
                {
                    FileOps.DeleteFile(path);
                }

                Directory.CreateDirectory(path);
            });
        }

        foreach (SnapshotEntry entry in Entries.Where(e => !e.IsDirectory))
        {
            string path = AbsolutePath(root, entry);
            Attempt($"restore file '{entry.Path}'", () => RestoreFile(path, BackupPath(backupLocation, entry), entry));
        }

        foreach (SnapshotEntry entry in Entries.Where(e => e.IsDirectory).OrderByDescending(e => e.Path.Length))
        {
            string path = AbsolutePath(root, entry);
            Attempt($"folder attributes '{entry.Path}'", () =>
            {
                if (Directory.Exists(path))
                {
                    File.SetAttributes(path, (FileAttributes)entry.Attributes | FileAttributes.Directory);
                }
            });
        }
    }

    private static void RestoreFile(string path, string backupFile, SnapshotEntry entry)
    {
        string expected = entry.Sha256 ?? string.Empty;
        bool same = File.Exists(path) && string.Equals(FileOps.Sha256(path), expected, StringComparison.Ordinal);
        if (!same)
        {
            FileOps.CopyVerified(backupFile, path, expected);
            File.SetLastWriteTimeUtc(path, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
        }

        File.SetAttributes(path, (FileAttributes)entry.Attributes);
    }

    private static void DeleteExtras(DirectoryInfo dir, string relative, HashSet<string> wanted, List<string> errors)
    {
        foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos().ToList())
        {
            string childRelative = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
            try
            {
                bool keep = wanted.Contains(childRelative);
                if (child is DirectoryInfo sub)
                {
                    if (!keep || FileOps.IsReparse(sub))
                    {
                        FileOps.DeleteTree(sub.FullName);
                    }
                    else
                    {
                        DeleteExtras(sub, childRelative, wanted, errors);
                    }
                }
                else if (!keep)
                {
                    FileOps.DeleteFile(child.FullName);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"delete extra '{childRelative}' under {dir.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
