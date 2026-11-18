using Bwc.Gpo.Refresh;

namespace Bwc.Gpo.Store;

/// <summary>
/// The shipping policy: the machine's real local group policy folder, the backups under the common application data
/// folder, the global lock and the real refresher. Builds only these paths; reads no environment variable.
/// </summary>
public sealed class SystemGpoStorePolicy : IGpoStorePolicy
{
    private readonly string[] _roots;

    /// <summary>Creates the policy for this machine.</summary>
    public SystemGpoStorePolicy()
    {
        GpoRoot = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "GroupPolicy"));
        BackupRoot = Path.GetFullPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterWindowsConfig", "gpedit", "backup"));
        Refresher = new UserenvPolicyRefresher();
        _roots = [GpoRoot, BackupRoot];
    }

    /// <inheritdoc />
    public string GpoRoot { get; }

    /// <inheritdoc />
    public string BackupRoot { get; }

    /// <inheritdoc />
    public string LockName => "Global\\BetterWindowsConfig.GpoWrite";

    /// <inheritdoc />
    public IPolicyRefresher Refresher { get; }

    /// <inheritdoc />
    public void EnsureWriteAllowed(string path) => GpoPathChecks.Check(path, _roots, removing: false);

    /// <inheritdoc />
    public void EnsureRemoveAllowed(string path) => GpoPathChecks.Check(path, _roots, removing: true);
}
