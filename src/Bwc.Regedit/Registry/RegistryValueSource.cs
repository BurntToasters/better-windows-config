using Bwc.Core;
using Bwc.Regedit.Controllers;
using Bwc.Registry;
using Bwc.UI.List;

namespace Bwc.Regedit.Registry;

/// <summary>
/// The rows of one key and the display-name to raw-name map that goes with them. Built on the thread
/// pool and never stored there: the caller applies it on the UI thread, and only when it is still the
/// current load.
/// </summary>
public sealed record RegistryValueListing(
    IReadOnlyList<DetailsRow> Rows,
    IReadOnlyDictionary<string, string> RawNamesByName);

/// <summary>
/// Loads the details rows for one key. Enumeration and formatting run on the thread pool and return a
/// <see cref="RegistryValueListing"/>; the caller applies it on the UI thread with <see cref="Apply"/>.
/// Registry failures produce an empty list, never an exception to the window.
/// </summary>
public sealed class RegistryValueSource
{
    /// <summary>Type column text of the synthetic default row (classic regedit shows REG_SZ).</summary>
    private static readonly string SyntheticDefaultType = RegistryValueTypeExtensions.ToTypeName((uint)RegistryValueType.String);
    private static readonly string SyntheticDefaultData = MessageCatalog.ValueNotSetData;
    private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;
    private static readonly IReadOnlyList<DetailsRow> NoRows = Array.Empty<DetailsRow>();
    private static readonly IReadOnlyDictionary<string, string> NoRawNames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly RegistryReader _reader;

    public RegistryValueSource(RegistryReader? reader = null)
    {
        _reader = reader ?? new RegistryReader();
    }

    /// <summary>
    /// Display name to raw registry name for the rows last applied with <see cref="Apply"/>. Read and
    /// written on the UI thread only.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawNamesByName { get; private set; } = NoRawNames;

    /// <summary>Reads the rows of a key on the thread pool. The result is not stored in this source.</summary>
    public Task<RegistryValueListing> ReadAsync(RegistryPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Makes a listing the current one for name lookups. Call on the UI thread, and only for the load
    /// that is still current.
    /// </summary>
    public void Apply(RegistryValueListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        RawNamesByName = listing.RawNamesByName;
    }

    private RegistryValueListing Read(RegistryPath path, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<RegistryValueInfo> values = _reader.EnumerateValues(path, cancellationToken);

            // The default value sorts first, then the remaining names in the approximate
            // linguistic order used by the tree. OrderBy is stable, so equal names keep
            // enumeration order.
            List<RegistryValueInfo> sorted = values
                .OrderBy(value => value.Name.Length == 0 ? 0 : 1)
                .ThenBy(value => value.Name, NameComparer)
                .ToList();

            var rows = new List<DetailsRow>(sorted.Count + 1);
            var rawNames = new Dictionary<string, string>(StringComparer.Ordinal);

            // Classic regedit lists (Default) for every key. A key without a default value gets a
            // synthetic first row with no registry value behind it, so it is not in the raw name map.
            if (sorted.Count == 0 || sorted[0].Name.Length > 0)
            {
                rows.Add(new DetailsRow(
                    MessageCatalog.DefaultValueLabel,
                    SyntheticDefaultType,
                    SyntheticDefaultData,
                    renameable: false));
            }

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

            return new RegistryValueListing(rows, rawNames);
        }
        catch (RegistryOperationException ex)
        {
            Log.Warn(
                $"Registry values for '{path.ToDisplayString()}' could not be read: Win32 {ex.Win32Error} ({ex.Message}).");
            return new RegistryValueListing(NoRows, NoRawNames);
        }
    }
}
