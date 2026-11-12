using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.UI.Dialogs;
using Bwc.Registry;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.Controllers;

/// <summary>Import and Export of .reg files.</summary>
internal sealed class ImportExportCommands
{
    /// <summary>
    /// The shortest time a progress dialog stays up. It keeps a fast import or export from flashing
    /// a window that the user cannot read or cancel.
    /// </summary>
    private static readonly TimeSpan ProgressMinimum = TimeSpan.FromMilliseconds(700);

    private const int ImportProgressEverySections = 20;
    private const int ExportProgressEveryKeys = 50;

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
    /// Applies a .reg file behind the classic progress dialog, refreshes the tree and the list, and
    /// then shows the classic result. The importer runs on the thread pool. Cancel stops between
    /// sections, keeps the sections that already finished, and shows string 132. A skipped line does
    /// not change the result. A refused file shows its own text, and a rejected write shows string
    /// 132. The import keeps going after a rejected write, as classic regedit does.
    /// </summary>
    private async Task ImportFileAsync(string filePath)
    {
        string caption = Get("MsgCaptionRegistryEditor");
        string? selectedPath = _context.Shell.TreeContent.SelectedNode?.RegistryPath;

        using var cancellation = new CancellationTokenSource();
        var progress = new ClassicProgressDialog(
            caption,
            string.Format(CultureInfo.CurrentCulture, "Importing {0}", Path.GetFileName(filePath)),
            Get("DialogCancel"),
            cancellation.Cancel);
        progress.SetIndeterminate("Preparing the import.");

        var progressSink = new ImportProgressSink(
            new Progress<ImportProgress>(update => progress.SetProgress(
                update.CompletedSections,
                update.TotalSections,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Imported {0} of {1} keys.",
                    update.CompletedSections,
                    update.TotalSections))));

        Task<bool> dialogTask = _context.ShowDialogAsync(progress);
        Task<RegFileImportResult> importTask = _context.RunRegistryAsync(
            () => RegFileImporter.ApplyFile(
                RegeditWritePolicy.CreateStore(),
                filePath,
                cancellation.Token,
                progressSink),
            cancellation.Token);

        RegFileImportResult? result = null;
        Exception? error = null;
        bool completed = false;
        bool cancelled = false;

        try
        {
            await Task.WhenAll(importTask, Task.Delay(ProgressMinimum));
            result = await importTask;
            completed = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (completed && !progress.Cancelled)
            {
                progress.CompleteFromHost();
            }
            else
            {
                cancellation.Cancel();
                CloseQuietly(progress);
            }
        }

        _ = await dialogTask;

        if (error is not null)
        {
            if (IsFileOperationFailure(error))
            {
                LogFileOperationFailure("import", filePath, error);
                await ShowFileOperationErrorAsync(RegistryAction.ImportFile, filePath, error);
                return;
            }

            ExceptionDispatchInfo.Capture(error).Throw();
            return;
        }

        if (!completed || cancelled)
        {
            Log.Warn(
                $"Registry Editor import of '{filePath}' canceled after {progressSink.CompletedSections} " +
                $"of {progressSink.TotalSections} key section(s). Completed writes stay applied.");
            await RefreshAfterImportAsync(selectedPath);
            await ShowImportCancelledAsync(filePath);
            return;
        }

        RegFileImportReport.LogOutcome(filePath, result!);
        await RefreshAfterImportAsync(selectedPath);
        await _context.ShowMessageAsync(
            caption,
            string.Format(CultureInfo.CurrentCulture, Get(RegFileImportReport.MessageKey(result!)), filePath));
    }

    private async Task RefreshAfterImportAsync(string? selectedPath)
    {
        await _context.RefreshTreeAsync(selectedPath);
        await _context.RefreshValuesAsync(selectDisplayName: null);
    }

    private Task ShowImportCancelledAsync(string filePath) =>
        _context.ShowMessageAsync(
            Get("MsgCaptionRegistryEditor"),
            string.Format(CultureInfo.CurrentCulture, Get("MsgImportPartial"), filePath));

    private async Task ExportAsync()
    {
        bool hasSelectedKey = _context.TryGetSelectedKeyPath(out RegistryPath selectedPath);
        bool computerSelected = _context.IsComputerSelected;
        if (!hasSelectedKey && !computerSelected)
        {
            await _context.ShowMessageAsync(Get("DialogExportNoSelection"));
            return;
        }

        string defaultFileName = hasSelectedKey
            ? BuildDefaultExportFileName(selectedPath)
            : BuildDefaultExportFileName("Computer");
        ExportFileDialogResult? choice = _context.ShowExportFileDialog(defaultFileName, hasSelectedKey);
        if (choice is null)
        {
            return;
        }

        if (choice.Range == ExportFileRange.SelectedBranch && !hasSelectedKey)
        {
            // The dialog disables Selected branch on Computer. A caller cannot reach this through
            // the UI, and the whole-registry export is the only meaningful Computer export.
            await _context.ShowMessageAsync(Get("DialogExportNoSelection"));
            return;
        }

        RegistryPath? startKey = choice.Range == ExportFileRange.SelectedBranch ? selectedPath : null;
        RegistryExportScope scope = choice.Range == ExportFileRange.WholeRegistry
            ? RegistryExportScope.WholeRegistry
            : RegistryExportScope.Selection;

        using var cancellation = new CancellationTokenSource();
        var progress = new ClassicProgressDialog(
            Get("MsgCaptionRegistryEditor"),
            string.Format(CultureInfo.CurrentCulture, "Exporting {0}", Path.GetFileName(choice.FilePath)),
            Get("DialogCancel"),
            cancellation.Cancel);
        progress.SetIndeterminate("Preparing the export.");

        var progressSink = new ExportProgressSink(
            new Progress<ExportProgress>(update => progress.SetIndeterminate(DescribeExport(update))));

        Task<bool> dialogTask = _context.ShowDialogAsync(progress);
        Task exportTask = _context.RunRegistryAsync(
            () => ExportToFile(choice, startKey, scope, cancellation.Token, progressSink),
            cancellation.Token);

        Exception? error = null;
        bool completed = false;
        bool cancelled = false;

        try
        {
            await Task.WhenAll(exportTask, Task.Delay(ProgressMinimum));
            completed = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (completed && !progress.Cancelled)
            {
                progress.CompleteFromHost();
            }
            else
            {
                cancellation.Cancel();
                CloseQuietly(progress);
            }
        }

        _ = await dialogTask;

        if (error is not null)
        {
            if (IsFileOperationFailure(error))
            {
                LogFileOperationFailure("export", choice.FilePath, error);
                await ShowFileOperationErrorAsync(RegistryAction.ExportFile, choice.FilePath, error);
                return;
            }

            ExceptionDispatchInfo.Capture(error).Throw();
            return;
        }

        if (!completed || cancelled)
        {
            Log.Info($"Registry Editor export of '{choice.FilePath}' was canceled. No file was written.");
            return;
        }

        // Classic regedit does not show a message after a successful export, and the oracle has no
        // export success string. The command ends silently.
        Log.Info($"Registry Editor export of '{choice.FilePath}' completed.");
    }

    /// <summary>
    /// Writes the export to a temporary file in the destination folder and moves it into place only
    /// after the writer finished. The temporary file is deleted on cancel or failure, so a canceled
    /// export writes nothing and never truncates an existing destination.
    /// </summary>
    private void ExportToFile(
        ExportFileDialogResult choice,
        RegistryPath? startKey,
        RegistryExportScope scope,
        CancellationToken cancellationToken,
        IProgress<ExportProgress> progress)
    {
        string tempPath = choice.FilePath + ".bwc-" + Guid.NewGuid().ToString("N") + ".tmp";
        bool moved = false;

        try
        {
            RegistryExportService service = CreateExportService(choice.Range);
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan))
            {
                switch (choice.Format)
                {
                    case ExportFileFormat.RegistrationRegedit4:
                        service.WriteRegFile(startKey, stream, RegFileFormat.Regedit4, scope, cancellationToken, progress);
                        break;

                    case ExportFileFormat.Text:
                        service.ExportTextFile(startKey, stream, scope, cancellationToken, progress);
                        break;

                    default:
                        service.WriteRegFile(startKey, stream, RegFileFormat.V5, scope, cancellationToken, progress);
                        break;
                }

                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, choice.FilePath, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved)
            {
                TryDelete(tempPath);
            }
        }
    }

    private RegistryExportService CreateExportService(ExportFileRange range)
    {
#if BWC_E2E
        if (range == ExportFileRange.WholeRegistry &&
            TryGetSandboxRoot(out RegistryPath sandboxRoot))
        {
            // A GUI test must not read the machine's whole registry into an artifact. The classic
            // roots are still exported through RegFileExporter.ExportWholeRegistry, but HKCU is
            // mapped to the run sandbox and the other roots are empty. The ship build uses the live
            // reader.
            return new RegistryExportService(new SandboxWholeRegistrySource(_context.Store.Reader, sandboxRoot));
        }
#endif

        return new RegistryExportService(_context.Store.Reader);
    }

#if BWC_E2E
    private static bool TryGetSandboxRoot(out RegistryPath sandboxRoot)
    {
        sandboxRoot = null!;
        string? configured = Environment.GetEnvironmentVariable("BWC_E2E_SANDBOX_ROOT");
        if (string.IsNullOrWhiteSpace(configured) ||
            !RegistryPath.TryParse(configured.Trim(), out RegistryPath? parsed) ||
            parsed is null)
        {
            return false;
        }

        sandboxRoot = parsed;
        return true;
    }

    /// <summary>
    /// E2E-only whole-registry source: the five classic roots are present, HKCU and everything below
    /// it map to the run sandbox, and the other roots contribute no keys. Exported paths keep the
    /// classic root names, so the document still proves the root order.
    /// </summary>
    private sealed class SandboxWholeRegistrySource : IRegistryExportSource
    {
        private readonly RegistryReader _reader;
        private readonly RegistryPath _sandboxRoot;

        internal SandboxWholeRegistrySource(RegistryReader reader, RegistryPath sandboxRoot)
        {
            _reader = reader;
            _sandboxRoot = sandboxRoot;
        }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default)
        {
            if (TryMap(path, out RegistryPath mapped))
            {
                return _reader.GetKeyInfo(mapped, cancellationToken);
            }

            return new RegistryKeyInfo(
                path.Root.DisplayName(),
                path,
                subKeyCount: 0,
                valueCount: 0,
                DateTime.UnixEpoch,
                maxSubKeyNameLength: 0,
                maxValueNameLength: 0,
                maxValueDataLength: 0,
                className: string.Empty);
        }

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default) =>
            TryMap(path, out RegistryPath mapped)
                ? _reader.EnumerateValues(mapped, cancellationToken)
                : Array.Empty<RegistryValueInfo>();

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default) =>
            TryMap(path, out RegistryPath mapped)
                ? _reader.EnumerateSubKeyNames(mapped, cancellationToken)
                : Array.Empty<string>();

        private bool TryMap(RegistryPath path, out RegistryPath mapped)
        {
            if (path.Root != RegistryRoot.CurrentUser)
            {
                mapped = null!;
                return false;
            }

            mapped = _sandboxRoot;
            foreach (string component in path.Components)
            {
                mapped = mapped.Append(component);
            }

            return true;
        }
    }
#endif

    private static string DescribeExport(ExportProgress update) =>
        update.Phase switch
        {
            ExportPhase.Starting => "Preparing the export.",
            ExportPhase.Completed =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Exported {0} keys and {1} values.",
                    update.KeysExported,
                    update.ValuesExported),
            ExportPhase.KeySkipped =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Skipped an unreadable key. Exported {0} keys and {1} values.",
                    update.KeysExported,
                    update.ValuesExported),
            _ =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Exported {0} keys and {1} values.",
                    update.KeysExported,
                    update.ValuesExported),
        };

    private static string BuildDefaultExportFileName(RegistryPath path) =>
        BuildDefaultExportFileName(
            path.Components.Count == 0 ? path.Root.DisplayName() : path.Components[^1]);

    private static string BuildDefaultExportFileName(string source)
    {
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Registry Editor could not delete the partial export '{path}': {ex.Message}");
        }
    }

    private static void CloseQuietly(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // The dialog was already closed or never shown. There is nothing else to release.
        }
    }

    /// <summary>
    /// Shows the classic text for a failed import or export. The text names the file the user chose.
    /// The engine detail stays in the log.
    /// </summary>
    private Task ShowFileOperationErrorAsync(RegistryAction action, string filePath, Exception exception) =>
        _context.ShowErrorAsync(_context.Messages.ForFileError(action, exception, filePath).ToErrorDialog());

    /// <summary>
    /// Counts every import report on the worker thread, but posts to the UI only for the first
    /// section, the last section, and every few sections in between. The counts remain exact for the
    /// cancel log even when the UI skipped a report.
    /// </summary>
    private sealed class ImportProgressSink : IProgress<ImportProgress>
    {
        private readonly IProgress<ImportProgress> _ui;

        internal ImportProgressSink(IProgress<ImportProgress> ui)
        {
            _ui = ui;
        }

        public int CompletedSections { get; private set; }

        public int TotalSections { get; private set; }

        public void Report(ImportProgress value)
        {
            CompletedSections = value.CompletedSections;
            TotalSections = value.TotalSections;

            if (value.CompletedSections == 1 ||
                value.CompletedSections == value.TotalSections ||
                value.CompletedSections % ImportProgressEverySections == 0)
            {
                _ui.Report(value);
            }
        }
    }

    /// <summary>
    /// Keeps the export progress stream bounded. Starting, Completed, and every skipped key are
    /// always reported; key progress is reported every few keys; per-value events are dropped because
    /// the key report already carries the running value count.
    /// </summary>
    private sealed class ExportProgressSink : IProgress<ExportProgress>
    {
        private readonly IProgress<ExportProgress> _ui;

        internal ExportProgressSink(IProgress<ExportProgress> ui)
        {
            _ui = ui;
        }

        public void Report(ExportProgress value)
        {
            if (value.Phase is ExportPhase.Starting or ExportPhase.Completed or ExportPhase.KeySkipped ||
                (value.Phase == ExportPhase.KeyExported && value.KeysExported % ExportProgressEveryKeys == 0))
            {
                _ui.Report(value);
            }
        }
    }
}
