namespace Bwc.Registry;

/// <summary>
/// Builds a <see cref="RegFileDocument"/> from a live registry tree. Keys and values are read in
/// registry enumeration order, never in the UI's sorted order. The complete document is built in
/// memory; the caller owns writing it.
/// </summary>
public static class RegFileExporter
{
    private const int ErrorAccessDenied = 5;
    private const string ComputerPrefix = "Computer\\";

    /// <summary>
    /// Exports <paramref name="startKey"/> and every readable descendant. An unreadable
    /// descendant is skipped with its subtree; an unreadable start key throws
    /// <see cref="RegistryOperationException"/>.
    /// </summary>
    public static RegFileDocument Export(
        RegistryReader reader,
        RegistryPath startKey,
        RegFileFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(startKey);
        ValidateFormat(format);
        cancellationToken.ThrowIfCancellationRequested();

        var keys = new List<RegFileKey>();

        Walk(startKey, isStart: true);

        return new RegFileDocument(format, keys);

        void Walk(RegistryPath path, bool isStart)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<RegistryValueInfo> values;
            IReadOnlyList<string> subKeyNames;

            try
            {
                values = reader.EnumerateValues(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                subKeyNames = reader.EnumerateSubKeyNames(path, cancellationToken);
            }
            catch (RegistryOperationException ex) when (!isStart && ex.Win32Error == ErrorAccessDenied)
            {
                return;
            }

            var fileValues = new List<RegFileValue>(values.Count);

            foreach (RegistryValueInfo value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileValues.Add(new RegFileValue(value.Name, value.Type, value.Data));
            }

            keys.Add(new RegFileKey(ToRegPath(path), fileValues));

            foreach (string subKeyName in subKeyNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Walk(path.Append(subKeyName), isStart: false);
            }
        }
    }

    /// <summary>Exports the key named by a display path such as <c>Computer\HKEY_CURRENT_USER</c>.</summary>
    public static RegFileDocument Export(
        RegistryReader reader,
        string displayPath,
        RegFileFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(displayPath);

        return Export(reader, RegistryPath.Parse(displayPath), format, cancellationToken);
    }

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
}
