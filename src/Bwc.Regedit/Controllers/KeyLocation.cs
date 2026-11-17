using Bwc.Regedit.Registry;
using Bwc.Registry;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// The key a command acts on: the local registry (<see cref="Connection"/> is null) or one connected remote
/// computer. <see cref="Path"/> is in local form (HKEY_LOCAL_MACHINE\...). <see cref="Store"/> is the store
/// the command reads and writes through, so a remote command never reaches the local registry.
/// </summary>
internal sealed record KeyLocation(RegistryPath Path, RegistryStore Store, RemoteRegistryConnection? Connection)
{
    public bool IsRemote => Connection is not null;

    /// <summary>The reader for this key. Remote keys read through the connection's store.</summary>
    public RegistryReader Reader => Store.Reader;

    /// <summary>
    /// The display path of the key: <c>Computer\HKEY_...</c> locally, <c>\\COMPUTER\HKEY_...</c> remotely. This is the
    /// tree node path and the form the Copy Key Name command copies.
    /// </summary>
    public string DisplayPath => DisplayOf(Path);

    /// <summary>The display path of another key on the same computer, such as a child or a renamed key.</summary>
    public string DisplayOf(RegistryPath key) => Connection is null
        ? key.ToDisplayString()
        : RemoteRegistryTreeSource.DisplayPathOf(RemoteRegistryTreeSource.DisplayPrefix(Connection.ComputerName), key);
}
