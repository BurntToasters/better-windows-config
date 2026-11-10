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
    Remote,
}

public interface IRegistryWritePolicy
{
    bool AllowsLinkValues { get; }

    void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation);
}

/// <summary>Allows every path. Used outside test runs.</summary>
public sealed class ShippingWritePolicy : IRegistryWritePolicy
{
    public bool AllowsLinkValues => true;

    public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);
    }
}

#if BWC_E2E
/// <summary>
/// Allows writes only to the configured sandbox root and its descendants. Compiled into E2E
/// builds only.
/// </summary>
public sealed class SandboxWritePolicy : IRegistryWritePolicy
{
    internal const int ErrorAccessDenied = 5;

    private static readonly RegistryPath SandboxBase =
        RegistryPath.Parse("HKCU\\Software\\BWC-E2E");

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
    }

    public RegistryPath SandboxRoot { get; }

    public bool AllowsLinkValues => false;

    public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Root == SandboxRoot.Root
            && (path.Equals(SandboxRoot) || path.IsDescendantOf(SandboxRoot)))
        {
            return;
        }

        throw new RegistryOperationException(operation.ToString(), path, ErrorAccessDenied);
    }
}
#endif
