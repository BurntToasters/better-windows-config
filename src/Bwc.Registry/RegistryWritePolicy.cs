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

    void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation);
}

/// <summary>Allows every path. Used outside test runs.</summary>
public sealed class ShippingWritePolicy : IRegistryWritePolicy
{
    public bool AllowsLinkValues => true;

    public bool RejectsSymbolicLinks => false;

    public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);
    }
}

/// <summary>
/// Refuses every mutation before any native code runs. The Registry Editor uses it in E2E builds
/// when the sandbox root is missing or invalid. Compiled into every flavor so the shipping
/// assembly can state that it never writes without a policy.
/// </summary>
public sealed class DenyAllWritePolicy : IRegistryWritePolicy
{
    internal const int ErrorAccessDenied = 5;

    public bool AllowsLinkValues => false;

    public bool RejectsSymbolicLinks => true;

    public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
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

    public bool RejectsSymbolicLinks => true;

    public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);

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
}
#endif
