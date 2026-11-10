using Bwc.Core;
using Bwc.Registry;
using Bwc.UI.List;

namespace Bwc.Regedit.Registry;

/// <summary>
/// Loads the details rows for one key. Enumeration and formatting run on the thread pool; the
/// caller applies the result on the UI thread. Registry failures produce an empty list, never an
/// exception to the window.
/// </summary>
public sealed class RegistryValueSource
{
    private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;
    private static readonly IReadOnlyList<DetailsRow> NoRows = Array.Empty<DetailsRow>();
    private static readonly IReadOnlyDictionary<string, string> NoRawNames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly RegistryReader _reader;

    public RegistryValueSource(RegistryReader? reader = null)
    {
        _reader = reader ?? new RegistryReader();
    }

    /// <summary>The last read failure text, or null after a successful load.</summary>
    public string? LastError { get; private set; }

    /// <summary>Display name to raw registry name. Kept for a future rename editor.</summary>
    public IReadOnlyDictionary<string, string> RawNamesByName { get; private set; } = NoRawNames;

    public Task<IReadOnlyList<DetailsRow>> LoadAsync(RegistryPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Load(path, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DetailsRow> Load(RegistryPath path, CancellationToken cancellationToken)
    {
        try
        {
            LastError = null;
            IReadOnlyList<RegistryValueInfo> values = _reader.EnumerateValues(path, cancellationToken);

            // The default value sorts first, then the remaining names in the approximate
            // linguistic order used by the tree. OrderBy is stable, so equal names keep
            // enumeration order.
            List<RegistryValueInfo> sorted = values
                .OrderBy(value => value.Name.Length == 0 ? 0 : 1)
                .ThenBy(value => value.Name, NameComparer)
                .ToList();

            var rows = new List<DetailsRow>(sorted.Count);
            var rawNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (RegistryValueInfo value in sorted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string displayName = RegistryValueDisplay.FormatName(value);
                rows.Add(new DetailsRow(
                    displayName,
                    RegistryValueDisplay.FormatType(value),
                    RegistryValueDisplay.FormatData(value),
                    renameable: value.Name.Length > 0));
                rawNames[displayName] = value.Name;
            }

            RawNamesByName = rawNames;
            return rows;
        }
        catch (RegistryOperationException ex)
        {
            LastError = ex.Message;
            RawNamesByName = NoRawNames;
            Log.Warn(
                $"Registry values for '{path.ToDisplayString()}' could not be read: Win32 {ex.Win32Error} ({ex.Message}).");
            return NoRows;
        }
    }
}
