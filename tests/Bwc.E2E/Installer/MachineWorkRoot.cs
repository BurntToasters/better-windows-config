using System.ComponentModel;
using System.Security.Cryptography;

namespace Bwc.E2E.Installer;

/// <summary>
/// The one folder the Elevated installer tests work in: <c>%ProgramFiles%\BWC-E2E\work-id</c>, a place a standard
/// user cannot
/// write (docs/failure-modes/installer-e2e.md, 82 to 87). It holds a hashed copy of each setup (<c>inputs</c>), the working
/// folder of every run, and the legacy per-user sandbox of the upgrade test, so nothing the elevated tests run,
/// start in, or
/// delete recursively sits in a folder the user controls. Program Files comes from the shell's known folder. A
/// parent that is
/// a link, is owned by another account, or that standard users can write is refused, and the tests are not run.
/// </summary>
internal sealed class MachineWorkRoot
{
    private const string ParentName = "BWC-E2E";
    private const string WorkPrefix = "work-";
    private const string InputsName = "inputs";
    private static readonly object Gate = new();
    private static MachineWorkRoot? _current;

    private MachineWorkRoot(string parent, string path, MachineInputs staged)
    {
        Parent = parent;
        Path = path;
        Staged = staged;
    }

    /// <summary>%ProgramFiles%\BWC-E2E.</summary>
    public string Parent { get; }

    /// <summary>%ProgramFiles%\BWC-E2E\work-id.</summary>
    public string Path { get; }

    /// <summary>The inputs with each setup path pointing at the hashed copy.</summary>
    public MachineInputs Staged { get; }

    /// <summary>The work folder of this test run. Only valid after <see cref="TryEnsure"/> returned it.</summary>
    public static MachineWorkRoot Required =>
        _current ?? throw new InvalidOperationException("The work folder does not exist: RequireReady must pass first.");

    /// <summary>
    /// Creates the work folder and the copies on the first call, and returns them on later calls. Null with the reason when
    /// the parent is not safe or a copy does not match its recorded hash: the caller reports NOT RUN.
    /// </summary>
    public static MachineWorkRoot? TryEnsure(MachineInputs source, out string reason)
    {
        lock (Gate)
        {
            if (_current is not null)
            {
                reason = string.Empty;
                return _current;
            }

            string? created = null;
            try
            {
                string parent = System.IO.Path.Combine(MachineNative.ProgramFilesFolder(), ParentName);
                string? parentProblem = FolderProblem(parent, mustExist: false);
                if (parentProblem is not null)
                {
                    reason = "Not run: " + parentProblem;
                    return null;
                }

                string work = System.IO.Path.Combine(parent, WorkPrefix + Guid.NewGuid().ToString("N")[..10]);
                _ = Directory.CreateDirectory(work);
                created = work;
                string? workProblem = FolderProblem(work, mustExist: true);
                if (workProblem is not null)
                {
                    reason = "Not run: " + workProblem;
                    Remove(work);
                    return null;
                }

                string inputs = System.IO.Path.Combine(work, InputsName);
                _ = Directory.CreateDirectory(inputs);
                MachineInputs.SetupFile? a = Copy(source.A, inputs, out string problemA);
                MachineInputs.SetupFile? b = Copy(source.B, inputs, out string problemB);
                if (a is null || b is null)
                {
                    reason = "Not run: " + (a is null ? problemA : problemB);
                    Remove(work);
                    return null;
                }

                _current = new MachineWorkRoot(parent, work, source with { A = a, B = b });
                reason = string.Empty;
                return _current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or System.Runtime.InteropServices.COMException)
            {
                if (created is not null)
                {
                    Remove(created);
                }

                reason = "Not run: the work folder under Program Files could not be prepared (" + ex.GetType().Name + ": " + ex.Message + ")";
                return null;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is inside the work folder (not the work folder itself) by its full name, with no link
    /// on the way from the work folder down. Every recursive delete of an Elevated run goes through this.
    /// </summary>
    public bool Owns(string path)
    {
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        string work = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
        if (!full.StartsWith(work + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string walk = full;
        while (walk.Length > work.Length)
        {
            if (Directory.Exists(walk) && new DirectoryInfo(walk).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            walk = System.IO.Path.GetDirectoryName(walk) ?? work;
        }

        return true;
    }

    /// <summary>
    /// Removes the work folder, then the BWC-E2E parent when it is empty. Records one check, so a folder that cannot be removed
    /// names its path. Does nothing when no test passed the gates.
    /// </summary>
    public static void Cleanup()
    {
        MachineWorkRoot? current;
        lock (Gate)
        {
            current = _current;
            _current = null;
        }

        if (current is null)
        {
            return;
        }

        bool removed = Remove(current.Path);
        bool parentOk = true;
        try
        {
            if (Directory.Exists(current.Parent) && !Directory.EnumerateFileSystemEntries(current.Parent).Any())
            {
                Directory.Delete(current.Parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            parentOk = false;
        }

        MachineChecks.Record(
            "cleanup.workRoot",
            removed && parentOk,
            removed
                ? "Removed " + current.Path + (parentOk ? " (and " + current.Parent + " when it was empty)." : "; " + current.Parent + " could not be removed.")
                : "The work folder is still there: " + current.Path + ". Remove it by hand.");
    }

    /// <summary>
    /// Why a folder cannot be used, or null: it is a link, its owner is not Administrators, SYSTEM, or TrustedInstaller, or an
    /// allow entry gives Users, Authenticated Users, or Everyone a write right. A missing parent is created first.
    /// </summary>
    private static string? FolderProblem(string folder, bool mustExist)
    {
        if (!Directory.Exists(folder))
        {
            if (mustExist)
            {
                return folder + " does not exist.";
            }

            _ = Directory.CreateDirectory(folder);
        }

        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return folder + " is a link.";
        }

        MachineNative.FolderAcl acl = MachineNative.ReadAcl(folder);
        if (!MachineNative.OwnerIsTrusted(acl))
        {
            return $"{folder} is owned by {acl.Owner?.Value ?? "(unknown)"}, not by Administrators or SYSTEM.";
        }

        IReadOnlyList<string> writers = MachineNative.UserWriteEntries(acl);
        return writers.Count == 0 ? null : folder + " can be written by standard users: " + string.Join("; ", writers);
    }

    /// <summary>
    /// Copies one setup into the inputs folder and compares the bytes that were written with the recorded size and SHA-256.
    /// The source is read once, hashed, and written, so what is hashed is what is written; the copy is read back.
    /// </summary>
    private static MachineInputs.SetupFile? Copy(MachineInputs.SetupFile source, string inputs, out string problem)
    {
        problem = string.Empty;
        byte[] bytes = File.ReadAllBytes(source.Path);
        string readHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != source.Size || !string.Equals(readHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            problem = source.Path + " changed after the inputs were checked (size or SHA-256 differs).";
            return null;
        }

        string target = System.IO.Path.Combine(inputs, System.IO.Path.GetFileName(source.Path));
        File.WriteAllBytes(target, bytes);
        string copyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
        if (new FileInfo(target).Length != source.Size || !string.Equals(copyHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            problem = "the copy " + target + " does not match the recorded SHA-256.";
            return null;
        }

        return new MachineInputs.SetupFile(target, source.Version, source.Size, source.Sha256);
    }

    /// <summary>
    /// Deletes a folder this class created (a name under the BWC-E2E parent that starts with work-), retrying
    /// briefly.
    /// </summary>
    private static bool Remove(string folder)
    {
        string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        string? parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        if (!name.StartsWith(WorkPrefix, StringComparison.Ordinal)
            || parent is null
            || !string.Equals(System.IO.Path.GetFileName(parent), ParentName, StringComparison.OrdinalIgnoreCase)
            || (Directory.Exists(folder) && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            return !Directory.Exists(folder);
        }

        for (int attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }

        return !Directory.Exists(folder);
    }
}
