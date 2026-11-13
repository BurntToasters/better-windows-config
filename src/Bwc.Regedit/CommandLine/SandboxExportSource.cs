#if BWC_E2E
using Bwc.Registry;

namespace Bwc.Regedit.CommandLine;

/// <summary>
/// E2E-only whole-registry source for the command line export. The five classic roots are present,
/// HKEY_CURRENT_USER and everything below it map to the run sandbox, and the other roots contribute no
/// keys. A GUI test must not read the machine's whole registry into an artifact, so the E2E build
/// exports the sandbox instead. The ship build uses the live reader. Mirrors the source the window's
/// Export uses under the same guard.
/// </summary>
internal sealed class SandboxExportSource : IRegistryExportSource
{
    private readonly RegistryReader _reader;
    private readonly RegistryPath _sandboxRoot;

    internal SandboxExportSource(RegistryReader reader, RegistryPath sandboxRoot)
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
