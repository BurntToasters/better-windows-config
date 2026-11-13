namespace Bwc.Registry;

/// <summary>
/// Saves one subtree to a hive file through <see cref="RegistryWriter.SaveKey"/>, so the write
/// policy and the SeBackupPrivilege / SeRestorePrivilege path stay in one place. Cancellation is
/// observed before the native save starts; a save already in progress cannot be interrupted.
/// </summary>
public static class RegistryHiveExporter
{
    public static void Save(
        RegistryWriter writer,
        RegistryPath key,
        string filePath,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ExportProgress(ExportPhase.Starting, key, null, 0, 0, 0));
        writer.SaveKey(key, filePath);
        progress?.Report(new ExportProgress(ExportPhase.Completed, key, null, 1, 0, 0));
    }
}
