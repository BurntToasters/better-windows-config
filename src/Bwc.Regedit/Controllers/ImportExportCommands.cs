using System.Globalization;
using System.Text;
using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.UI.Dialogs;
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

    /// <summary>
    /// Imports a .reg file named on the command line or sent by a second launch. Classic regedit asks
    /// first (RT_STRING 304), then reports the result (RT_STRING 228).
    /// </summary>
    public void ImportRegistryFile(string filePath) => _context.StartCommand(() => ImportPathAsync(filePath));

    private async Task ImportPathAsync(string filePath)
    {
        string caption = Get("MsgCaptionRegistryEditor");
        if (!File.Exists(filePath))
        {
            await _context.ShowMessageAsync(caption, string.Format(CultureInfo.CurrentCulture, Get("MsgImportFileMissing"), filePath));
            return;
        }

        var confirm = new ConfirmDialog(
            caption,
            string.Format(CultureInfo.CurrentCulture, Get("MsgImportConfirm"), filePath),
            Get("DialogYes"),
            Get("DialogNo"));
        if (!await _context.ShowDialogAsync(confirm))
        {
            return;
        }

        await ImportFileAsync(filePath);
    }

    private async Task ImportAsync()
    {
        string? filePath = _context.ShowFileDialog(save: false, defaultFileName: null);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        await ImportFileAsync(filePath);
    }

    /// <summary>
    /// Applies a .reg file, refreshes the tree and the list, and then shows the classic result. A skipped
    /// line does not change the result. A refused file shows its own text, and a rejected write shows
    /// string 132. The import keeps going after a rejected write, as classic regedit does.
    /// </summary>
    private async Task ImportFileAsync(string filePath)
    {
        string? selectedPath = _context.Shell.TreeContent.SelectedNode?.RegistryPath;
        RegFileImportResult result;
        try
        {
            result = await _context.RunRegistryAsync(() =>
            {
                RegistryStore store = RegeditWritePolicy.CreateStore();
                return RegFileImporter.ApplyFile(store, filePath, CancellationToken.None);
            });
        }
        catch (Exception ex) when (IsFileOperationFailure(ex))
        {
            LogFileOperationFailure("import", filePath, ex);
            await ShowFileOperationErrorAsync(RegistryAction.ImportFile, filePath, ex);
            return;
        }

        RegFileImportReport.LogOutcome(filePath, result);
        await _context.RefreshTreeAsync(selectedPath);
        await _context.RefreshValuesAsync(selectDisplayName: null);
        await _context.ShowMessageAsync(
            Get("MsgCaptionRegistryEditor"),
            string.Format(CultureInfo.CurrentCulture, Get(RegFileImportReport.MessageKey(result)), filePath));
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
