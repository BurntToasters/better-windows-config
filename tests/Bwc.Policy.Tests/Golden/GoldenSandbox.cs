using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;

namespace Bwc.Policy.Tests.Golden;

/// <summary>A refresher that does nothing and counts its calls, so a replay never touches the live
/// registry.</summary>
internal sealed class CountingRefresher : IPolicyRefresher
{
    public List<GpoSide> Calls { get; } = [];

    public void Refresh(GpoSide side) => Calls.Add(side);
}

/// <summary>
/// A temp sandbox GPO folder for one test: a new run folder under the OS temp folder, a GPO root inside it
/// and the store over both. The only file in this test project that copies or deletes policy files, so the
/// test classes that name policy file names hold no write API (scripts/check-policy-writer.js). It names
/// no policy path itself; the file names come from <see cref="GpoFileNames"/>. It never touches the real
/// GPO folder.
/// </summary>
internal sealed class GoldenSandbox : IDisposable
{
    public GoldenSandbox()
    {
        RunRoot = Path.Combine(Path.GetTempPath(), "bwc-policy-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RunRoot);
        GpoRoot = Path.Combine(RunRoot, "gpo");
        Refresher = new CountingRefresher();
        Policy = new SandboxGpoStorePolicy(GpoRoot, RunRoot, null, Refresher);
        Store = new GpoStore(
            Policy,
            new GpoStoreOptions
            {
                LockTimeout = TimeSpan.FromSeconds(5),
                WriteRetryCount = 2,
                WriteRetryDelay = TimeSpan.FromMilliseconds(10),
            });
    }

    public string RunRoot { get; }

    public string GpoRoot { get; }

    public SandboxGpoStorePolicy Policy { get; }

    public CountingRefresher Refresher { get; }

    public GpoStore Store { get; }

    /// <summary>The path of a file of the five, by the flat name a golden step folder uses.</summary>
    /// <param name="flatName">For example the machine registry file as <c>Machine.</c> plus the file name, or
    /// the version file.</param>
    /// <returns>The full path inside the sandbox GPO root.</returns>
    public string PathOf(string flatName)
    {
        if (flatName == GpoFileNames.GptIniFile)
        {
            return GpoFileNames.GptIniPath(GpoRoot);
        }

        foreach (var side in new[] { GpoSide.Machine, GpoSide.User })
        {
            var prefix = GpoFileNames.FolderName(side) + ".";
            if (!flatName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var file = flatName[prefix.Length..];
            if (file == GpoFileNames.RegistryPol)
            {
                return GpoFileNames.PolPath(GpoRoot, side);
            }

            if (file == GpoFileNames.CommentCmtx)
            {
                return GpoFileNames.CmtxPath(GpoRoot, side);
            }
        }

        throw new ArgumentException("Not one of the five GPO files: " + flatName, nameof(flatName));
    }

    public string PolPath(GpoSide side) => GpoFileNames.PolPath(GpoRoot, side);

    public string CmtxPath(GpoSide side) => GpoFileNames.CmtxPath(GpoRoot, side);

    public string GptPath => GpoFileNames.GptIniPath(GpoRoot);

    /// <summary>Writes bytes to a full path inside the sandbox, creating the folder.</summary>
    /// <param name="path">A path under <see cref="GpoRoot"/>.</param>
    /// <param name="bytes">The content.</param>
    public void Seed(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(GpoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Seeding outside the sandbox GPO root: " + full);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>Copies a golden file into the sandbox under a flat name.</summary>
    /// <param name="flatName">The flat file name.</param>
    /// <param name="sourcePath">The golden file.</param>
    public void SeedFrom(string flatName, string sourcePath) => Seed(PathOf(flatName), File.ReadAllBytes(sourcePath));

    /// <summary>Reads a file of the sandbox, or null when it does not exist.</summary>
    /// <param name="flatName">The flat file name.</param>
    /// <returns>The bytes, or null.</returns>
    public byte[]? Read(string flatName)
    {
        var path = PathOf(flatName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Every file below the GPO root, as flat-free relative paths with forward slashes.</summary>
    /// <returns>The relative paths in ordinal order.</returns>
    public string[] FilesOnDisk() =>
        Directory.Exists(GpoRoot)
            ? Directory.GetFiles(GpoRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(GpoRoot, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>Copies a capture case folder into a scratch goldens root inside the sandbox run folder, for
    /// harness tests that damage it.</summary>
    /// <param name="goldenCaseFolder">The case folder in the repository.</param>
    /// <param name="caseId">The case id the copy is stored under.</param>
    /// <returns>The scratch goldens root.</returns>
    public string CopyCase(string goldenCaseFolder, string caseId)
    {
        var root = Path.Combine(RunRoot, "goldens-copy");
        var target = Path.Combine(root, caseId.Replace('/', Path.DirectorySeparatorChar));
        foreach (var file in Directory.GetFiles(goldenCaseFolder, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(goldenCaseFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return root;
    }

    /// <summary>Replaces the content of a file inside the sandbox run folder.</summary>
    /// <param name="path">A file under <see cref="RunRoot"/>.</param>
    /// <param name="bytes">The new content.</param>
    public void Overwrite(string path, byte[] bytes)
    {
        if (!Path.GetFullPath(path).StartsWith(RunRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Overwriting outside the sandbox: " + path);
        }

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Deletes a file inside the sandbox run folder.</summary>
    /// <param name="path">A file under <see cref="RunRoot"/>.</param>
    public void Remove(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(RunRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Deleting outside the sandbox: " + path);
        }

        File.Delete(path);
    }

    public bool BackupRootExists => Directory.Exists(Policy.BackupRoot);

    public void Dispose()
    {
        try
        {
            RemoveTree(RunRoot);
        }
        catch (Exception)
        {
            // A leftover temp folder is harmless; clean-up never fails a test.
        }
    }

    private static void RemoveTree(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, false);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                RemoveTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(folder, false);
    }
}
