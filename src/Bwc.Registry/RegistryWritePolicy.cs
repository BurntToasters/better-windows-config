using System.Net;
using System.Security.Principal;

namespace Bwc.Registry;

public enum RegistryWriteOperation
{
    CreateKey,
    SetValue,
    DeleteKey,
    DeleteValue,
    RenameKey,
    CopyTree,
    LoadHive,
    UnloadHive,
    SaveKey,
    ReplaceKey,
    RestoreKey,
    SetKeySecurity,
}

public interface IRegistryWritePolicy
{
    bool AllowsLinkValues { get; }

    /// <summary>
    /// True when the writer must open every key on the path without following symbolic links and
    /// refuse any write that passes through one. The default is false, which keeps the
    /// follow-link behavior of regedit.
    /// </summary>
    bool RejectsSymbolicLinks => false;

    /// <summary>
    /// Decides one write before any native call. <paramref name="computerName"/> is null for the local
    /// registry, or the computer name the remote connection was made with. The path is always absolute.
    /// </summary>
    void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation);
}

/// <summary>Allows every path, local or remote. Used outside test runs.</summary>
public sealed class ShippingWritePolicy : IRegistryWritePolicy
{
    public bool AllowsLinkValues => true;

    public bool RejectsSymbolicLinks => false;

    public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);
    }
}

/// <summary>
/// Refuses every mutation before any native code runs, local or remote. The Registry Editor uses it in E2E
/// builds when the sandbox root is missing or invalid. Compiled into every flavor so the shipping
/// assembly can state that it never writes without a policy.
/// </summary>
public sealed class DenyAllWritePolicy : IRegistryWritePolicy
{
    internal const int ErrorAccessDenied = 5;

    public bool AllowsLinkValues => false;

    public bool RejectsSymbolicLinks => true;

    public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);

        throw new RegistryOperationException(
            operation.ToString(),
            path,
            ErrorAccessDenied,
            "The deny-all write policy refuses every registry write.");
    }
}

#if BWC_E2E
/// <summary>
/// Allows writes only to the configured sandbox root and its descendants. Compiled into E2E
/// builds only. Refuses symbolic links at any component, renaming the sandbox root, and copying
/// from outside the sandbox.
/// </summary>
/// <remarks>
/// A remote write is allowed only to a loopback computer (127.0.0.1 or ::1) and only to the same key under
/// HKEY_USERS\&lt;current SID&gt;. The remote path is refused when it has a '.' or '..' component.
/// </remarks>
public sealed class SandboxWritePolicy : IRegistryWritePolicy
{
    internal const int ErrorAccessDenied = 5;

    private static readonly RegistryPath SandboxBase =
        RegistryPath.Parse("HKCU\\Software\\BWC-E2E");

    private readonly RegistryPath _remoteSandboxRoot;

    public SandboxWritePolicy(RegistryPath sandboxRoot)
    {
        ArgumentNullException.ThrowIfNull(sandboxRoot);

        if (sandboxRoot.Root != RegistryRoot.CurrentUser
            || (!sandboxRoot.Equals(SandboxBase) && !sandboxRoot.IsDescendantOf(SandboxBase)))
        {
            throw new ArgumentException(
                "The sandbox root must be HKCU\\Software\\BWC-E2E or a descendant of it.",
                nameof(sandboxRoot));
        }

        SandboxRoot = sandboxRoot;
        _remoteSandboxRoot = RemoteEquivalentOf(sandboxRoot);
    }

    public RegistryPath SandboxRoot { get; }

    public bool AllowsLinkValues => false;

    public bool RejectsSymbolicLinks => true;

    public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (computerName is null)
        {
            EnsureLocalWriteAllowed(path, operation);
        }
        else
        {
            EnsureRemoteWriteAllowed(computerName, path, operation);
        }
    }

    private void EnsureLocalWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
    {
        if (path.Root != SandboxRoot.Root
            || !(path.Equals(SandboxRoot) || path.IsDescendantOf(SandboxRoot)))
        {
            throw new RegistryOperationException(
                operation.ToString(),
                path,
                ErrorAccessDenied,
                $"The sandbox write policy refuses '{path.ToAbbreviatedString()}'. Writes are limited to '{SandboxRoot.ToAbbreviatedString()}' and its descendants.");
        }

        if (operation == RegistryWriteOperation.RenameKey && path.Equals(SandboxRoot))
        {
            // Renaming the root moves every descendant out of the sandbox, even though the path
            // itself is inside it.
            throw new RegistryOperationException(
                operation.ToString(),
                path,
                ErrorAccessDenied,
                $"The sandbox write policy refuses to rename the sandbox root '{SandboxRoot.ToAbbreviatedString()}'.");
        }
    }

    private void EnsureRemoteWriteAllowed(string computerName, RegistryPath path, RegistryWriteOperation operation)
    {
        if (!IsLoopback(computerName))
        {
            throw RemoteRefusal(
                computerName,
                path,
                operation,
                "Remote writes are limited to the loopback computers 127.0.0.1 and ::1.");
        }

        if (path.Root != RegistryRoot.Users
            || !(path.Equals(_remoteSandboxRoot) || path.IsDescendantOf(_remoteSandboxRoot)))
        {
            throw RemoteRefusal(
                computerName,
                path,
                operation,
                $"Remote writes are limited to '{_remoteSandboxRoot.ToAbbreviatedString()}' and its descendants.");
        }

        foreach (string component in path.Components)
        {
            if (component is "." or "..")
            {
                throw RemoteRefusal(
                    computerName,
                    path,
                    operation,
                    "A remote write path cannot contain a '.' or '..' component.");
            }
        }

        if (operation == RegistryWriteOperation.RenameKey && path.Equals(_remoteSandboxRoot))
        {
            throw RemoteRefusal(
                computerName,
                path,
                operation,
                $"The sandbox root '{_remoteSandboxRoot.ToAbbreviatedString()}' cannot be renamed remotely.");
        }
    }

    private static RegistryOperationException RemoteRefusal(
        string computerName,
        RegistryPath path,
        RegistryWriteOperation operation,
        string reason) =>
        new(
            operation.ToString(),
            path,
            ErrorAccessDenied,
            $"The sandbox write policy refuses a write on computer '{computerName}'. {reason}");

    /// <summary>True only for the literal loopback addresses 127.0.0.1 and ::1. Names are never resolved.</summary>
    private static bool IsLoopback(string computerName)
    {
        string trimmed = computerName.Trim();
        return IPAddress.TryParse(trimmed, out IPAddress? address) &&
            (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
    }

    /// <summary>The same key as <paramref name="localRoot"/> under HKEY_USERS\&lt;current SID&gt;.</summary>
    private static RegistryPath RemoteEquivalentOf(RegistryPath localRoot)
    {
        RegistryPath result = RegistryPath.Parse($@"HKU\{CurrentUserSid()}");
        foreach (string component in localRoot.Components)
        {
            result = result.Append(component);
        }

        return result;
    }

    private static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }
}
#endif
