using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;

namespace Bwc.Gpo.Tests;

/// <summary>A refresher that records calls in the shared log and can be told to fail.</summary>
internal sealed class SpyRefresher(List<string> log) : IPolicyRefresher
{
    public List<GpoSide> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public void Refresh(GpoSide side)
    {
        Calls.Add(side);
        log.Add("refresh:" + side);
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>
/// A temp sandbox for one test: a run data root, a GPO root inside it, a sibling backup root, a spy refresher and a
/// log of every write. The only helper in the test project that deletes folders and changes attributes, so the test
/// classes that name policy paths hold no write API (scripts/check-policy-writer.js). Never touches the real GPO folder.
/// </summary>
internal sealed class SandboxGpoFixture : IDisposable
{
    private readonly List<IDisposable> _holds = [];

    public SandboxGpoFixture()
    {
        RunRoot = Path.Combine(Path.GetTempPath(), "bwc-gpo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RunRoot);
        GpoRoot = Path.Combine(RunRoot, "gpo");
        BackupRoot = GpoRoot + ".backup";
        Refresher = new SpyRefresher(Log);
        Policy = new SandboxGpoStorePolicy(GpoRoot, RunRoot, null, Refresher);
        Seeder = new GpoFileWriter(Policy);
    }

    public string RunRoot { get; }

    public string GpoRoot { get; }

    public string BackupRoot { get; }

    public List<string> Log { get; } = [];

    public List<GpoWriteEvent> Events { get; } = [];

    public SpyRefresher Refresher { get; }

    public SandboxGpoStorePolicy Policy { get; }

    public GpoFileWriter Seeder { get; }

    public string PolPath(GpoSide side) => GpoFileNames.PolPath(GpoRoot, side);

    public string CmtxPath(GpoSide side) => GpoFileNames.CmtxPath(GpoRoot, side);

    public string GptPath => GpoFileNames.GptIniPath(GpoRoot);

    public GpoStore NewStore(Action<GpoStoreOptions>? configure = null)
    {
        var options = new GpoStoreOptions
        {
            WriteObserver = e =>
            {
                Events.Add(e);
                if (e.Kind == GpoWriteKind.FileWritten)
                {
                    Log.Add("write:" + Path.GetRelativePath(GpoRoot, e.Path).Replace('\\', '/'));
                }
            },
            LockTimeout = TimeSpan.FromSeconds(5),
            WriteRetryCount = 2,
            WriteRetryDelay = TimeSpan.FromMilliseconds(10),
        };
        configure?.Invoke(options);
        return new GpoStore(Policy, options);
    }

    public void Seed(string path, byte[] bytes) => Seeder.WriteAtomic(path, bytes);

    public byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public string[] BackupFolders() =>
        Directory.Exists(BackupRoot)
            ? Directory.GetDirectories(BackupRoot).Select(Path.GetFileName).Select(n => n!).Order(StringComparer.Ordinal).ToArray()
            : [];

    public string[] LeftoverTempFiles() =>
        Directory.Exists(GpoRoot)
            ? Directory.GetFiles(GpoRoot, "*.tmp", SearchOption.AllDirectories)
            : [];

    public void MakeFolder(string path) => Directory.CreateDirectory(path);

    public void PutRawFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    /// <summary>Keeps a handle open that allows readers and writers but not delete or rename.</summary>
    public void HoldOpenWithoutDelete(string path)
    {
        _holds.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    }

    /// <summary>Keeps a file open with no sharing at all, so nothing else can read, write or replace it.</summary>
    public void HoldExclusive(string path)
    {
        _holds.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
    }

    /// <summary>
    /// Takes the sandbox write lock the way a second BWC process would: creates the lock file in the backup root when it
    /// is missing and holds it with no sharing. Disposing the result frees the lock and leaves the file in place.
    /// </summary>
    public IDisposable HoldLockFile()
    {
        Directory.CreateDirectory(BackupRoot);
        var hold = new FileStream(Policy.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _holds.Add(hold);
        return hold;
    }

    /// <summary>Opens a file for reading with every sharing mode allowed; fails when another handle excludes readers.</summary>
    public static void OpenSharedForRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>Creates a file symbolic link; false when the account may not create one.</summary>
    public static bool TryMakeFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void ReleaseHolds()
    {
        foreach (var hold in _holds)
        {
            hold.Dispose();
        }

        _holds.Clear();
    }

    /// <summary>Creates a junction at <paramref name="link"/> to <paramref name="target"/>; false when mklink fails.</summary>
    public bool TryMakeJunction(string link, string target)
    {
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    public void RemoveJunction(string link)
    {
        if (Directory.Exists(link))
        {
            Directory.Delete(link, false);
        }
    }

    /// <summary>Adds an explicit allow rule for the Guests group to a file and returns that SID.</summary>
    public SecurityIdentifier AddExplicitRule(string path)
    {
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData, AccessControlType.Allow));
        info.SetAccessControl(security);
        return sid;
    }

    public bool HasExplicitRule(string path, SecurityIdentifier sid)
    {
        var security = new FileInfo(path).GetAccessControl();
        return security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow);
    }

    public void Dispose()
    {
        ReleaseHolds();
        try
        {
            RemoveTree(RunRoot);
        }
        catch (Exception)
        {
            // A leftover temp folder is harmless; never fail a test on clean-up.
        }
    }

    /// <summary>Removes a folder tree, deleting a junction or symlink as a link and never following it.</summary>
    private static void RemoveTree(string folder)
    {
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
