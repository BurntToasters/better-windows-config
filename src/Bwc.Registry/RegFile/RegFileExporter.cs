using System.Diagnostics;

namespace Bwc.Registry;

/// <summary>
/// Builds a <see cref="RegFileDocument"/> from a live registry tree, or streams the same key sections
/// to a writer as they are read. Keys and values are read in registry enumeration order, never in the
/// UI's sorted order. The document overloads collect every key in memory; the caller owns writing it.
/// <see cref="RegistryExportService.WriteRegFile"/> uses the streaming path, so its memory use does not
/// grow with the size of the export.
/// </summary>
public static class RegFileExporter
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorKeyDeleted = 1018;
    private const int ErrorInvalidName = 123;
    private const string ExportValueNameOperation = "ExportValueName";
    private const string ExportKeyNameOperation = "ExportKeyName";
    private const string ComputerPrefix = "Computer\\";
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Exports <paramref name="startKey"/> and every readable descendant. An unreadable
    /// descendant is skipped with its subtree; an unreadable start key throws
    /// <see cref="RegistryOperationException"/>. A value or key name that cannot be written to a
    /// .reg file also throws, and no document is returned.
    /// </summary>
    public static RegFileDocument Export(
        RegistryReader reader,
        RegistryPath startKey,
        RegFileFormat format,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Export(
            new RegistryReaderExportSource(reader),
            startKey,
            format,
            cancellationToken,
            progress);
    }

    public static RegFileDocument Export(
        IRegistryExportSource source,
        RegistryPath startKey,
        RegFileFormat format,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(startKey);
        ValidateFormat(format);

        var keys = new List<RegFileKey>();
        ExportSubtree(source, startKey, cancellationToken, progress, keys.Add);
        return new RegFileDocument(format, keys);
    }

    /// <summary>
    /// Exports every root classic regedit shows under Computer in one document, in tree order. An
    /// inaccessible root or subkey is skipped with its subtree; the remaining roots are written.
    /// </summary>
    public static RegFileDocument ExportWholeRegistry(
        RegistryReader reader,
        RegFileFormat format,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ExportWholeRegistry(
            new RegistryReaderExportSource(reader),
            format,
            cancellationToken,
            progress);
    }

    public static RegFileDocument ExportWholeRegistry(
        IRegistryExportSource source,
        RegFileFormat format,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateFormat(format);

        var keys = new List<RegFileKey>();
        ExportWholeRegistryKeys(source, cancellationToken, progress, keys.Add);
        return new RegFileDocument(format, keys);
    }

    /// <summary>Exports the key named by a display path such as <c>Computer\HKEY_CURRENT_USER</c>.</summary>
    public static RegFileDocument Export(
        RegistryReader reader,
        string displayPath,
        RegFileFormat format,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(displayPath);

        return Export(reader, RegistryPath.Parse(displayPath), format, cancellationToken, progress);
    }

    /// <summary>
    /// Passes each key section of a subtree export to <paramref name="emit"/> in document order. A
    /// section is passed only after all of its values are read, so an exception from the walk never
    /// leaves a half-read section behind.
    /// </summary>
    internal static void ExportSubtree(
        IRegistryExportSource source,
        RegistryPath startKey,
        CancellationToken cancellationToken,
        IProgress<ExportProgress>? progress,
        Action<RegFileKey> emit)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = new ExportState();
        var gate = new ProgressGate(progress);
        gate.Always(Starting(startKey, state));
        Walk(source, startKey, isStart: true, cancellationToken, gate, state, emit);
        gate.Always(Completed(startKey, state));
    }

    /// <summary>Passes each key section of every classic root to <paramref name="emit"/>, in tree order.</summary>
    internal static void ExportWholeRegistryKeys(
        IRegistryExportSource source,
        CancellationToken cancellationToken,
        IProgress<ExportProgress>? progress,
        Action<RegFileKey> emit)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = new ExportState();
        var gate = new ProgressGate(progress);
        IReadOnlyList<RegistryPath> roots = RegistryExportRoots.Classic;
        gate.Always(Starting(roots[0], state));

        foreach (RegistryPath root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(source, root, isStart: false, cancellationToken, gate, state, emit);
        }

        gate.Always(Completed(roots[^1], state));
    }

    private static void Walk(
        IRegistryExportSource source,
        RegistryPath path,
        bool isStart,
        CancellationToken cancellationToken,
        ProgressGate gate,
        ExportState state,
        Action<RegFileKey> emit)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RegistryValueInfo> values;
        IReadOnlyList<string> subKeyNames;

        try
        {
            values = source.EnumerateValues(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            subKeyNames = source.EnumerateSubKeyNames(path, cancellationToken);
        }
        catch (RegistryOperationException ex) when (!isStart && IsSkippableKeyError(ex.Win32Error))
        {
            // Access denied, or a key that was deleted or never existed by the time it was read.
            state.KeysSkipped += 1;
            gate.Always(new ExportProgress(
                ExportPhase.KeySkipped,
                path,
                null,
                state.KeysExported,
                state.ValuesExported,
                state.KeysSkipped));
            return;
        }

        var fileValues = new List<RegFileValue>(values.Count);

        foreach (RegistryValueInfo value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A .reg name cannot hold a NUL, a line feed, or a carriage return. Writing one would
            // produce a name that is not the registry's, and classic regedit stops the export at
            // such a value without an error. The exporter refuses instead.
            if (value.Name.IndexOfAny(RegFileWriter.UnwritableNameCharacters) >= 0)
            {
                throw new RegistryOperationException(
                    ExportValueNameOperation,
                    path,
                    ErrorInvalidName,
                    "A value name contains a NUL, line feed, or carriage return and cannot be written to a .reg file.");
            }

            fileValues.Add(new RegFileValue(value.Name, value.Type, value.Data));
            state.ValuesExported += 1;
            gate.Throttled(new ExportProgress(
                ExportPhase.ValueExported,
                path,
                value.Name,
                state.KeysExported,
                state.ValuesExported,
                state.KeysSkipped));
        }

        emit(new RegFileKey(ToRegPath(path), fileValues));
        state.KeysExported += 1;
        gate.Throttled(new ExportProgress(
            ExportPhase.KeyExported,
            path,
            null,
            state.KeysExported,
            state.ValuesExported,
            state.KeysSkipped));

        foreach (string subKeyName in subKeyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (subKeyName.IndexOfAny(RegFileWriter.UnwritableNameCharacters) >= 0)
            {
                // Refused before the child is read, so no section for it is emitted.
                throw new RegistryOperationException(
                    ExportKeyNameOperation,
                    path,
                    ErrorInvalidName,
                    "A key name contains a NUL, line feed, or carriage return and cannot be written to a .reg file.");
            }

            Walk(source, path.Append(subKeyName), isStart: false, cancellationToken, gate, state, emit);
        }
    }

    private static bool IsSkippableKeyError(int win32Error) =>
        win32Error is ErrorAccessDenied or ErrorFileNotFound or ErrorKeyDeleted;

    private static ExportProgress Starting(RegistryPath path, ExportState state) =>
        new(ExportPhase.Starting, path, null, state.KeysExported, state.ValuesExported, state.KeysSkipped);

    private static ExportProgress Completed(RegistryPath path, ExportState state) =>
        new(ExportPhase.Completed, path, null, state.KeysExported, state.ValuesExported, state.KeysSkipped);

    private static string ToRegPath(RegistryPath path)
    {
        string display = path.ToDisplayString();
        return display.StartsWith(ComputerPrefix, StringComparison.Ordinal)
            ? display[ComputerPrefix.Length..]
            : display;
    }

    private static void ValidateFormat(RegFileFormat format)
    {
        if (format is not (RegFileFormat.V5 or RegFileFormat.Regedit4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "The document has an unknown .reg format.");
        }
    }

    private sealed class ExportState
    {
        public int KeysExported { get; set; }

        public int ValuesExported { get; set; }

        public int KeysSkipped { get; set; }
    }

    /// <summary>
    /// Passes progress to the caller. Starting, skip, and completion events always go through. Key and
    /// value events are throttled to one report per <see cref="ThrottleInterval"/> for each kind, so a large
    /// export does not flood the UI thread. The first event of each kind goes through at once. Value events
    /// have their own interval, so a value report cannot use up the slot of the key report that follows it.
    /// Completion carries the final running totals.
    /// </summary>
    private sealed class ProgressGate
    {
        private readonly IProgress<ExportProgress>? _progress;
        private long _lastKeyReport;
        private long _lastValueReport;

        internal ProgressGate(IProgress<ExportProgress>? progress)
        {
            _progress = progress;
        }

        internal void Always(ExportProgress report)
        {
            _progress?.Report(report);
        }

        internal void Throttled(ExportProgress report)
        {
            if (_progress is null)
            {
                return;
            }

            ref long last = ref report.Phase == ExportPhase.ValueExported ? ref _lastValueReport : ref _lastKeyReport;
            long now = Stopwatch.GetTimestamp();
            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ThrottleInterval)
            {
                return;
            }

            last = now;
            _progress.Report(report);
        }
    }
}
