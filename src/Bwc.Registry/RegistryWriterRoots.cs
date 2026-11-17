namespace Bwc.Registry;

/// <summary>
/// Where a <see cref="RegistryWriter"/> gets its root keys. The local source opens the predefined roots, which
/// need no pin. A remote source serves HKEY_LOCAL_MACHINE and HKEY_USERS from one
/// <see cref="RemoteRegistryConnection"/> and pins the root for each open.
/// </summary>
internal sealed class RegistryWriterRoots
{
    private readonly RemoteRegistryConnection? _connection;

    internal static RegistryWriterRoots Local { get; } = new(null);

    internal RegistryWriterRoots(RemoteRegistryConnection? connection)
    {
        _connection = connection;
    }

    /// <summary>True when the writer's keys live on a remote computer.</summary>
    internal bool IsRemote => _connection is not null;

    /// <summary>The computer name passed to the policy: null for the local registry.</summary>
    internal string? ComputerName => _connection?.ComputerName;

    /// <summary>
    /// Pins the remote root for one open. Returns null for a local root, because the caller then opens the
    /// predefined key. Throws <see cref="ArgumentException"/> for a root the remote connection does not serve,
    /// and <see cref="ObjectDisposedException"/> after the connection is disposed. Both happen before any native call.
    /// </summary>
    internal SafeRegistryKeyHandle? Pin(RegistryRoot root) => _connection?.GetRoot(root);
}
