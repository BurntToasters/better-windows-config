using System.Text;
using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.Registry;

namespace Bwc.Regedit.Controllers;

/// <summary>Import and Export of .reg files.</summary>
internal sealed class ImportExportCommands
{
    private readonly RegeditContext _context;

    public ImportExportCommands(RegeditContext context)
    {
        _context = context;
    }

    private string Get(string key) => _context.Messages.Get(key);

    public void Import() => _context.StartCommand(ImportAsync);

    public void Export() => _context.StartCommand(ExportAsync);

    private async Task ImportAsync()
    {
        string? filePath = _context.ShowFileDialog(save: false, defaultFileName: null);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        string? selectedPath = _context.Shell.TreeContent.SelectedNode?.RegistryPath;

        try
        {
            await _context.RunRegistryAsync(() =>
            {
                RegistryStore store = RegeditWritePolicy.CreateStore();
                RegFileImporter.ApplyFile(store, filePath, CancellationToken.None);
            });
        }
        catch (Exception ex) when (IsFileOperationFailure(ex))
        {
            LogFileOperationFailure("import", filePath, ex);
            await ShowFileOperationErrorAsync(RegistryAction.ImportFile, filePath, ex);
            return;
        }

        await _context.RefreshTreeAsync(selectedPath);
        await _context.RefreshValuesAsync(selectDisplayName: null);
    }

    private async Task ExportAsync()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath selectedPath))
        {
            await _context.ShowMessageAsync(Get("DialogExportNoSelection"));
            return;
        }

        string defaultFileName = BuildDefaultExportFileName(selectedPath);
        string? filePath = _context.ShowFileDialog(save: true, defaultFileName);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        try
        {
            await _context.RunRegistryAsync(() =>
            {
                RegFileDocument document = RegFileExporter.Export(
                    _context.Store.Reader,
                    selectedPath,
                    RegFileFormat.V5,
                    CancellationToken.None);

                using var buffer = new MemoryStream();
                RegFileWriter.WriteV5(document, buffer);
                File.WriteAllBytes(filePath, buffer.ToArray());
            });
        }
        catch (Exception ex) when (IsFileOperationFailure(ex))
        {
            LogFileOperationFailure("export", filePath, ex);
            await ShowFileOperationErrorAsync(RegistryAction.ExportFile, filePath, ex);
        }
    }

    private static string BuildDefaultExportFileName(RegistryPath path)
    {
        string source = path.Components.Count == 0
            ? path.Root.DisplayName()
            : path.Components[^1];

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (char character in source)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        string name = builder.ToString().Trim().TrimEnd('.');
        return (name.Length == 0 ? MessageCatalog.FallbackExportName : name) + ".reg";
    }

    private static bool IsFileOperationFailure(Exception exception) =>
        exception is RegFileParseException
            or RegistryOperationException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private static void LogFileOperationFailure(string operation, string path, Exception exception) =>
        Log.Warn(
            $"Registry Editor {operation} of '{path}' failed: {exception.GetType().Name}: {exception.Message}");

    /// <summary>
    /// Shows the classic text for a failed import or export. The text names the file the user chose.
    /// The engine detail stays in the log.
    /// </summary>
    private Task ShowFileOperationErrorAsync(RegistryAction action, string filePath, Exception exception) =>
        _context.ShowErrorAsync(_context.Messages.ForFileError(action, exception, filePath).ToErrorDialog());
}
