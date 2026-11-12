namespace Bwc.Registry;

/// <summary>
/// Builds a <see cref="RegFileDocument"/> from a live registry tree. Keys and values are read in
/// registry enumeration order, never in the UI's sorted order. The complete document is built in
/// memory; the caller owns writing it.
/// </summary>
public static class RegFileExporter
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidName = 123;
    private const string ExportValueNameOperation = "ExportValueName";
    private const string ComputerPrefix = "Computer\\";

    /// <summary>
    /// Exports <paramref name="startKey"/> and every readable descendant. An unreadable
    /// descendant is skipped with its subtree; an unreadable start key throws
    /// <see cref="RegistryOperationException"/>. A value whose name contains an embedded NUL also
    /// throws, with operation <c>ExportValueName</c>, and no document is returned.
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
        cancellationToken.ThrowIfCancellationRequested();

        var keys = new List<RegFileKey>();
        var state = new ExportState();
        progress?.Report(Starting(startKey, state));
        Walk(source, startKey, isStart: true, cancellationToken, progress, state, keys);
        progress?.Report(Completed(startKey, state));

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
        cancellationToken.ThrowIfCancellationRequested();

        var keys = new List<RegFileKey>();
        var state = new ExportState();
        IReadOnlyList<RegistryPath> roots = RegistryExportRoots.Classic;
        progress?.Report(Starting(roots[0], state));

        foreach (RegistryPath root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(source, root, isStart: false, cancellationToken, progress, state, keys);
        }

        progress?.Report(Completed(roots[^1], state));
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

    private static void Walk(
        IRegistryExportSource source,
        RegistryPath path,
        bool isStart,
        CancellationToken cancellationToken,
        IProgress<ExportProgress>? progress,
        ExportState state,
        List<RegFileKey> keys)
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
        catch (RegistryOperationException ex) when (!isStart && ex.Win32Error == ErrorAccessDenied)
        {
            state.KeysSkipped += 1;
            progress?.Report(new ExportProgress(
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

            // A .reg name cannot hold a NUL. Writing it would produce a name that is not the
            // registry's, and classic regedit stops the export at such a value without an error.
            if (value.Name.Contains('\0'))
            {
                throw new RegistryOperationException(
                    ExportValueNameOperation,
                    path,
                    ErrorInvalidName,
                    "A value name contains an embedded NUL and cannot be written to a .reg file.");
            }

            fileValues.Add(new RegFileValue(value.Name, value.Type, value.Data));
            state.ValuesExported += 1;
            progress?.Report(new ExportProgress(
                ExportPhase.ValueExported,
                path,
                value.Name,
                state.KeysExported,
                state.ValuesExported,
                state.KeysSkipped));
        }

        keys.Add(new RegFileKey(ToRegPath(path), fileValues));
        state.KeysExported += 1;
        progress?.Report(new ExportProgress(
            ExportPhase.KeyExported,
            path,
            null,
            state.KeysExported,
            state.ValuesExported,
            state.KeysSkipped));

        foreach (string subKeyName in subKeyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(source, path.Append(subKeyName), isStart: false, cancellationToken, progress, state, keys);
        }
    }

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
}
