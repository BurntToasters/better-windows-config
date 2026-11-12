using System.Text;

namespace Bwc.Registry;

/// <summary>Which part of the registry an export covers.</summary>
public enum RegistryExportScope
{
    /// <summary>The selected key and its descendants.</summary>
    Selection,

    /// <summary>Every root classic regedit shows under Computer.</summary>
    WholeRegistry,
}

/// <summary>
/// UI-facing export facade over one exporter source and, for hive files, one writer. Every method
/// uses <see cref="RegFileFormat.V5"/> unless the caller asks for another format.
/// </summary>
public sealed class RegistryExportService
{
    private readonly IRegistryExportSource _source;
    private readonly RegistryWriter? _writer;

    public RegistryExportService(IRegistryExportSource source, RegistryWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _writer = writer;
    }

    public RegistryExportService(RegistryReader reader)
        : this(new RegistryReaderExportSource(reader))
    {
    }

    public RegistryExportService(RegistryReader reader, RegistryWriter writer)
        : this(new RegistryReaderExportSource(reader), writer)
    {
    }

    public RegistryExportService(RegistryWriter writer)
        : this(new RegistryReader(), writer)
    {
    }

    public RegistryExportService(RegistryStore store)
        : this(store.Reader, store.Writer)
    {
    }

    public RegFileDocument ExportRegFile(
        RegistryPath? startKey,
        RegFileFormat format = RegFileFormat.V5,
        RegistryExportScope scope = RegistryExportScope.Selection,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        if (scope == RegistryExportScope.WholeRegistry)
        {
            return RegFileExporter.ExportWholeRegistry(_source, format, cancellationToken, progress);
        }

        return RegFileExporter.Export(
            _source,
            RequireStartKey(startKey),
            format,
            cancellationToken,
            progress);
    }

    public void WriteRegFile(
        RegistryPath? startKey,
        Stream stream,
        RegFileFormat format = RegFileFormat.V5,
        RegistryExportScope scope = RegistryExportScope.Selection,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        RegFileDocument document = ExportRegFile(startKey, format, scope, cancellationToken, progress);

        if (format == RegFileFormat.V5)
        {
            RegFileWriter.WriteV5(document, stream);
        }
        else
        {
            RegFileWriter.WriteRegedit4(document, stream);
        }
    }

    public void ExportTextFile(
        RegistryPath? startKey,
        TextWriter writer,
        RegistryExportScope scope = RegistryExportScope.Selection,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (scope == RegistryExportScope.WholeRegistry)
        {
            RegistryTextExporter.ExportWholeRegistry(_source, writer, cancellationToken, progress);
            return;
        }

        RegistryTextExporter.Export(
            _source,
            RequireStartKey(startKey),
            writer,
            cancellationToken,
            progress);
    }

    /// <summary>
    /// Writes text as UTF-16LE with a BOM and CRLF line endings, the stable encoding documented in
    /// <c>docs/failure-modes/export.md</c>. The stream stays open.
    /// </summary>
    public void ExportTextFile(
        RegistryPath? startKey,
        Stream stream,
        RegistryExportScope scope = RegistryExportScope.Selection,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new StreamWriter(
            stream,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            bufferSize: 4096,
            leaveOpen: true);
        ExportTextFile(startKey, writer, scope, cancellationToken, progress);
    }

    public void ExportHiveFile(
        RegistryPath key,
        string filePath,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(filePath);

        if (_writer is null)
        {
            throw new InvalidOperationException("A hive export requires a RegistryWriter.");
        }

        RegistryHiveExporter.Save(_writer, key, filePath, cancellationToken, progress);
    }

    private static RegistryPath RequireStartKey(RegistryPath? startKey)
    {
        if (startKey is null)
        {
            throw new ArgumentNullException(
                nameof(startKey),
                "A start key is required unless the scope is WholeRegistry.");
        }

        return startKey;
    }
}
