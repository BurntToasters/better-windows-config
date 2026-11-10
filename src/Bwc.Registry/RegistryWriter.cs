using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// The only type that calls mutating registry APIs. Every mutation passes the write policy
/// first and every key is opened in the 64-bit view.
/// </summary>
public sealed class RegistryWriter
{
    private const int MaxValueNameLength = 16383;
    private const uint DeleteStandardRight = 0x00010000;
    private const uint WriteDacStandardRight = 0x00040000;
    private const int ErrorAccessDenied = 5;

    private readonly IRegistryWritePolicy _policy;

    public RegistryWriter(IRegistryWritePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public void CreateKey(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        RejectRoot(path, "A predefined registry root cannot be created.");

        const string Operation = nameof(RegistryWriteOperation.CreateKey);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.CreateKey);

        using SafeRegistryKeyHandle parent = OpenKey(
            path.Parent!,
            REG_SAM_FLAGS.KEY_WRITE,
            Operation,
            path);

        SafeRegistryHandle? created = null;
        try
        {
            WIN32_ERROR error = PInvoke.RegCreateKeyEx(
                parent,
                path.Components[^1],
                default,
                REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE,
                REG_SAM_FLAGS.KEY_WRITE | REG_SAM_FLAGS.KEY_WOW64_64KEY,
                null,
                out SafeRegistryHandle createdHandle);

            created = createdHandle;
            ThrowIfFailed(error, Operation, path);
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
        _policy.EnsureWriteAllowed(key, RegistryWriteOperation.SetValue);

        if (type == (uint)REG_VALUE_TYPE.REG_LINK && !_policy.AllowsLinkValues)
        {
            throw new RegistryOperationException(Operation, key, ErrorAccessDenied);
        }

        using SafeRegistryKeyHandle handle = OpenKey(
            key,
            REG_SAM_FLAGS.KEY_SET_VALUE,
            Operation,
            key);

        WIN32_ERROR error = PInvoke.RegSetValueEx(handle, valueName, (REG_VALUE_TYPE)type, data);
        ThrowIfFailed(error, Operation, key);
    }

    public void DeleteValue(RegistryPath path, string valueName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateValueName(valueName);

        const string Operation = nameof(RegistryWriteOperation.DeleteValue);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.DeleteValue);

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
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.DeleteKey);

        using SafeRegistryKeyHandle parent = OpenKey(
            path.Parent!,
            REG_SAM_FLAGS.KEY_WRITE | (REG_SAM_FLAGS)DeleteStandardRight,
            Operation,
            path);

        string leaf = path.Components[^1];
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
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.RenameKey);

        using SafeRegistryKeyHandle parent = OpenKey(
            path.Parent!,
            REG_SAM_FLAGS.KEY_WRITE | (REG_SAM_FLAGS)DeleteStandardRight,
            Operation,
            path);

        WIN32_ERROR error = PInvoke.RegRenameKey(parent, path.Components[^1], newName);
        ThrowIfFailed(error, Operation, path);
    }

    public void CopyTree(RegistryPath source, RegistryPath destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        const string Operation = nameof(RegistryWriteOperation.CopyTree);
        _policy.EnsureWriteAllowed(destination, RegistryWriteOperation.CopyTree);

        using SafeRegistryKeyHandle sourceHandle = OpenKey(
            source,
            REG_SAM_FLAGS.KEY_READ,
            Operation,
            source);

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

        // A null subkey copies the source key itself into the destination key.
        WIN32_ERROR error = PInvoke.RegCopyTree(sourceHandle, null!, destinationHandle);
        ThrowIfFailed(error, Operation, destination);
    }

    public void LoadHive(RegistryPath path, string filePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.LoadHive);
        ArgumentNullException.ThrowIfNull(filePath);
        throw new NotImplementedException("LoadHive is reserved for B8 and is not implemented yet.");
    }

    public void UnloadHive(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.UnloadHive);
        throw new NotImplementedException("UnloadHive is reserved for B8 and is not implemented yet.");
    }

    public void SaveKey(RegistryPath path, string filePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.SaveKey);
        ArgumentNullException.ThrowIfNull(filePath);
        throw new NotImplementedException("SaveKey is reserved for B8 and is not implemented yet.");
    }

    public void ReplaceKey(RegistryPath path, string newFileName, string backupFileName)
    {
        ArgumentNullException.ThrowIfNull(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.ReplaceKey);
        ArgumentNullException.ThrowIfNull(newFileName);
        ArgumentNullException.ThrowIfNull(backupFileName);
        throw new NotImplementedException("ReplaceKey is reserved for B8 and is not implemented yet.");
    }

    public void RestoreKey(RegistryPath path, string filePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.RestoreKey);
        ArgumentNullException.ThrowIfNull(filePath);
        throw new NotImplementedException("RestoreKey is reserved for B8 and is not implemented yet.");
    }

    public void SetKeySecurity(RegistryPath path, ReadOnlySpan<byte> securityDescriptor)
    {
        ArgumentNullException.ThrowIfNull(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.SetKeySecurity);
        _ = securityDescriptor;
        throw new NotImplementedException("SetKeySecurity is reserved for B8 and is not implemented yet.");
    }

    private SafeRegistryKeyHandle OpenKey(
        RegistryPath path,
        REG_SAM_FLAGS access,
        string operation,
        RegistryPath errorPath)
    {
        WIN32_ERROR error = NativeRegistry.TryOpenKey(path, access, out SafeRegistryKeyHandle? handle);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle?.Dispose();
            throw new RegistryOperationException(operation, errorPath, (int)error);
        }

        return handle!;
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
