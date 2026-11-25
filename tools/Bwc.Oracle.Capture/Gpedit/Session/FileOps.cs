using System.Security.Cryptography;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// File helpers that never follow a reparse point and retry sharing violations, because the policy service and MMC may hold a file briefly.
internal static class FileOps
{
    private const int Attempts = 6;

    internal static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < Attempts && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(250);
            }
        }
    }

    internal static bool IsReparse(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    internal static void RefuseReparse(FileSystemInfo info)
    {
        if (IsReparse(info))
        {
            throw new InvalidOperationException($"refused: {info.FullName} is a reparse point (symlink or junction). The session never follows one.");
        }
    }

    internal static void DeleteFile(string path)
    {
        Retry(() =>
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        });
    }

    // Deletes a folder tree. A link inside is removed as a link; its target is never entered.
    internal static void DeleteTree(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
        {
            return;
        }

        if (!IsReparse(info))
        {
            foreach (FileSystemInfo child in info.EnumerateFileSystemInfos().ToList())
            {
                if (child is DirectoryInfo sub)
                {
                    DeleteTree(sub.FullName);
                }
                else
                {
                    DeleteFile(child.FullName);
                }
            }
        }

        Retry(() =>
        {
            File.SetAttributes(dir, FileAttributes.Directory);
            Directory.Delete(dir, recursive: false);
        });
    }

    // Copies src over dst (overwrite allowed) and proves the copy by hash.
    internal static void CopyVerified(string src, string dst, string expectedHash)
    {
        Retry(() =>
        {
            string? dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(dst))
            {
                File.SetAttributes(dst, FileAttributes.Normal);
            }

            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        });
        string actual = Sha256(dst);
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
        {
            throw new IOException($"copy of {src} to {dst} does not match its hash (expected {expectedHash}, got {actual}).");
        }
    }

    // Writes a new file. An existing file is never replaced (FileMode.CreateNew).
    internal static void CopyNew(string src, string dst)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }
}
