namespace Bwc.Registry;

/// <summary>
/// Applies a fully parsed <see cref="RegFileDocument"/> to a <see cref="RegistryStore"/>. Sections
/// run in file order, and values run in their file order. An import is not transactional: a
/// native failure partway through a section leaves the calls that already succeeded applied.
/// </summary>
public static class RegFileImporter
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAlreadyExists = 183;

    /// <summary>
    /// Applies the document section by section. Cancellation is checked between complete key
    /// sections, so a started section is finished before the import stops.
    /// </summary>
    public static void Apply(
        RegistryStore store,
        RegFileDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(document);

        foreach (RegFileKey key in document.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegistryPath path = ParsePath(key.Path);

            if (key.Delete)
            {
                try
                {
                    store.DeleteKey(path, recursive: true);
                }
                catch (RegistryOperationException ex) when (ex.Win32Error == ErrorFileNotFound)
                {
                    // Deleting an absent key is a normal `.reg` outcome.
                }

                continue;
            }

            try
            {
                store.CreateKey(path);
            }
            catch (RegistryOperationException ex) when (ex.Win32Error == ErrorAlreadyExists)
            {
                // A future writer may report an existing key instead of opening it.
            }

            foreach (RegFileValue value in key.Values)
            {
                if (value.Delete)
                {
                    try
                    {
                        store.DeleteValue(path, value.Name);
                    }
                    catch (RegistryOperationException ex) when (ex.Win32Error == ErrorFileNotFound)
                    {
                        // Deleting an absent value is a normal `.reg` outcome.
                    }

                    continue;
                }

                store.SetValue(path, value.Name, (RegistryValueType)value.Type, value.Data);
            }
        }
    }

    /// <summary>Parses the whole file first, then applies it, so a malformed file writes nothing.</summary>
    public static void ApplyFile(
        RegistryStore store,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(path);

        RegFileDocument document = RegFileReader.ParseFile(path);
        Apply(store, document, cancellationToken);
    }

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
}
