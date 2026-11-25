using Bwc.Gpo.Refresh;
using Bwc.Update;

namespace Bwc.Gpo.Store;

/// <summary>
/// The shipping policy: the machine's real local group policy folder, the backups under the common application data
/// folder, the write lock file in the backup root and the real refresher. Builds only these paths; reads no
/// environment variable. The backup root sits below the BWC data root, which a standard user could pre-create, so the
/// data root and every folder down to the backup root must be protected folders (<see cref="ProtectedDataRoot"/>):
/// checked before any backup is made and again for every write or removal under the backup root. The cases are in
/// docs/failure-modes/gpo-store.md, section K.
/// </summary>
public sealed class SystemGpoStorePolicy : IGpoStorePolicy
{
    private readonly string[] _roots;
    private readonly string _dataRoot;
    private readonly ProtectedAcl _acl;

    /// <summary>Creates the policy for this machine.</summary>
    public SystemGpoStorePolicy()
    {
        GpoRoot = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "GroupPolicy"));
        _dataRoot = Path.GetFullPath(ProtectedDataRoot.DefaultPath);
        _acl = ProtectedAcl.Ship();
        BackupRoot = Path.GetFullPath(Path.Combine(_dataRoot, "gpedit", "backup"));
        Refresher = new UserenvPolicyRefresher();
        _roots = [GpoRoot, BackupRoot];
    }

    /// <summary>Creates the policy over other folders. Tests only: nothing real is touched.</summary>
    internal SystemGpoStorePolicy(string gpoRoot, string dataRoot, ProtectedAcl acl, IPolicyRefresher refresher)
    {
        GpoRoot = Path.GetFullPath(gpoRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _acl = acl;
        BackupRoot = Path.GetFullPath(Path.Combine(_dataRoot, "gpedit", "backup"));
        Refresher = refresher;
        _roots = [GpoRoot, BackupRoot];
    }

    /// <inheritdoc />
    public string GpoRoot { get; }

    /// <inheritdoc />
    public string BackupRoot { get; }

    /// <inheritdoc />
    public string LockPath => Path.Combine(BackupRoot, "write.lock");

    /// <inheritdoc />
    public IPolicyRefresher Refresher { get; }

    /// <inheritdoc />
    public void EnsureWriteAllowed(string path)
    {
        GpoPathChecks.Check(path, _roots, removing: false);
        VerifyBackupChainFor(path, "Write");
    }

    /// <inheritdoc />
    public void EnsureRemoveAllowed(string path)
    {
        GpoPathChecks.Check(path, _roots, removing: true);
        VerifyBackupChainFor(path, "Remove");
    }

    /// <summary>
    /// Creates the data root, <c>gpedit</c> and the backup root with the protected access list when missing, and refuses
    /// a root or a folder that is not protected. Nothing is created through a folder that fails.
    /// </summary>
    /// <exception cref="GpoStoreException">A folder is refused (policy denied) or cannot be created.</exception>
    internal void EnsureBackupRoot(string operation) =>
        ThrowIfProblem(ProtectedDataRoot.Ensure(_dataRoot, BackupRoot, _acl), operation);

    /// <summary>
    /// The same check that creates nothing. A folder that does not exist yet is not a problem (there is nothing to pass
    /// through).
    /// </summary>
    /// <exception cref="GpoStoreException">An existing folder is refused.</exception>
    internal void VerifyBackupRoot(string operation)
    {
        var problem = ProtectedDataRoot.Verify(_dataRoot, BackupRoot, _acl);
        if (problem is not null && problem.Status != ProtectedFolderStatus.Missing)
        {
            ThrowIfProblem(problem, operation);
        }
    }

    private void VerifyBackupChainFor(string path, string operation)
    {
        if (GpoPathChecks.TryNormalize(path, out var full) && GpoPathChecks.IsSameOrUnder(BackupRoot, full))
        {
            VerifyBackupRoot(operation);
        }
    }

    private static void ThrowIfProblem(ProtectedFolderProblem? problem, string operation)
    {
        if (problem is null)
        {
            return;
        }

        if (problem.Error is not null)
        {
            throw GpoStoreException.FromIo(operation, problem.Folder, problem.Error);
        }

        throw GpoPathChecks.Denied(operation, problem.Folder, "it is not a protected folder: " + problem.Describe());
    }
}
