namespace Bwc.Registry;

/// <summary>
/// Typed convenience layer over <see cref="RegistryWriter"/> and <see cref="RegistryReader"/>.
/// Every mutation goes through the single writer instance and its write policy. Calls are not
/// transactional across methods; see docs/failure-modes/registry-store.md.
/// </summary>
public sealed class RegistryStore
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAlreadyExists = 183;

    private readonly RegistryWriter _writer;
    private readonly RegistryReader _reader;

    public RegistryStore(RegistryWriter writer, RegistryReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _writer = writer;
        _reader = reader ?? new RegistryReader();
    }

    public RegistryStore(IRegistryWritePolicy policy)
        : this(new RegistryWriter(policy))
    {
    }

    public RegistryWriter Writer => _writer;

    public RegistryReader Reader => _reader;

    public void CreateKey(RegistryPath path) => _writer.CreateKey(path);

    public void DeleteKey(RegistryPath path, bool recursive) => _writer.DeleteKey(path, recursive);

    public void RenameKey(RegistryPath path, string newName) => _writer.RenameKey(path, newName);

    public void CopyTree(RegistryPath source, RegistryPath destination) =>
        _writer.CopyTree(source, destination);

    /// <summary>Loads a hive file at a direct child of HKLM or HKU.</summary>
    public void LoadHive(RegistryPath path, string filePath) => _writer.LoadHive(path, filePath);

    /// <summary>Unloads a hive at a direct child of HKLM or HKU.</summary>
    public void UnloadHive(RegistryPath path) => _writer.UnloadHive(path);

    /// <summary>Saves a key tree to a new hive file.</summary>
    public void SaveKey(RegistryPath path, string filePath) => _writer.SaveKey(path, filePath);

    /// <summary>Replaces a key tree from a hive file and writes the previous contents to a backup file.</summary>
    public void ReplaceKey(RegistryPath path, string newFileName, string backupFileName) =>
        _writer.ReplaceKey(path, newFileName, backupFileName);

    /// <summary>Restores a key tree from a hive file without creating a backup.</summary>
    public void RestoreKey(RegistryPath path, string filePath) => _writer.RestoreKey(path, filePath);

    public void DeleteValue(RegistryPath key, string valueName) => _writer.DeleteValue(key, valueName);

    public byte[] GetKeySecurity(
        RegistryPath key,
        RegistrySecurityInformation information,
        CancellationToken cancellationToken = default) =>
        _reader.GetKeySecurity(key, information, cancellationToken);

    public void SetKeySecurity(
        RegistryPath key,
        RegistrySecurityInformation information,
        ReadOnlySpan<byte> securityDescriptor) =>
        _writer.SetKeySecurity(key, information, securityDescriptor);

    public RegistryValueInfo? GetValue(
        RegistryPath key,
        string valueName,
        CancellationToken cancellationToken = default) =>
        _reader.ReadValue(key, valueName, cancellationToken);

    public IReadOnlyList<RegistryValueInfo> GetValues(
        RegistryPath key,
        CancellationToken cancellationToken = default) =>
        _reader.EnumerateValues(key, cancellationToken);

    public void SetString(RegistryPath key, string valueName, string value) =>
        SetValue(key, valueName, RegistryValueType.String, RegistryValueCodec.EncodeString(value));

    public void SetExpandString(RegistryPath key, string valueName, string value) =>
        SetValue(key, valueName, RegistryValueType.ExpandString, RegistryValueCodec.EncodeString(value));

    public void SetDword(RegistryPath key, string valueName, uint value) =>
        SetValue(key, valueName, RegistryValueType.Dword, RegistryValueCodec.EncodeDword(value));

    public void SetDwordBigEndian(RegistryPath key, string valueName, uint value) =>
        SetValue(key, valueName, RegistryValueType.DwordBigEndian, RegistryValueCodec.EncodeDwordBigEndian(value));

    public void SetQword(RegistryPath key, string valueName, ulong value) =>
        SetValue(key, valueName, RegistryValueType.Qword, RegistryValueCodec.EncodeQword(value));

    public void SetBinary(RegistryPath key, string valueName, ReadOnlySpan<byte> data) =>
        SetValue(key, valueName, RegistryValueType.Binary, data);

    public void SetMultiString(RegistryPath key, string valueName, IReadOnlyList<string> values) =>
        SetValue(key, valueName, RegistryValueType.MultiString, RegistryValueCodec.EncodeMultiString(values));

    public void SetValue(
        RegistryPath key,
        string valueName,
        RegistryValueType type,
        ReadOnlySpan<byte> data) =>
        _writer.SetValue(key, valueName, (uint)type, data);

    /// <summary>
    /// Copies the raw value bytes to <paramref name="newName"/> and then deletes
    /// <paramref name="oldName"/>. A <paramref name="newName"/> that already exists is refused with
    /// ERROR_ALREADY_EXISTS before any write. A case-only change is delete then recreate. If the delete
    /// fails the new value is removed and the original error is rethrown. If the rollback also fails, the
    /// returned error names both failures.
    /// </summary>
    public void RenameValue(RegistryPath key, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);

        RegistryValueInfo? existing = _reader.ReadValue(key, oldName);

        if (existing is null)
        {
            throw new RegistryOperationException("RenameValue", key, ErrorFileNotFound);
        }

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            // Both names denote the same registry value, so the target is the source and is not
            // "already exists". Writing first would keep the stored spelling. The value is deleted
            // and recreated under the new spelling instead.
            RenameCaseOnly(key, oldName, newName, existing);
            return;
        }

        if (_reader.ValueExists(key, newName))
        {
            // Refused before any write, so the existing target keeps its data. The UI maps 183 to
            // the classic "value name already exists" message.
            throw new RegistryOperationException(
                "RenameValue",
                key,
                ErrorAlreadyExists,
                $"A value named '{newName}' already exists.");
        }

        _writer.SetValue(key, newName, existing.Type, existing.Data);

        try
        {
            _writer.DeleteValue(key, oldName);
        }
        catch (Exception deleteError)
        {
            try
            {
                _writer.DeleteValue(key, newName);
            }
            catch (Exception rollbackError)
            {
                string detail =
                    $"The value '{newName}' could not be removed after the copy. Rollback failed with {rollbackError.GetType().Name}: {rollbackError.Message}";

                if (deleteError is RegistryOperationException registryError)
                {
                    throw new RegistryOperationException(
                        registryError.Operation,
                        registryError.Path,
                        registryError.Win32Error,
                        detail);
                }

                throw new AggregateException(detail, deleteError, rollbackError);
            }

            throw;
        }
    }

    /// <summary>
    /// Changes only the casing of a value name: delete the old spelling, then write the same type and data
    /// under the new spelling. If the write fails, the old spelling is written back and the write error is
    /// rethrown. If that restore also fails, the error names both failures.
    /// </summary>
    private void RenameCaseOnly(RegistryPath key, string oldName, string newName, RegistryValueInfo existing)
    {
        _writer.DeleteValue(key, oldName);

        try
        {
            _writer.SetValue(key, newName, existing.Type, existing.Data);
        }
        catch (Exception writeError)
        {
            try
            {
                _writer.SetValue(key, oldName, existing.Type, existing.Data);
            }
            catch (Exception restoreError)
            {
                string detail =
                    $"The value '{oldName}' could not be restored after the rename failed. Restore failed with {restoreError.GetType().Name}: {restoreError.Message}";

                if (writeError is RegistryOperationException registryError)
                {
                    throw new RegistryOperationException(
                        registryError.Operation,
                        registryError.Path,
                        registryError.Win32Error,
                        detail);
                }

                throw new AggregateException(detail, writeError, restoreError);
            }

            throw;
        }
    }
}
