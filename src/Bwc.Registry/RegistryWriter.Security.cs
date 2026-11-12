using System.Security.AccessControl;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Progress for a security change. <see cref="Path"/> is the key that was just changed, and
/// <see cref="KeysChanged"/> is the running count for the whole call.
/// </summary>
public sealed record KeySecurityProgress(RegistryPath Path, int KeysChanged);

/// <summary>
/// Permission propagation for the Permissions dialog. The key receives the descriptor it was given. Each
/// key below it receives only the entries it inherits from its parent. See
/// docs/failure-modes/permissions.md sections 15 to 22 and docs/failure-modes/registry-writer.md section I.
/// </summary>
public sealed partial class RegistryWriter
{
    private const int ErrorInvalidSecurityDescriptor = 1338;
    private const uint OwnerAndGroupAndDacl = (uint)(RegistrySecurityInformation.Owner
        | RegistrySecurityInformation.Group
        | RegistrySecurityInformation.Dacl);

    /// <summary>
    /// Applies <paramref name="securityDescriptor"/> to <paramref name="path"/>. When
    /// <paramref name="propagateToSubkeys"/> is true, every key below it is updated the way classic
    /// "Replace all child object permission entries" works: each child's DACL, owner and group are
    /// computed from its parent, so the child holds only the entries it inherits. Each key goes through
    /// the write policy. The sandbox refuses the whole call, before any key changes, when the subtree
    /// contains a symbolic link. The walk checks <paramref name="cancellationToken"/> before each key and
    /// reports <paramref name="progress"/> after each one. A failure on one key stops the walk. Keys
    /// already changed keep their new descriptor, and no rollback is attempted.
    /// </summary>
    /// <remarks>
    /// Propagation refuses SACL information with <see cref="ArgumentException"/> before any change. Owner and
    /// group are copied to the children only when they are part of <paramref name="information"/>.
    /// </remarks>
    public void SetKeySecurity(
        RegistryPath path,
        ReadOnlySpan<byte> securityDescriptor,
        RegistrySecurityInformation information,
        bool propagateToSubkeys,
        IProgress<KeySecurityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        const string Operation = nameof(RegistryWriteOperation.SetKeySecurity);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.SetKeySecurity);
        ValidateSecurityArguments(information, securityDescriptor);

        if (!propagateToSubkeys)
        {
            SetKeySecurity(path, information, securityDescriptor);
            progress?.Report(new KeySecurityProgress(path, 1));
            return;
        }

        if ((information & RegistrySecurityInformation.Sacl) != 0)
        {
            throw new ArgumentException(
                "Permission propagation does not carry audit entries. Change the audit entries with a separate, non-propagating call.",
                nameof(information));
        }

        byte[] descriptor = securityDescriptor.ToArray();
        RegistrySecurityInformation childInformation = information
            & (RegistrySecurityInformation.Dacl | RegistrySecurityInformation.Owner | RegistrySecurityInformation.Group);

        if (_policy.RejectsSymbolicLinks)
        {
            // Refuse before the first key changes, so a refused propagation leaves every key as it was.
            using SafeRegistryKeyHandle root = OpenKey(path, REG_SAM_FLAGS.KEY_READ, Operation, path);
            EnsureSubtreeHasNoSymbolicLinks(root, path.ToAbbreviatedString(), Operation, path, depth: 0);
        }

        // The subkey names are listed before the root changes. The new DACL may deny the current user
        // enumeration, and the walk must still reach every key. A key that cannot be listed yet is listed
        // after its change instead.
        IReadOnlyList<string>? rootNames = childInformation == RegistrySecurityInformation.None
            ? null
            : TryListSubkeyNames(path, Operation);

        SetKeySecurity(path, information, descriptor);
        int changed = 1;
        progress?.Report(new KeySecurityProgress(path, changed));

        if (childInformation == RegistrySecurityInformation.None)
        {
            return;
        }

        // The children inherit from the key as it now stands, so the parent is read back after the write.
        byte[] parentEffective;
        using (SafeRegistryKeyHandle handle = OpenKey(path, (REG_SAM_FLAGS)ReadControlStandardRight, Operation, path))
        {
            parentEffective = ReadSecurityDescriptor(handle, (RegistrySecurityInformation)OwnerAndGroupAndDacl, Operation, path);
        }

        rootNames ??= ListSubkeyNames(path, Operation);
        PropagateToChildren(path, parentEffective, rootNames, childInformation, progress, cancellationToken, ref changed, depth: 0);
    }

    private void PropagateToChildren(
        RegistryPath path,
        byte[] parentEffective,
        IReadOnlyList<string> names,
        RegistrySecurityInformation childInformation,
        IProgress<KeySecurityProgress>? progress,
        CancellationToken cancellationToken,
        ref int changed,
        int depth)
    {
        const string Operation = nameof(RegistryWriteOperation.SetKeySecurity);

        cancellationToken.ThrowIfCancellationRequested();

        if (depth >= MaxSubtreeDepth)
        {
            throw new RegistryOperationException(
                Operation,
                path,
                ErrorAccessDenied,
                $"The permission propagation stops at '{path.ToAbbreviatedString()}' because it is nested {MaxSubtreeDepth} levels deep.");
        }

        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegistryPath child = path.Append(name);
            _policy.EnsureWriteAllowed(child, RegistryWriteOperation.SetKeySecurity);

            // The child's own subkeys are listed before the child's DACL changes. The new DACL may deny the
            // owner KEY_ENUMERATE_SUB_KEYS, which would stop the walk below the child.
            IReadOnlyList<string>? childNames = TryListSubkeyNames(child, Operation);

            byte[] childEffective = ReplaceWithInheritedEntries(child, parentEffective, childInformation);

            changed += 1;
            progress?.Report(new KeySecurityProgress(child, changed));

            childNames ??= ListSubkeyNames(child, Operation);
            PropagateToChildren(child, childEffective, childNames, childInformation, progress, cancellationToken, ref changed, depth + 1);
        }
    }

    /// <summary>
    /// Lists the names of the subkeys of <paramref name="path"/>. Returns null when the current access does
    /// not allow the listing, so the caller can list the key after its DACL changes.
    /// </summary>
    private IReadOnlyList<string>? TryListSubkeyNames(RegistryPath path, string operation)
    {
        try
        {
            return ListSubkeyNames(path, operation);
        }
        catch (RegistryOperationException error) when (error.Win32Error == ErrorAccessDenied)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists the names of the subkeys of <paramref name="path"/>. The names are listed before the walk
    /// descends, so a child that is changed does not alter the list.
    /// </summary>
    private IReadOnlyList<string> ListSubkeyNames(RegistryPath path, string operation)
    {
        var names = new List<string>();
        using SafeRegistryKeyHandle handle = OpenKey(path, REG_SAM_FLAGS.KEY_READ, operation, path);
        for (uint index = 0; ; index += 1)
        {
            WIN32_ERROR error = NativeLinkGuard.TryEnumerateSubKey(handle, index, out string? name);
            if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                break;
            }

            ThrowIfFailed(error, operation, path);
            names.Add(name!);
        }

        return names;
    }

    /// <summary>
    /// Replaces the requested parts of one child's descriptor with the system-computed inherited version,
    /// and returns the child's descriptor as it reads back afterwards.
    /// </summary>
    private byte[] ReplaceWithInheritedEntries(
        RegistryPath child,
        byte[] parentEffective,
        RegistrySecurityInformation childInformation)
    {
        const string Operation = nameof(RegistryWriteOperation.SetKeySecurity);

        bool replaceDacl = (childInformation & RegistrySecurityInformation.Dacl) != 0;
        bool copyOwner = (childInformation & RegistrySecurityInformation.Owner) != 0;
        bool copyGroup = (childInformation & RegistrySecurityInformation.Group) != 0;

        REG_SAM_FLAGS access = (REG_SAM_FLAGS)ReadControlStandardRight;
        if (replaceDacl)
        {
            access |= (REG_SAM_FLAGS)WriteDacStandardRight;
        }

        if (copyOwner || copyGroup)
        {
            access |= (REG_SAM_FLAGS)WriteOwnerStandardRight;
        }

        // A child inherits only the requested parts, and the DACL is always unprotected so it can inherit.
        RegistrySecurityInformation setInformation = childInformation & (RegistrySecurityInformation.Dacl
            | RegistrySecurityInformation.Owner
            | RegistrySecurityInformation.Group);
        if (replaceDacl)
        {
            setInformation |= RegistrySecurityInformation.UnprotectedDacl;
        }

        using SafeRegistryKeyHandle handle = OpenKey(child, access, Operation, child);

        byte[] current = ReadSecurityDescriptor(
            handle,
            (RegistrySecurityInformation)OwnerAndGroupAndDacl,
            Operation,
            child);

        byte[] computed;
        try
        {
            computed = RegistrySecurityInheritance.ComputeChildDescriptor(
                parentEffective,
                current,
                replaceDacl,
                copyOwner,
                copyGroup);
        }
        catch (ArgumentException ex)
        {
            throw new RegistryOperationException(Operation, child, ErrorInvalidSecurityDescriptor, ex.Message);
        }

        WriteSecurityDescriptor(handle, setInformation, computed, Operation, child);

        return ReadSecurityDescriptor(
            handle,
            (RegistrySecurityInformation)OwnerAndGroupAndDacl,
            Operation,
            child);
    }

    /// <summary>
    /// Reads the requested parts of a key's descriptor. The result is self-relative and validated, so a
    /// descriptor that does not parse is reported as an error and never passed on.
    /// </summary>
    private static unsafe byte[] ReadSecurityDescriptor(
        SafeRegistryKeyHandle handle,
        RegistrySecurityInformation information,
        string operation,
        RegistryPath path)
    {
        var requested = (OBJECT_SECURITY_INFORMATION)(uint)information;
        uint size = 0;
        WIN32_ERROR error = PInvoke.RegGetKeySecurity(handle, requested, default, ref size);
        if (error != WIN32_ERROR.ERROR_SUCCESS && error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            throw new RegistryOperationException(operation, path, (int)error);
        }

        for (int attempt = 0; attempt < 8; attempt += 1)
        {
            if (size == 0)
            {
                break;
            }

            byte[] buffer = new byte[size];
            fixed (byte* pointer = buffer)
            {
                error = PInvoke.RegGetKeySecurity(handle, requested, new PSECURITY_DESCRIPTOR(pointer), ref size);
            }

            if (error == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw new RegistryOperationException(operation, path, (int)error);
            }

            if (!RegistrySecurityDescriptor.TryGetLength(buffer, out int declaredLength) ||
                declaredLength != buffer.Length)
            {
                throw new RegistryOperationException(operation, path, ErrorInvalidSecurityDescriptor);
            }

            return buffer;
        }

        throw new RegistryOperationException(operation, path, ErrorInvalidSecurityDescriptor);
    }

    private static unsafe void WriteSecurityDescriptor(
        SafeRegistryKeyHandle handle,
        RegistrySecurityInformation information,
        byte[] descriptor,
        string operation,
        RegistryPath path)
    {
        var requested = (OBJECT_SECURITY_INFORMATION)(uint)information;
        WIN32_ERROR error;
        fixed (byte* pointer = descriptor)
        {
            error = PInvoke.RegSetKeySecurity(handle, requested, new PSECURITY_DESCRIPTOR(pointer));
        }

        ThrowIfFailed(error, operation, path);
    }

    private static void ValidateSecurityArguments(
        RegistrySecurityInformation information,
        ReadOnlySpan<byte> securityDescriptor)
    {
        if (information == RegistrySecurityInformation.None)
        {
            throw new ArgumentException(
                "At least one security information flag is required.",
                nameof(information));
        }

        if (!RegistrySecurityDescriptor.TryGetLength(securityDescriptor, out int declaredLength) ||
            declaredLength != securityDescriptor.Length)
        {
            throw new ArgumentException(
                "The buffer is not a valid self-relative security descriptor with an exact declared length.",
                nameof(securityDescriptor));
        }
    }
}
