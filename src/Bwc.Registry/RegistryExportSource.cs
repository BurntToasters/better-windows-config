namespace Bwc.Registry;

/// <summary>
/// The read operations an exporter needs. <see cref="RegistryReader"/> is wrapped by this
/// interface so the exporters can also be exercised with an in-memory tree.
/// </summary>
public interface IRegistryExportSource
{
    RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default);

    IReadOnlyList<RegistryValueInfo> EnumerateValues(
        RegistryPath path,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> EnumerateSubKeyNames(
        RegistryPath path,
        CancellationToken cancellationToken = default);
}

/// <summary>The roots classic regedit shows under Computer, in tree order.</summary>
public static class RegistryExportRoots
{
    public static IReadOnlyList<RegistryPath> Classic { get; } = Array.AsReadOnly(
        new[]
        {
            RegistryPath.Parse("HKEY_CLASSES_ROOT"),
            RegistryPath.Parse("HKEY_CURRENT_USER"),
            RegistryPath.Parse("HKEY_LOCAL_MACHINE"),
            RegistryPath.Parse("HKEY_USERS"),
            RegistryPath.Parse("HKEY_CURRENT_CONFIG"),
        });
}

internal sealed class RegistryReaderExportSource : IRegistryExportSource
{
    private readonly RegistryReader _reader;

    public RegistryReaderExportSource(RegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
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
