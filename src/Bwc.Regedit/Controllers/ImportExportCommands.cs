using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Bwc.Core;
using Bwc.Regedit.Dialogs;
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
        if (RegeditSelectionGuards.IsRemoteSelected(_context))
        {
            // Remote computers are read-only in this version, so an import never targets one.
            await _context.ShowMessageAsync(Get("DialogRemoteReadOnly"));
            return;
        }

        ImportFileDialogResult? choice = RegeditFileDialogs.ShowImport(_context);
        if (choice is null)
        {
            return;
        }

        if (choice.Format == ImportFileFormat.Hive)
        {
            await RestoreHiveAsync(choice.FilePath);
            return;
        }

        await ImportFileAsync(choice.FilePath);
    }

    /// <summary>
    /// File > Import with the Registry Hive Files filter: restores a binary hive onto the selected key
    /// (RegRestoreKey). Classic requires the privilege, so a process without elevation is refused with
    /// string 126 and nothing runs. The restore replaces every value and subkey of the key, so it asks
    /// first with strings 55 and 56.
    /// </summary>
    private async Task RestoreHiveAsync(string filePath)
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(
                Get("MsgCaptionRegistryEditor"),
                string.Format(CultureInfo.CurrentCulture, Get("MsgImportInvalidKey"), filePath));
            return;
        }

        if (!RegeditSelectionGuards.IsElevated())
        {
            Log.Info($"Registry Editor refused to restore '{filePath}' because the process is not elevated.");
            await _context.ShowMessageAsync(
                Get("MsgCaptionRegistryEditor"),
                string.Format(CultureInfo.CurrentCulture, Get("MsgImportPrivileges"), filePath));
            return;
        }

        var confirm = new ConfirmDialog(
            Get("MsgRestoreKeyCaption"),
            string.Format(CultureInfo.CurrentCulture, Get("MsgRestoreKeyConfirm"), key.ToDisplayString()),
            Get("DialogYes"),
            Get("DialogNo"));
        if (!await _context.ShowDialogAsync(confirm))
        {
            return;
        }

        try
        {
            await _context.RunRegistryWriteAsync(() => _context.Store.RestoreKey(key, filePath));
        }
        catch (RegistryOperationException ex)
        {
            LogFileOperationFailure("restore", filePath, ex);
            await ShowFileOperationErrorAsync(RegistryAction.ImportFile, filePath, ex);
            return;
        }

        Log.Info($"Registry Editor restored '{filePath}' onto '{key.ToDisplayString()}'.");
        await RefreshAfterImportAsync(key.ToDisplayString());
    }

    /// <summary>
    /// Applies a .reg file behind the classic Import progress window (DLG-019). That window has no
    /// Cancel button and no close box, so the import always runs to the end. The importer runs on the
    /// thread pool and reports sections to the window. A skipped line does not change the result. A
    /// refused file shows its own text, and a rejected write shows string 132. The import keeps going
    /// after a rejected write, as classic regedit does.
    /// </summary>
    private async Task ImportFileAsync(string filePath)
    {
        string caption = Get("MsgCaptionRegistryEditor");
        string? selectedPath = _context.Shell.TreeContent.SelectedNode?.RegistryPath;

        var progress = new ImportProgressDialog(
            Get("MsgImportRegistryFileCaption"),
            Get("ImportProgressFileLabel"),
            Path.GetFileName(filePath));

        var progressSink = new ImportProgressSink(
            new Progress<ImportProgress>(update => progress.SetProgress(update.CompletedSections, update.TotalSections)));

        Task<bool> dialogTask = _context.ShowDialogAsync(progress);
        Task<RegFileImportResult> importTask = _context.RunRegistryAsync(
            () => RegFileImporter.ApplyFile(
                RegeditWritePolicy.CreateStore(),
                filePath,
                CancellationToken.None,
                progressSink));

        RegFileImportResult? result = null;
        Exception? error = null;

        try
        {
            await Task.WhenAll(importTask, Task.Delay(ProgressMinimum));
            result = await importTask;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            progress.CompleteFromHost();
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

    private async Task ExportAsync()
    {
        if (RegeditSelectionGuards.IsRemoteSelected(_context))
        {
            // Remote export is not in this version. The menu keeps Export disabled for a remote node.
            await _context.ShowMessageAsync(Get("DialogExportNoSelection"));
            return;
        }

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
        ExportFileDialogResult? choice = RegeditFileDialogs.ShowExport(_context, defaultFileName, hasSelectedKey);
        if (choice is null)
        {
            return;
        }

        // A hive file is one key saved with RegSaveKey, so the hive format needs a selected key, and
        // the whole registry from Computer is only available through the .reg and text writers.
        bool hive = choice.Format == ExportFileFormat.Hive;
        if ((choice.Range == ExportFileRange.SelectedBranch || hive) && !hasSelectedKey)
        {
            await _context.ShowMessageAsync(Get("DialogExportNoSelection"));
            return;
        }

        RegistryPath? startKey = choice.Range == ExportFileRange.SelectedBranch || hive ? selectedPath : null;
        RegistryExportScope scope = choice.Range == ExportFileRange.WholeRegistry
            ? RegistryExportScope.WholeRegistry
            : RegistryExportScope.Selection;

        using var cancellation = new CancellationTokenSource();
        var progress = new ClassicProgressDialog(
            Get("MsgCaptionRegistryEditor"),
            string.Format(CultureInfo.CurrentCulture, Get("ExportProgressTitle"), Path.GetFileName(choice.FilePath)),
            Get("DialogCancel"),
            cancellation.Cancel);
        progress.SetIndeterminate(Get("ExportProgressPreparing"));

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
    /// export writes nothing and never truncates an existing destination. A hive export has the same
    /// rule: RegSaveKey creates the temporary file, which is moved over the destination at the end.
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
            if (choice.Format == ExportFileFormat.Hive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new ExportProgress(ExportPhase.Starting, startKey!, null, 0, 0, 0));
                _context.Store.SaveKey(startKey!, tempPath);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, choice.FilePath, overwrite: true);
                moved = true;
                return;
            }

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
        IRegistryExportSource? source = null;
        if (range == ExportFileRange.WholeRegistry &&
            TryGetSandboxRoot(out RegistryPath sandboxRoot))
        {
            // A GUI test must not read the machine's whole registry into an artifact. The classic
            // roots are still exported through RegFileExporter.ExportWholeRegistry, but HKCU is
            // mapped to the run sandbox and the other roots are empty. The ship build uses the live
            // reader.
            source = new SandboxWholeRegistrySource(_context.Store.Reader, sandboxRoot);
        }

        int pauseMs = E2EPauseMs(SlowExportVariable);
        if (pauseMs > 0)
        {
            source = new PacedExportSource(source ?? new ReaderExportSource(_context.Store.Reader), pauseMs);
        }

        if (source is not null)
        {
            return new RegistryExportService(source);
        }
#endif

        return new RegistryExportService(_context.Store.Reader);
    }

#if BWC_E2E
    /// <summary>
    /// E2E only: the pause in milliseconds per exported key, from BWC_E2E_SLOW_EXPORT_MS. A seeded test
    /// branch finishes in a few seconds, which is too quick for a test to read the progress window and
    /// click Cancel. The value is clamped to 0 through 1000. The ship build never reads this variable.
    /// </summary>
    private const string SlowExportVariable = "BWC_E2E_SLOW_EXPORT_MS";

    /// <summary>
    /// E2E only: the pause in milliseconds per import section, from BWC_E2E_SLOW_IMPORT_MS. The same
    /// rule as the export pause applies: a short file must stay on screen long enough to read.
    /// </summary>
    private const string SlowImportVariable = "BWC_E2E_SLOW_IMPORT_MS";

    private static readonly int SlowImportPauseMs = E2EPauseMs(SlowImportVariable);

    private static int E2EPauseMs(string variable)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int ms)
            ? Math.Clamp(ms, 0, 1000)
            : 0;
    }

    /// <summary>E2E only: reads the live registry through the reader and pauses before each key.</summary>
    private sealed class ReaderExportSource : IRegistryExportSource
    {
        private readonly RegistryReader _reader;

        internal ReaderExportSource(RegistryReader reader)
        {
            _reader = reader;
        }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default) =>
            _reader.GetKeyInfo(path, cancellationToken);

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default) =>
            _reader.EnumerateValues(path, cancellationToken);

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default) =>
            _reader.EnumerateSubKeyNames(path, cancellationToken);
    }

    /// <summary>
    /// E2E only: wraps another source and pauses once per key, at its value read. The pause ends early on
    /// cancel, and the export then stops with OperationCanceledException as in the ship build.
    /// </summary>
    private sealed class PacedExportSource : IRegistryExportSource
    {
        private readonly IRegistryExportSource _inner;
        private readonly int _pauseMs;

        internal PacedExportSource(IRegistryExportSource inner, int pauseMs)
        {
            _inner = inner;
            _pauseMs = pauseMs;
        }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default) =>
            _inner.GetKeyInfo(path, cancellationToken);

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            // Every writer reads each key's values once, so the pause lands once per key in each format.
            _ = cancellationToken.WaitHandle.WaitOne(_pauseMs);
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.EnumerateValues(path, cancellationToken);
        }

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default) =>
            _inner.EnumerateSubKeyNames(path, cancellationToken);
    }
#endif

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

    private string DescribeExport(ExportProgress update) =>
        update.Phase switch
        {
            ExportPhase.Starting => Get("ExportProgressPreparing"),
            ExportPhase.KeySkipped =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    Get("ExportProgressSkipped"),
                    update.KeysExported,
                    update.ValuesExported),
            _ =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    Get("ExportProgressCounts"),
                    update.KeysExported,
                    update.ValuesExported),
        };

    private string BuildDefaultExportFileName(RegistryPath path) =>
        BuildDefaultExportFileName(
            path.Components.Count == 0 ? path.Root.DisplayName() : path.Components[^1]);

    private string BuildDefaultExportFileName(string source)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (char character in source)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        string name = builder.ToString().Trim().TrimEnd('.');
        return (name.Length == 0 ? Get("ExportFallbackName") : name) + ".reg";
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

#if BWC_E2E
            // E2E only: the pause runs on the worker thread after the section is counted.
            if (SlowImportPauseMs > 0)
            {
                Thread.Sleep(SlowImportPauseMs);
            }
#endif
        }
    }

    /// <summary>
    /// Forwards the export progress stream to the window. Every event is forwarded, so the window shows
    /// the first key at once. The engine throttles key progress (100 ms), so the sink does not count;
    /// per-value events are dropped because the key report already carries the running value count.
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
            if (value.Phase is ExportPhase.Starting or ExportPhase.Completed or ExportPhase.KeySkipped or ExportPhase.KeyExported)
            {
                _ui.Report(value);
            }
        }
    }
}
