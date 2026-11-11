using System.Net;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Owns the read-only HKEY_LOCAL_MACHINE and HKEY_USERS handles for one remote computer.
/// Readers created from this connection borrow those handles and never close them.
/// </summary>
public sealed class RemoteRegistryConnection : IDisposable
{
    private const int MaxComputerNameLength = 255;
    private const string ConnectOperation = "Connect";

    private readonly SafeRegistryKeyHandle _localMachine;
    private readonly SafeRegistryKeyHandle _users;
    private int _disposed;

    private RemoteRegistryConnection(
        string computerName,
        SafeRegistryKeyHandle localMachine,
        SafeRegistryKeyHandle users)
    {
        ComputerName = computerName;
        _localMachine = localMachine;
        _users = users;
    }

    public string ComputerName { get; }

    /// <summary>
    /// Returns true when the name identifies this computer. The check is ordinal-ignore-case
    /// against the machine name, its short form, the DNS host name, and the DNS short name.
    /// DNS lookup is best effort: a failure returns false and never throws.
    /// </summary>
    public static bool IsLocalComputer(string computerName)
    {
        if (string.IsNullOrWhiteSpace(computerName))
        {
            return false;
        }

        string name = computerName.Trim();

        if (name is ".")
        {
            return true;
        }

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string machineName = Environment.MachineName;
        if (name.Equals(machineName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(ShortName(machineName), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? dnsHostName = TryGetDnsHostName();
        if (dnsHostName is null)
        {
            return false;
        }

        return name.Equals(dnsHostName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(ShortName(dnsHostName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens read-only HKEY_LOCAL_MACHINE and HKEY_USERS handles for a remote computer.
    /// A name that identifies this computer is rejected before any native call.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="computerName"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, too long, or contains NUL or a separator.</exception>
    /// <exception cref="RemoteRegistryException">
    /// The name is the local computer, or a native connection failed.
    /// </exception>
    public static RemoteRegistryConnection Connect(string computerName)
    {
        ArgumentNullException.ThrowIfNull(computerName);

        string name = computerName.Trim();
        ValidateComputerName(name);

        if (IsLocalComputer(name))
        {
            throw new RemoteRegistryException(
                name,
                ConnectOperation,
                0,
                RemoteRegistryFailureReason.OwnComputer);
        }

        SafeRegistryKeyHandle localMachine = ConnectRoot(
            name,
            RegistryRoot.LocalMachine,
            RemoteRegistryFailureReason.ConnectFailed);

        try
        {
            SafeRegistryKeyHandle users = ConnectRoot(
                name,
                RegistryRoot.Users,
                RemoteRegistryFailureReason.PartialConnection);

            return new RemoteRegistryConnection(name, localMachine, users);
        }
        catch
        {
            DisposeHandle(localMachine);
            throw;
        }
    }

    internal SafeRegistryKeyHandle GetRoot(RegistryRoot root)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        SafeRegistryKeyHandle handle = root switch
        {
            RegistryRoot.LocalMachine => _localMachine,
            RegistryRoot.Users => _users,
            _ => throw new ArgumentException(
                "A remote registry reader supports only HKEY_LOCAL_MACHINE and HKEY_USERS.",
                nameof(root)),
        };

        return SafeRegistryKeyHandle.FromBorrowed(handle.DangerousGetHandle());
    }

    public RegistryReader CreateReader()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new RegistryReader(this);
    }

    /// <summary>
    /// Closes both owned root handles. The operation is idempotent and never throws.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeHandle(_localMachine);
        DisposeHandle(_users);
    }

    private static unsafe SafeRegistryKeyHandle ConnectRoot(
        string computerName,
        RegistryRoot root,
        RemoteRegistryFailureReason failureReason)
    {
        HKEY connected;
        WIN32_ERROR error;

        fixed (char* computerNamePointer = computerName)
        {
            error = PInvoke.RegConnectRegistry(
                new PCWSTR(computerNamePointer),
                (HKEY)root.ToHkey(),
                &connected);
        }

        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new RemoteRegistryException(
                computerName,
                $"RegConnectRegistry({root.DisplayName()})",
                (int)error,
                failureReason);
        }

        return SafeRegistryKeyHandle.FromOwned((nint)connected.Value);
    }

    private static void ValidateComputerName(string name)
    {
        if (name.Length == 0)
        {
            throw new ArgumentException("A remote computer name is required.", "computerName");
        }

        if (name.Length > MaxComputerNameLength)
        {
            throw new ArgumentException(
                $"A remote computer name must be at most {MaxComputerNameLength} characters.",
                "computerName");
        }

        foreach (char character in name)
        {
            if (character is '\0' or '\\' or '/')
            {
                throw new ArgumentException(
                    "A remote computer name must not contain NUL, backslash, or slash.",
                    "computerName");
            }
        }
    }

    private static string? TryGetDnsHostName()
    {
        try
        {
            string hostName = Dns.GetHostName();
            return string.IsNullOrWhiteSpace(hostName) ? null : hostName;
        }
        catch (Exception)
        {
            // DNS is best effort. A failure must not block a valid remote name.
            return null;
        }
    }

    private static string ShortName(string hostName)
    {
        int dot = hostName.IndexOf('.');
        return dot > 0 ? hostName[..dot] : hostName;
    }

    private static void DisposeHandle(SafeRegistryKeyHandle handle)
    {
        try
        {
            handle.Dispose();
        }
        catch (Exception)
        {
            // Dispose must not throw, even when the close call fails.
        }
    }
}
