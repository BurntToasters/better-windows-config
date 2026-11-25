using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Update;

namespace Bwc.Gpo.Store;

/// <summary>What a look at one protected folder found.</summary>
public enum ProtectedFolderStatus
{
    /// <summary>The folder exists, is not a link and passes the access rules.</summary>
    Ready,

    /// <summary>The folder does not exist.</summary>
    Missing,

    /// <summary>The path is a file.</summary>
    NotADirectory,

    /// <summary>The folder is a reparse point (or its attributes cannot be read, which counts as one).</summary>
    ReparsePoint,

    /// <summary>The owner is not a trusted owner. The detail says who owns the folder.</summary>
    OwnerRejected,

    /// <summary>The owner is trusted but the access list is not acceptable. The detail says why.</summary>
    AclRejected,

    /// <summary>The owner or the access list cannot be read.</summary>
    Unreadable,

    /// <summary>The folder could not be created.</summary>
    CreateFailed,
}

/// <summary>The first folder of a chain that is not acceptable.</summary>
/// <param name="Folder">The full path of the folder that failed.</param>
/// <param name="Status">Why it failed.</param>
/// <param name="Detail">The reason for a rejected access list, or the HRESULT of an unreadable folder.</param>
/// <param name="Error">The I/O error behind <see cref="ProtectedFolderStatus.CreateFailed"/>, else null.</param>
public sealed record ProtectedFolderProblem(string Folder, ProtectedFolderStatus Status, string Detail, Exception? Error = null)
{
    /// <summary>A short reason that does not repeat the folder name.</summary>
    public string Describe() =>
        Status switch
        {
            ProtectedFolderStatus.Missing => "the folder does not exist",
            ProtectedFolderStatus.NotADirectory => "the path is a file",
            ProtectedFolderStatus.ReparsePoint => "the folder is a reparse point",
            ProtectedFolderStatus.OwnerRejected or ProtectedFolderStatus.AclRejected => Detail,
            ProtectedFolderStatus.Unreadable => "the folder cannot be inspected (" + Detail + ")",
            ProtectedFolderStatus.CreateFailed => "the folder cannot be created (" + (Error?.Message ?? Detail) + ")",
            _ => string.Empty,
        };
}

/// <summary>
/// Creates and checks the BWC data root (<c>%ProgramData%\BetterWindowsConfig</c>) and every folder below it down to a
/// target. A standard user may create folders in <c>%ProgramData%</c>, so a root that this process did not make may belong
/// to that user. The root and each folder down to the target must have a trusted owner, an access list that passes
/// <see cref="ProtectedAcl.Inspect"/> and must not be a reparse point. A missing folder is created with the protected
/// access list (<see cref="ProtectedAcl.NewSecurity"/>); a folder that fails is never repaired and nothing is created
/// through it. The caller names the root and the target, so this file names no policy folder
/// (docs/failure-modes/gpo-store.md, section K; docs/failure-modes/template-store.md, section H).
/// </summary>
public static class ProtectedDataRoot
{
    /// <summary>The name of the data root folder under the common application data folder.</summary>
    public const string FolderName = "BetterWindowsConfig";

    /// <summary>The data root of this machine: the common application data folder plus <see cref="FolderName"/>.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);

    /// <summary>
    /// A read-only look at one folder: reparse point first (so a dangling link is never read as missing), then file,
    /// missing, owner and access list.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="acl">The owner and access rules.</param>
    /// <returns>The status, and the reason or HRESULT for a rejected or unreadable folder.</returns>
    public static (ProtectedFolderStatus Status, string Detail) InspectFolder(string folder, ProtectedAcl acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        try
        {
            if (GpoPathChecks.IsReparsePoint(folder))
            {
                return (ProtectedFolderStatus.ReparsePoint, folder);
            }

            if (File.Exists(folder))
            {
                return (ProtectedFolderStatus.NotADirectory, folder);
            }

            if (!Directory.Exists(folder))
            {
                return (ProtectedFolderStatus.Missing, string.Empty);
            }

            var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var why = acl.Inspect(security);
            if (why is null)
            {
                return (ProtectedFolderStatus.Ready, string.Empty);
            }

            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return (acl.IsTrustedOwner(owner) ? ProtectedFolderStatus.AclRejected : ProtectedFolderStatus.OwnerRejected, why);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
        {
            return (ProtectedFolderStatus.Unreadable, "0x" + e.HResult.ToString("X8", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Creates what is missing from the root down to the target, each with the protected access list, and checks every
    /// folder on the way (the root included). Stops at the first folder that fails.
    /// </summary>
    /// <param name="root">The data root (an absolute path).</param>
    /// <param name="target">The root itself or a folder below it.</param>
    /// <param name="acl">The owner and access rules: ship rules in the ship build, current user rules in tests.</param>
    /// <returns>Null when every folder is acceptable, else the first folder that is not.</returns>
    /// <exception cref="ArgumentException">A path is not absolute, or the target is not the root or below it.</exception>
    public static ProtectedFolderProblem? Ensure(string root, string target, ProtectedAcl acl) => Walk(root, target, acl, create: true);

    /// <summary>
    /// The same walk as <see cref="Ensure"/> that creates nothing. A folder that does not exist is reported as
    /// <see cref="ProtectedFolderStatus.Missing"/>; the caller decides whether that matters.
    /// </summary>
    /// <param name="root">The data root (an absolute path).</param>
    /// <param name="target">The root itself or a folder below it.</param>
    /// <param name="acl">The owner and access rules.</param>
    /// <returns>Null when every folder exists and is acceptable, else the first folder that is not.</returns>
    /// <exception cref="ArgumentException">A path is not absolute, or the target is not the root or below it.</exception>
    public static ProtectedFolderProblem? Verify(string root, string target, ProtectedAcl acl) => Walk(root, target, acl, create: false);

    private static ProtectedFolderProblem? Walk(string root, string target, ProtectedAcl acl, bool create)
    {
        ArgumentNullException.ThrowIfNull(acl);
        var fullRoot = Absolute(root, nameof(root));
        var fullTarget = Absolute(target, nameof(target));
        if (!GpoPathChecks.IsSameOrUnder(fullRoot, fullTarget))
        {
            throw new ArgumentException("The target is not the root or a folder below it.", nameof(target));
        }

        var folders = new List<string> { fullRoot };
        var current = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, fullTarget).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            folders.Add(current);
        }

        foreach (var folder in folders)
        {
            var (status, detail) = InspectFolder(folder, acl);
            if (status == ProtectedFolderStatus.Missing && create)
            {
                try
                {
                    acl.NewSecurity().CreateDirectory(folder);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return new ProtectedFolderProblem(folder, ProtectedFolderStatus.CreateFailed, "0x" + e.HResult.ToString("X8", CultureInfo.InvariantCulture), e);
                }

                (status, detail) = InspectFolder(folder, acl);
            }

            if (status != ProtectedFolderStatus.Ready)
            {
                return new ProtectedFolderProblem(folder, status, detail);
            }
        }

        return null;
    }

    private static string Absolute(string path, string name)
    {
        if (!Path.IsPathRooted(path) || !GpoPathChecks.TryNormalize(path, out var full))
        {
            throw new ArgumentException("The path is empty, relative or not valid.", name);
        }

        return full;
    }
}
