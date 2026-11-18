using Bwc.Gpo.Refresh;

namespace Bwc.Gpo.Store;

/// <summary>
/// The write policy of the store: where the GPO and its backups live, the name of the lock, the refresher, and the
/// check every write passes first. Implementations never read an environment variable. The tool picks the
/// implementation: the system one in the ship flavor, a sandbox one in the E2E flavor, or deny-all.
/// </summary>
public interface IGpoStorePolicy
{
    /// <summary>The GPO folder (the system GroupPolicy folder or a sandbox root).</summary>
    string GpoRoot { get; }

    /// <summary>The folder that receives the timestamped backup folders.</summary>
    string BackupRoot { get; }

    /// <summary>The name of the named mutex that serializes saves.</summary>
    string LockName { get; }

    /// <summary>The object that refreshes policy after a save.</summary>
    IPolicyRefresher Refresher { get; }

    /// <summary>
    /// Called before a file or folder is created, replaced or written. Throws <see cref="GpoStoreException"/> with
    /// reason <see cref="GpoStoreFailure.PolicyDenied"/> when the path is not allowed. A path that is itself a reparse
    /// point is refused.
    /// </summary>
    /// <param name="path">The path about to be written.</param>
    void EnsureWriteAllowed(string path);

    /// <summary>
    /// Called before a file or folder is removed. Same rules as <see cref="EnsureWriteAllowed"/> except that the final
    /// component may be a reparse point: removing a link removes the link, never its target.
    /// </summary>
    /// <param name="path">The path about to be removed.</param>
    void EnsureRemoveAllowed(string path);
}
