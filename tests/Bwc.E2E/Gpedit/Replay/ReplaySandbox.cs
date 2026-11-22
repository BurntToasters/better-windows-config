using System.Text;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Bwc.Policy.Tests.Golden;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// The sandbox policy object of one replay or save test and every file operation the Replay tests do on it:
/// seeding the files
/// of a capture case, reading the five files back, holding the write lock, setting the read-only attribute. The
/// class names no
/// policy file: the five files come from <see cref="GpoFileNames"/> and <see cref="GoldenFiles"/>, so the test
/// classes that
/// drive the window hold no write API (scripts/check-policy-writer.js). It works inside the run data root of the
/// editor only
/// and never names the real policy folder.
/// </summary>
internal sealed class ReplaySandbox
{
    private readonly GpeditSandbox _sandbox;

    private ReplaySandbox(GpeditSandbox sandbox) => _sandbox = sandbox;

    internal GpeditSandbox Sandbox => _sandbox;

    internal string GpoRoot => _sandbox.GpoRoot;

    /// <summary>A sandbox for a test. The setting dialog is on, so the editor opens it for an edit request.</summary>
    /// <param name="name">The scope name: letters, digits, dots and hyphens.</param>
    internal static ReplaySandbox Create(string name)
    {
        var sandbox = GpeditSandbox.Create(name);
        sandbox.DialogEnabled = true;
        return new ReplaySandbox(sandbox);
    }

    /// <summary>The scope name for a capture case id such as <c>B/Pol_List_Plain</c> (underscores and slashes become
    /// hyphens).</summary>
    internal static string ScopeOf(string prefix, string caseId) =>
        (prefix + "-" + caseId).Replace('/', '-').Replace('_', '-').Replace(' ', '-').ToLowerInvariant();

    /// <summary>The path of a file by the flat name a golden step folder uses.</summary>
    internal string PathOf(string flatName)
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

    /// <summary>Writes the bytes of one file of the sandbox, creating its folder.</summary>
    internal void Seed(string flatName, byte[] bytes)
    {
        var full = Path.GetFullPath(PathOf(flatName));
        if (!full.StartsWith(GpoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Seeding outside the sandbox GPO root: " + full);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>
    /// Seeds what classic had on disk when the case's first operation ran: the first snapshot when the case opens with one,
    /// else the crafted start files. Classic creates a version file with an empty General section when it opens a GPO
    /// that has none (A/Machine_Cancel and the start snapshots of every group A case hold exactly that), and the editor only
    /// saves, so the replay does that open here (the same rule as the engine replay of the policy tests).
    /// </summary>
    internal void SeedStart(GoldenCase golden)
    {
        if (golden.Ops[0].Kind == "snapshot")
        {
            foreach (var name in GoldenFiles.Names.Where(n => golden.Steps[0].Files[n] is not null))
            {
                Seed(name, File.ReadAllBytes(golden.Steps[0].PathOf(name)));
            }

            return;
        }

        foreach (var name in golden.StartFiles.Keys)
        {
            Seed(name, File.ReadAllBytes(Path.Combine(golden.StartFolder, name)));
        }

        if (!golden.StartFiles.ContainsKey(GpoFileNames.GptIniFile))
        {
            Seed(GpoFileNames.GptIniFile, Encoding.ASCII.GetBytes("[General]\r\n"));
        }
    }

    /// <summary>The bytes of one file, or null when it does not exist.</summary>
    internal byte[]? Read(string flatName)
    {
        var path = PathOf(flatName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// The files below the GPO root that are none of the five, as relative paths. The record the sandbox refresher may append
    /// is not counted.
    /// </summary>
    internal string[] Extras()
    {
        if (!Directory.Exists(GpoRoot))
        {
            return [];
        }

        var known = GoldenFiles.Names.Select(n => Path.GetRelativePath(GpoRoot, PathOf(n)).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        return Directory.GetFiles(GpoRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(GpoRoot, f).Replace('\\', '/'))
            .Where(f => !known.Contains(f) && f != SandboxGpoStorePolicy.RefreshRecordFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The lock file the store of the sandbox takes for a save.</summary>
    internal string LockPath => new SandboxGpoStorePolicy(GpoRoot, _sandbox.DataRoot, null, new NoRefresh()).LockPath;

    /// <summary>Takes the write lock as another process would: the file is opened with no sharing until the handle is
    /// disposed.</summary>
    internal FileStream HoldLock()
    {
        var path = LockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None, 1, FileOptions.None);
    }

    /// <summary>Sets or clears the read-only attribute of the policy file of a side.</summary>
    internal void SetPolReadOnly(GpoSide side, bool readOnly)
    {
        var path = GpoFileNames.PolPath(GpoRoot, side);
        File.SetAttributes(path, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);
    }

    /// <summary>True when the policy file of a side exists and has the read-only attribute.</summary>
    internal bool PolIsReadOnly(GpoSide side)
    {
        var path = GpoFileNames.PolPath(GpoRoot, side);
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
    }

    private sealed class NoRefresh : IPolicyRefresher
    {
        public void Refresh(GpoSide side)
        {
            // Only the lock path of the policy is wanted here.
        }
    }
}
