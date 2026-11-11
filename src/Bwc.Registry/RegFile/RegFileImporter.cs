namespace Bwc.Registry;

/// <summary>
/// Applies a parsed <see cref="RegFileDocument"/> to a <see cref="RegistryStore"/>, the way
/// classic regedit <c>/s</c> does. Sections run in file order, and values run in their file
/// order. A rejected line or write is recorded and the import continues. The import is not
/// transactional: writes that succeeded stay applied.
/// </summary>
public static class RegFileImporter
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAlreadyExists = 183;

    /// <summary>
    /// Applies the document section by section and returns the counts and every failure. Progress
    /// is reported after each section finishes. Cancellation is checked before each section, so a
    /// started section is finished before the import stops, and the cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static RegFileImportResult Apply(
        RegistryStore store,
        RegFileDocument document,
        CancellationToken cancellationToken = default,
        IProgress<ImportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(document);

        var tally = new Tally();
        var failures = new List<RegFileImportFailure>();

        foreach (RegFileProblem problem in document.Problems)
        {
            failures.Add(new RegFileImportFailure(
                RegFileImportFailureKind.MalformedLine,
                problem.Line,
                KeyPath: null,
                ValueName: null,
                Win32Error: 0,
                problem.Message));
        }

        for (int index = 0; index < document.Keys.Count; index += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegFileKey key = document.Keys[index];
            RegistryPath path = ParsePath(key.Path);

            ApplySection(store, key, path, tally, failures);
            progress?.Report(new ImportProgress(index + 1, document.Keys.Count, key.Path));
        }

        return new RegFileImportResult(
            RegFileRefusal.None,
            tally.KeysWritten,
            tally.KeysDeleted,
            tally.ValuesWritten,
            tally.ValuesDeleted,
            failures.OrderBy(failure => failure.Line).ToArray());
    }

    /// <summary>
    /// Reads and parses a file the way classic regedit imports it, then applies it. A file that
    /// cannot be read or is not a registry file is refused and nothing is written.
    /// </summary>
    public static RegFileImportResult ApplyFile(
        RegistryStore store,
        string path,
        CancellationToken cancellationToken = default,
        IProgress<ImportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(path);

        RegFileDocument document;

        try
        {
            document = RegFileReader.ParseFileLenient(path);
        }
        catch (RegFileParseException error)
        {
            RegFileRefusal refusal = error.Refusal == RegFileRefusal.None
                ? RegFileRefusal.NotRegistryFile
                : error.Refusal;
            return Refused(refusal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused(RegFileRefusal.ReadFailed);
        }

        return Apply(store, document, cancellationToken, progress);
    }

    private static void ApplySection(
        RegistryStore store,
        RegFileKey key,
        RegistryPath path,
        Tally tally,
        List<RegFileImportFailure> failures)
    {
        if (key.Delete)
        {
            try
            {
                store.DeleteKey(path, recursive: true);
                tally.KeysDeleted += 1;
            }
            catch (RegistryOperationException ex) when (ex.Win32Error == ErrorFileNotFound)
            {
                // Deleting an absent key is a normal `.reg` outcome.
            }
            catch (Exception ex) when (ex is RegistryOperationException or ArgumentException)
            {
                failures.Add(WriteFailure(key.Line, key.Path, valueName: null, ex));
            }

            return;
        }

        try
        {
            EnsureKeyPath(store, path);
            tally.KeysWritten += 1;
        }
        catch (Exception ex) when (ex is RegistryOperationException or ArgumentException)
        {
            // The key cannot be opened, so none of its value lines can be written. One failure
            // names the section line.
            failures.Add(WriteFailure(key.Line, key.Path, valueName: null, ex));
            return;
        }

        foreach (RegFileValue value in key.Values)
        {
            if (value.Delete)
            {
                try
                {
                    store.DeleteValue(path, value.Name);
                    tally.ValuesDeleted += 1;
                }
                catch (RegistryOperationException ex) when (ex.Win32Error == ErrorFileNotFound)
                {
                    // Deleting an absent value is a normal `.reg` outcome.
                }
                catch (Exception ex) when (ex is RegistryOperationException or ArgumentException)
                {
                    failures.Add(WriteFailure(value.Line, key.Path, value.Name, ex));
                }

                continue;
            }

            try
            {
                store.SetValue(path, value.Name, (RegistryValueType)value.Type, value.Data);
                tally.ValuesWritten += 1;
            }
            catch (Exception ex) when (ex is RegistryOperationException or ArgumentException)
            {
                failures.Add(WriteFailure(value.Line, key.Path, value.Name, ex));
            }
        }
    }

    // Classic regedit creates every missing ancestor of a section's key. Each missing key is
    // created top down, so a parent is always present before its child. A predefined root always
    // exists, so nothing is created for it.
    private static void EnsureKeyPath(RegistryStore store, RegistryPath path)
    {
        if (path.IsRoot)
        {
            return;
        }

        var missing = new Stack<RegistryPath>();

        for (RegistryPath? current = path;
             current is not null && !current.IsRoot && !store.Reader.KeyExists(current);
             current = current.Parent)
        {
            missing.Push(current);
        }

        while (missing.Count > 0)
        {
            try
            {
                store.CreateKey(missing.Pop());
            }
            catch (RegistryOperationException ex) when (ex.Win32Error == ErrorAlreadyExists)
            {
                // A concurrent creator made the key first. The key exists, so nothing is lost.
            }
        }
    }

    private static RegFileImportFailure WriteFailure(
        int line,
        string keyPath,
        string? valueName,
        Exception error) =>
        new(
            RegFileImportFailureKind.WriteFailed,
            line,
            keyPath,
            valueName,
            error is RegistryOperationException registryError ? registryError.Win32Error : 0,
            error.Message);

    private static RegFileImportResult Refused(RegFileRefusal refusal) =>
        new(refusal, 0, 0, 0, 0, Array.Empty<RegFileImportFailure>());

    private static RegistryPath ParsePath(string path)
    {
        try
        {
            return RegistryPath.Parse(path);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                $"The .reg key path '{path}' is not a valid absolute registry path.",
                ex);
        }
    }

    private sealed class Tally
    {
        public int KeysWritten { get; set; }

        public int KeysDeleted { get; set; }

        public int ValuesWritten { get; set; }

        public int ValuesDeleted { get; set; }
    }
}
