using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// The only type that calls mutating registry APIs. Every mutation passes the write policy
/// first and every key is opened in the 64-bit view. A writer over a remote connection opens
/// keys under the connection's HKEY_LOCAL_MACHINE and HKEY_USERS roots (see <see cref="RegistryWriterRoots"/>).
/// </summary>
public sealed partial class RegistryWriter
{
    private const int MaxValueNameLength = 16383;
    private const uint DeleteStandardRight = 0x00010000;
    private const uint ReadControlStandardRight = 0x00020000;
    private const uint WriteDacStandardRight = 0x00040000;
    private const uint WriteOwnerStandardRight = 0x00080000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;

    private readonly IRegistryWritePolicy _policy;
    private readonly RegistryWriterRoots _roots;
    private readonly RegistryReader? _reader;

    public RegistryWriter(IRegistryWritePolicy policy)
        : this(policy, RegistryWriterRoots.Local, reader: null)
    {
    }

    /// <summary>
    /// The reader is used only for a remote copy, which reads the source tree before it writes. A remote writer
    /// without a reader cannot copy, so <see cref="CopyTree"/> then throws.
    /// </summary>
    internal RegistryWriter(IRegistryWritePolicy policy, RegistryWriterRoots roots, RegistryReader? reader)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(roots);
        _policy = policy;
        _roots = roots;
        _reader = reader;
    }

    public void CreateKey(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        RejectRoot(path, "A predefined registry root cannot be created.");

        const string Operation = nameof(RegistryWriteOperation.CreateKey);
        Authorize(path, RegistryWriteOperation.CreateKey);

        using SafeRegistryKeyHandle parent = OpenKey(
            path.Parent!,
            REG_SAM_FLAGS.KEY_WRITE,
            Operation,
            path);

        // Under a link-rejecting policy the leaf is opened as a link node, so an existing link is
        // seen and refused instead of followed. A missing leaf is still created as a normal key.
        bool guarded = _policy.RejectsSymbolicLinks;
        REG_OPEN_CREATE_OPTIONS options = guarded
            ? REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE | (REG_OPEN_CREATE_OPTIONS)RegOptionOpenLink
            : REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE;
        REG_SAM_FLAGS access = guarded
            ? REG_SAM_FLAGS.KEY_WRITE | REG_SAM_FLAGS.KEY_QUERY_VALUE | REG_SAM_FLAGS.KEY_WOW64_64KEY
            : REG_SAM_FLAGS.KEY_WRITE | REG_SAM_FLAGS.KEY_WOW64_64KEY;

        SafeRegistryHandle? created = null;
        try
        {
            WIN32_ERROR error = PInvoke.RegCreateKeyEx(
                parent,
                path.Components[^1],
                default,
                options,
                access,
                null,
                out SafeRegistryHandle createdHandle);

            created = createdHandle;
            ThrowIfFailed(error, Operation, path);

            if (guarded)
            {
                RefuseIfSymbolicLink(createdHandle, path.ToAbbreviatedString(), Operation, path);
            }
        }
        finally
        {
            created?.Dispose();
        }
    }

    public void SetValue(RegistryPath key, string valueName, uint type, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateValueName(valueName);

        const string Operation = nameof(RegistryWriteOperation.SetValue);
        Authorize(key, RegistryWriteOperation.SetValue);

        if (type == (uint)REG_VALUE_TYPE.REG_LINK && !_policy.AllowsLinkValues)
        {
            throw new RegistryOperationException(Operation, key, ErrorAccessDenied);
        }

        using SafeRegistryKeyHandle handle = OpenKey(
            key,
            REG_SAM_FLAGS.KEY_SET_VALUE,
            Operation,
            key);

        WIN32_ERROR error = SetValueData(handle, valueName, type, data);
        ThrowIfFailed(error, Operation, key);
    }

    /// <summary>
    /// Writes the data. An empty span would pass a null data pointer, which a remote key's RPC stub rejects with
    /// error 1780, so a zero-length value passes a pointer to a spare byte and a length of zero.
    /// </summary>
    private static unsafe WIN32_ERROR SetValueData(
        SafeRegistryKeyHandle handle,
        string valueName,
        uint type,
        ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            return PInvoke.RegSetValueEx(handle, valueName, (REG_VALUE_TYPE)type, data);
        }

        byte spare = 0;
        var key = new HKEY(handle.DangerousGetHandle());
        fixed (char* name = valueName)
        {
            return PInvoke.RegSetValueEx(key, name, 0, (REG_VALUE_TYPE)type, &spare, 0);
        }
    }

    public void DeleteValue(RegistryPath path, string valueName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateValueName(valueName);

        const string Operation = nameof(RegistryWriteOperation.DeleteValue);
        Authorize(path, RegistryWriteOperation.DeleteValue);

        using SafeRegistryKeyHandle handle = OpenKey(
            path,
            REG_SAM_FLAGS.KEY_SET_VALUE,
            Operation,
            path);

        WIN32_ERROR error = PInvoke.RegDeleteValue(handle, valueName);
        ThrowIfFailed(error, Operation, path);
    }

    public void DeleteKey(RegistryPath path, bool recursive)
    {
        ArgumentNullException.ThrowIfNull(path);
        RejectRoot(path, "A predefined registry root cannot be deleted.");

        const string Operation = nameof(RegistryWriteOperation.DeleteKey);
        Authorize(path, RegistryWriteOperation.DeleteKey);

        using SafeRegistryKeyHandle parent = OpenKey(
            path.Parent!,
            REG_SAM_FLAGS.KEY_WRITE | (REG_SAM_FLAGS)DeleteStandardRight,
            Operation,
            path);

        string leaf = path.Components[^1];
        if (_policy.RejectsSymbolicLinks)
        {
            EnsureChildIsNotLink(parent, path.Parent!.ToAbbreviatedString(), leaf, recursive, Operation, path);
        }

        WIN32_ERROR error = recursive
            ? PInvoke.RegDeleteTree(parent, leaf)
            : PInvoke.RegDeleteKeyEx(parent, leaf, (uint)REG_SAM_FLAGS.KEY_WOW64_64KEY);

        ThrowIfFailed(error, Operation, path);
    }

    public void RenameKey(RegistryPath path, string newName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateKeyComponent(newName);
        RejectRoot(path, "A predefined registry root cannot be renamed.");

        const string Operation = nameof(RegistryWriteOperation.RenameKey);
        Authorize(path, RegistryWriteOperation.RenameKey);

        WIN32_ERROR error;
        using (SafeRegistryKeyHandle parent = OpenKey(
            path.Parent!,
            REG_SAM_FLAGS.KEY_WRITE | (REG_SAM_FLAGS)DeleteStandardRight,
            Operation,
            path))
        {
            if (_policy.RejectsSymbolicLinks)
            {
                EnsureChildIsNotLink(parent, path.Parent!.ToAbbreviatedString(), path.Components[^1], recursive: false, Operation, path);
            }

            error = PInvoke.RegRenameKey(parent, path.Components[^1], newName);
        }

        if (error == WIN32_ERROR.ERROR_SUCCESS)
        {
            return;
        }

        // Measured on loopback: RegRenameKey on a remote handle fails with ERROR_INVALID_HANDLE (6). On a remote
        // root, the copy-then-delete fallback runs for that code and for the three "not supported" codes. Any
        // other failure is reported as is.
        if (!_roots.IsRemote || !IsRemoteRenameFallback(error))
        {
            throw new RegistryOperationException(Operation, path, (int)error);
        }

        RenameKeyByCopy(path, newName);
    }

    /// <summary>
    /// Renames a key by copying its tree to the new name and then deleting the original. This is the fallback
    /// for <see cref="RenameKey"/> on a remote root. The new key must not exist. It is created first, because
    /// <see cref="CopyTree"/> copies into an existing key. A failed copy removes the new key, which this call
    /// created. A failed delete of the original keeps the copy, because a recursive delete is not atomic.
    /// </summary>
    internal void RenameKeyByCopy(RegistryPath path, string newName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateKeyComponent(newName);
        RejectRoot(path, "A predefined registry root cannot be renamed.");

        const string Operation = nameof(RegistryWriteOperation.RenameKey);
        Authorize(path, RegistryWriteOperation.RenameKey);

        RegistryPath destination = path.Parent!.Append(newName);
        if (destination.Equals(path))
        {
            // The two names are the same key, so a copy cannot change its stored spelling.
            throw new RegistryOperationException(
                Operation,
                path,
                ErrorNotSupported,
                "A case-only rename is not supported by copy on a remote computer.");
        }

        if (KeyExists(destination, Operation))
        {
            throw new RegistryOperationException(
                Operation,
                path,
                ErrorFileExists,
                $"A key named '{newName}' already exists.");
        }

        CreateKey(destination);

        try
        {
            CopyTree(path, destination);
        }
        catch (RegistryOperationException copyError)
        {
            // The destination was created by this call, so everything under it is the partial copy. The source
            // was not changed by a failed copy.
            try
            {
                DeleteKey(destination, recursive: true);
            }
            catch (RegistryOperationException rollbackError)
            {
                throw new RegistryOperationException(
                    Operation,
                    path,
                    copyError.Win32Error,
                    $"The copy to '{destination.ToAbbreviatedString()}' failed and the partial copy could not be removed: {rollbackError.Message}");
            }

            throw;
        }

        try
        {
            DeleteKey(path, recursive: true);
        }
        catch (RegistryOperationException deleteError)
        {
            throw new RegistryOperationException(
                Operation,
                path,
                deleteError.Win32Error,
                $"Both keys now exist. The tree was copied to '{destination.ToAbbreviatedString()}', but the original '{path.ToAbbreviatedString()}' could not be removed. The copy holds the full tree; the original may be partly removed.");
        }
    }

    private static bool IsRemoteRenameFallback(WIN32_ERROR error) =>
        error is WIN32_ERROR.ERROR_INVALID_HANDLE
            or WIN32_ERROR.ERROR_NOT_SUPPORTED
            or WIN32_ERROR.ERROR_CALL_NOT_IMPLEMENTED
            or WIN32_ERROR.ERROR_INVALID_FUNCTION;

    public void CopyTree(RegistryPath source, RegistryPath destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        const string Operation = nameof(RegistryWriteOperation.CopyTree);
        Authorize(destination, RegistryWriteOperation.CopyTree);
        // The source is checked as well, so a policy can refuse to copy from outside its sandbox.
        Authorize(source, RegistryWriteOperation.CopyTree);

        // Applies to every policy. RegCopyTree would recurse into the tree it is writing.
        if (destination.Equals(source) || destination.IsDescendantOf(source))
        {
            throw new ArgumentException(
                "A key tree cannot be copied onto itself or into one of its own subkeys.",
                nameof(destination));
        }

        if (_roots.IsRemote)
        {
            // Measured on loopback: RegCopyTree fails with ERROR_INVALID_HANDLE (6) on a remote handle.
            CopyTreeByHand(source, destination, Operation);
            return;
        }

        using SafeRegistryKeyHandle sourceHandle = OpenKey(
            source,
            REG_SAM_FLAGS.KEY_READ,
            Operation,
            source);

        if (_policy.RejectsSymbolicLinks)
        {
            // RegCopyTree opens the children of the source with ordinary (following) opens, so a
            // link below the source would bring a link key, or a copy of a foreign target, into the
            // destination. Refuse the whole copy instead.
            EnsureSubtreeHasNoSymbolicLinks(sourceHandle, source.ToAbbreviatedString(), Operation, source, depth: 0);
        }

        // RegCopyTree copies the source security descriptor. The destination handle needs
        // WRITE_DAC in addition to read and write access, although that right is easy to miss
        // in the API documentation.
        using SafeRegistryKeyHandle destinationHandle = OpenKey(
            destination,
            REG_SAM_FLAGS.KEY_READ
                | REG_SAM_FLAGS.KEY_WRITE
                | (REG_SAM_FLAGS)WriteDacStandardRight,
            Operation,
            destination);

        if (_policy.RejectsSymbolicLinks)
        {
            // RegCopyTree follows an existing destination child that has the same name as a source child, so a
            // link already under the destination is an escape route even when the source is clean.
            EnsureSubtreeHasNoSymbolicLinks(destinationHandle, destination.ToAbbreviatedString(), Operation, destination, depth: 0);
        }

        // A null subkey copies the source key itself into the destination key.
        WIN32_ERROR error = PInvoke.RegCopyTree(sourceHandle, null!, destinationHandle);
        ThrowIfFailed(error, Operation, destination);
    }

    public void SetKeySecurity(
        RegistryPath path,
        RegistrySecurityInformation information,
        ReadOnlySpan<byte> securityDescriptor)
    {
        ArgumentNullException.ThrowIfNull(path);

        const string Operation = nameof(RegistryWriteOperation.SetKeySecurity);
        Authorize(path, RegistryWriteOperation.SetKeySecurity);

        ValidateSecurityArguments(information, securityDescriptor);

        uint access = WriteDacStandardRight | ReadControlStandardRight;
        if ((information & (RegistrySecurityInformation.Owner | RegistrySecurityInformation.Group)) != 0)
        {
            // Owner and primary-group changes need WRITE_OWNER. DACL-only changes must not
            // require it, or a non-owner who can still edit the DACL would be refused.
            access |= WriteOwnerStandardRight;
        }

        using SafeRegistryKeyHandle handle = OpenKey(
            path,
            (REG_SAM_FLAGS)access,
            Operation,
            path);

        var requested = (OBJECT_SECURITY_INFORMATION)(uint)information;
        WIN32_ERROR error;

        unsafe
        {
            fixed (byte* pointer = securityDescriptor)
            {
                error = PInvoke.RegSetKeySecurity(
                    handle,
                    requested,
                    new PSECURITY_DESCRIPTOR(pointer));
            }
        }

        ThrowIfFailed(error, Operation, path);
    }

    private SafeRegistryKeyHandle OpenKey(
        RegistryPath path,
        REG_SAM_FLAGS access,
        string operation,
        RegistryPath errorPath)
    {
        if (_policy.RejectsSymbolicLinks)
        {
            return OpenGuardedKey(path, access, operation, errorPath);
        }

        WIN32_ERROR error = TryOpenKey(path, access, out SafeRegistryKeyHandle? handle);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle?.Dispose();
            throw new RegistryOperationException(operation, errorPath, (int)error);
        }

        return handle!;
    }

    /// <summary>
    /// Opens <paramref name="path"/> under the writer's root. A remote root is pinned for this one open, so a
    /// Dispose of the connection cannot close it while the open runs. A root the remote connection does not serve
    /// throws before any native call.
    /// </summary>
    private WIN32_ERROR TryOpenKey(RegistryPath path, REG_SAM_FLAGS access, out SafeRegistryKeyHandle? handle)
    {
        using SafeRegistryKeyHandle? pin = _roots.Pin(path.Root);
        return NativeRegistry.TryOpenKey(path, access, pin, out handle);
    }

    /// <summary>Asks the write policy, passing the computer name (null for the local registry).</summary>
    private void Authorize(RegistryPath path, RegistryWriteOperation operation) =>
        _policy.EnsureWriteAllowed(_roots.ComputerName, path, operation);

    /// <summary>
    /// Refuses an operation that has no remote form. It runs after the policy check and before any native call.
    /// </summary>
    private void RejectRemote(RegistryPath path, string operation)
    {
        if (_roots.IsRemote)
        {
            throw new RegistryOperationException(
                operation,
                path,
                ErrorNotSupported,
                "This operation is not supported on a remote computer.");
        }
    }

    /// <summary>True when the key exists. Any open failure other than not-found is thrown.</summary>
    private bool KeyExists(RegistryPath path, string operation)
    {
        WIN32_ERROR error = TryOpenKey(path, REG_SAM_FLAGS.KEY_READ, out SafeRegistryKeyHandle? handle);
        handle?.Dispose();

        if (error == WIN32_ERROR.ERROR_SUCCESS)
        {
            return true;
        }

        if (error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND)
        {
            return false;
        }

        throw new RegistryOperationException(operation, path, (int)error);
    }

    private static void ThrowIfFailed(WIN32_ERROR error, string operation, RegistryPath path)
    {
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new RegistryOperationException(operation, path, (int)error);
        }
    }

    private static void ValidateValueName(string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);

        if (valueName.Length > MaxValueNameLength)
        {
            throw new ArgumentException(
                $"A registry value name cannot exceed {MaxValueNameLength} characters.",
                nameof(valueName));
        }

        if (valueName.Contains('\0'))
        {
            throw new ArgumentException("A registry value name cannot contain NUL.", nameof(valueName));
        }
    }

    private static void ValidateKeyComponent(string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);

        if (!RegistryPath.IsValidComponent(newName))
        {
            throw new ArgumentException(
                "A new key name must be one component: non-empty, at most 255 characters, and without separators, NUL, '.' or '..'.",
                nameof(newName));
        }
    }

    private static void RejectRoot(RegistryPath path, string message)
    {
        if (path.IsRoot)
        {
            throw new ArgumentException(message, nameof(path));
        }
    }
}
