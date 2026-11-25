using System.Diagnostics;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// Puts the machine into a known start state before a case: GroupPolicy holds exactly the case's start files, the MMC state is gone and the
// four BWC-Oracle keys do not exist. Only paths the session already backed up are touched, and a reparse point is never followed.
internal static class CaseReset
{
    internal static void Reset(GpeditPaths paths, StartState start)
    {
        if (paths.Sandbox)
        {
            throw new InvalidOperationException("the matrix never runs against the sandbox paths");
        }

        string root = paths.GroupPolicyDir;
        if (!string.Equals(Path.GetFileName(root), "GroupPolicy", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"refused: {root} is not a GroupPolicy folder");
        }

        if (Directory.Exists(root))
        {
            var info = new DirectoryInfo(root);
            FileOps.RefuseReparse(info);
            foreach (FileSystemInfo child in info.EnumerateFileSystemInfos().ToList())
            {
                if (child is DirectoryInfo sub)
                {
                    FileOps.DeleteTree(sub.FullName);
                }
                else
                {
                    FileOps.DeleteFile(child.FullName);
                }
            }
        }
        else
        {
            Directory.CreateDirectory(root);
        }

        foreach (StartFile file in start.Files)
        {
            string target = Path.GetFullPath(Path.Combine(root, file.Name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"refused: start file {file.Name} leaves the GroupPolicy folder");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            FileOps.Retry(() => File.WriteAllBytes(target, file.Bytes));
        }

        if (Directory.Exists(paths.MmcStatePath))
        {
            FileOps.DeleteTree(paths.MmcStatePath);
        }
        else
        {
            FileOps.DeleteFile(paths.MmcStatePath);
        }

        foreach (RegistryTarget target in paths.RegistryTargets)
        {
            GpeditRegistry.Delete(target);
        }

        Verify(paths, start);
    }

    // The folder holds the start files byte for byte and nothing else; the state and the keys are gone.
    internal static void Verify(GpeditPaths paths, StartState start)
    {
        var expected = start.Files.ToDictionary(f => f.Name, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var actual = new List<string>();
        if (Directory.Exists(paths.GroupPolicyDir))
        {
            actual.AddRange(Directory.EnumerateFiles(paths.GroupPolicyDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(paths.GroupPolicyDir, f)));
        }

        foreach (string name in actual)
        {
            if (!expected.TryGetValue(name, out byte[]? bytes))
            {
                throw new InvalidOperationException($"reset failed: {name} is still in the GroupPolicy folder");
            }

            if (!File.ReadAllBytes(Path.Combine(paths.GroupPolicyDir, name)).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException($"reset failed: {name} does not hold the start bytes");
            }
        }

        foreach (string name in expected.Keys.Where(n => !actual.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"reset failed: start file {name} is missing");
        }

        if (Directory.Exists(paths.MmcStatePath) || File.Exists(paths.MmcStatePath))
        {
            throw new InvalidOperationException("reset failed: the MMC state is still there");
        }

        foreach (RegistryTarget target in paths.RegistryTargets.Where(GpeditRegistry.Exists))
        {
            throw new InvalidOperationException($"reset failed: {target.Display} still exists");
        }
    }
}

// Closes mmc.exe processes that started after the matrix did, so a case that died half way cannot leave a console behind for the next one.
internal static class StrayMmc
{
    internal static int Close(DateTime notBefore)
    {
        int closed = 0;
        foreach (Process process in Process.GetProcessesByName("mmc"))
        {
            try
            {
                if (process.StartTime < notBefore)
                {
                    continue;
                }

                if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }

                closed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // The process ended by itself or cannot be inspected; the next case's start check will say if one is left.
            }
            finally
            {
                process.Dispose();
            }
        }

        return closed;
    }
}
