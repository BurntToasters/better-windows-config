using Bwc.Registry.Privileges;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

public sealed partial class RegistryWriter
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileExists = 183;
    private const int ErrorPrivilegeNotHeld = 1314;

    public void LoadHive(RegistryPath path, string filePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filePath);

        const string Operation = nameof(RegistryWriteOperation.LoadHive);
        ValidateHiveMountTarget(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.LoadHive);

        string fullPath = ResolveExistingHiveFile(filePath, Operation, path, nameof(filePath));
        using PrivilegeScope privilegeScope = EnableHivePrivileges(Operation, path);

        using SafeRegistryKeyHandle root = SafeRegistryKeyHandle.FromPredefined(path.Root);
        WIN32_ERROR error = PInvoke.RegLoadKey(root, path.Components[0], fullPath);
        ThrowIfFailed(error, Operation, path);
    }

    public void UnloadHive(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        const string Operation = nameof(RegistryWriteOperation.UnloadHive);
        ValidateHiveMountTarget(path);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.UnloadHive);

        using PrivilegeScope privilegeScope = EnableHivePrivileges(Operation, path);

        using SafeRegistryKeyHandle root = SafeRegistryKeyHandle.FromPredefined(path.Root);
        WIN32_ERROR error = PInvoke.RegUnLoadKey(root, path.Components[0]);
        ThrowIfFailed(error, Operation, path);
    }

    public void SaveKey(RegistryPath path, string filePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filePath);

        const string Operation = nameof(RegistryWriteOperation.SaveKey);
        RejectRoot(path, "A predefined registry root cannot be saved to a hive file.");
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.SaveKey);

        if (_policy.RejectsSymbolicLinks)
        {
            // Check the key before the file is created or any privilege is taken.
            EnsureKeyIsReachable(path, Operation);
        }

        string fullPath = ResolveSaveDestination(filePath, Operation, path, nameof(filePath));
        using PrivilegeScope privilegeScope = EnableHivePrivileges(Operation, path);

        using SafeRegistryKeyHandle handle = OpenKey(
            path,
            REG_SAM_FLAGS.KEY_READ,
            Operation,
            path);

        WIN32_ERROR error = PInvoke.RegSaveKey(handle, fullPath, null);
        ThrowIfFailed(error, Operation, path);
    }

    public void ReplaceKey(RegistryPath path, string newFileName, string backupFileName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newFileName);
        ArgumentNullException.ThrowIfNull(backupFileName);

        const string Operation = nameof(RegistryWriteOperation.ReplaceKey);
        RejectRoot(path, "A predefined registry root cannot be replaced from a hive file.");
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.ReplaceKey);

        if (_policy.RejectsSymbolicLinks)
        {
            EnsureKeyIsReachable(path, Operation);
        }

        string newFullPath = ResolveExistingHiveFile(
            newFileName,
            Operation,
            path,
            nameof(newFileName));

        if (_policy.RejectsSymbolicLinks)
        {
            // Refuse before the backup file is created, so a refused replace leaves no files behind.
            EnsureHiveFileHasNoLinks(newFullPath, Operation, path);
        }

        string backupFullPath = ResolveBackupDestination(
            backupFileName,
            newFullPath,
            Operation,
            path,
            nameof(backupFileName));

        using PrivilegeScope privilegeScope = EnableHivePrivileges(Operation, path);

        using SafeRegistryKeyHandle handle = OpenKey(
            path,
            REG_SAM_FLAGS.KEY_READ,
            Operation,
            path);

        WIN32_ERROR error = PInvoke.RegReplaceKey(handle, null, newFullPath, backupFullPath);
        ThrowIfFailed(error, Operation, path);
    }

    public void RestoreKey(RegistryPath path, string filePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filePath);

        const string Operation = nameof(RegistryWriteOperation.RestoreKey);
        RejectRoot(path, "A predefined registry root cannot be restored from a hive file.");
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.RestoreKey);

        if (_policy.RejectsSymbolicLinks)
        {
            EnsureKeyIsReachable(path, Operation);
        }

        string fullPath = ResolveExistingHiveFile(filePath, Operation, path, nameof(filePath));

        if (_policy.RejectsSymbolicLinks)
        {
            // Refuse before the native restore, so a refused restore leaves the target untouched.
            EnsureHiveFileHasNoLinks(fullPath, Operation, path);
        }

        using PrivilegeScope privilegeScope = EnableHivePrivileges(Operation, path);

        using SafeRegistryKeyHandle handle = OpenKey(
            path,
            REG_SAM_FLAGS.KEY_READ,
            Operation,
            path);

        WIN32_ERROR error = PInvoke.RegRestoreKey(handle, fullPath, default);
        ThrowIfFailed(error, Operation, path);
    }

    private static void ValidateHiveMountTarget(RegistryPath path)
    {
        bool validRoot = path.Root is RegistryRoot.LocalMachine or RegistryRoot.Users;
        if (!validRoot || path.Components.Count != 1)
        {
            throw new ArgumentException(
                "A hive can be loaded or unloaded only at a direct child of HKEY_LOCAL_MACHINE or HKEY_USERS.",
                nameof(path));
        }
    }

    private static PrivilegeScope EnableHivePrivileges(string operation, RegistryPath path)
    {
        if (!PrivilegeScope.TryEnable(out PrivilegeScope? scope, out string? error))
        {
            throw new RegistryOperationException(operation, path, ErrorPrivilegeNotHeld, error);
        }

        return scope!;
    }

    private static string ResolveExistingHiveFile(
        string filePath,
        string operation,
        RegistryPath path,
        string parameterName)
    {
        string fullPath = ResolveHiveFilePath(filePath, parameterName);

        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException(
                "A hive file path must name a file, not a directory.",
                parameterName);
        }

        if (!File.Exists(fullPath))
        {
            throw new RegistryOperationException(operation, path, ErrorFileNotFound);
        }

        return fullPath;
    }

    private static string ResolveSaveDestination(
        string filePath,
        string operation,
        RegistryPath path,
        string parameterName)
    {
        string fullPath = ResolveHiveFilePath(filePath, parameterName);

        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException(
                "A hive file path must name a file, not a directory.",
                parameterName);
        }

        if (File.Exists(fullPath))
        {
            throw new RegistryOperationException(operation, path, ErrorFileExists);
        }

        EnsureParentDirectoryExists(fullPath, operation, path, parameterName);
        return fullPath;
    }

    private static string ResolveBackupDestination(
        string backupFileName,
        string newFullPath,
        string operation,
        RegistryPath path,
        string parameterName)
    {
        string fullPath = ResolveHiveFilePath(backupFileName, parameterName);

        if (string.Equals(fullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The backup hive file must be a different file from the new hive file.",
                parameterName);
        }

        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException(
                "A hive file path must name a file, not a directory.",
                parameterName);
        }

        if (File.Exists(fullPath))
        {
            throw new RegistryOperationException(operation, path, ErrorFileExists);
        }

        EnsureParentDirectoryExists(fullPath, operation, path, parameterName);
        return fullPath;
    }

    private static string ResolveHiveFilePath(string filePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A hive file path is required.", parameterName);
        }

        if (filePath.Contains('\0'))
        {
            throw new ArgumentException("A hive file path cannot contain NUL.", parameterName);
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The hive file path is not valid.", parameterName, ex);
        }
    }

    private static void EnsureParentDirectoryExists(
        string fullPath,
        string operation,
        RegistryPath path,
        string parameterName)
    {
        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException(
                "A hive file path must name a file in a directory.",
                parameterName);
        }

        if (!Directory.Exists(parent))
        {
            throw new RegistryOperationException(operation, path, ErrorPathNotFound);
        }
    }
}
