using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Owns a registry key handle and closes it with RegCloseKey. Predefined roots are wrapped as
/// non-owning handles so they are never closed. A pin holds an extra reference on another handle, so
/// the other handle's close is deferred until the pin is released.
/// </summary>
internal sealed class SafeRegistryKeyHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly SafeRegistryKeyHandle? _pinnedSource;

    private SafeRegistryKeyHandle(nint handle, bool ownsHandle, SafeRegistryKeyHandle? pinnedSource = null)
        : base(ownsHandle)
    {
        SetHandle(handle);
        _pinnedSource = pinnedSource;
    }

    internal static SafeRegistryKeyHandle FromOwned(nint handle) => new(handle, ownsHandle: true);

    internal static SafeRegistryKeyHandle FromBorrowed(nint handle) => new(handle, ownsHandle: false);

    internal static SafeRegistryKeyHandle FromPredefined(RegistryRoot root) =>
        new(root.ToHkey(), ownsHandle: false);

    /// <summary>
    /// Returns a handle to the same key that holds its own reference on <paramref name="source"/>. The
    /// key stays open until the returned handle is disposed, even if <paramref name="source"/> is disposed
    /// first. Throws <see cref="ObjectDisposedException"/> when <paramref name="source"/> is already closed.
    /// </summary>
    internal static SafeRegistryKeyHandle Pin(SafeRegistryKeyHandle source)
    {
        ArgumentNullException.ThrowIfNull(source);

        bool added = false;
        source.DangerousAddRef(ref added);
        if (!added)
        {
            throw new ObjectDisposedException(nameof(SafeRegistryKeyHandle));
        }

        return new SafeRegistryKeyHandle(source.DangerousGetHandle(), ownsHandle: true, pinnedSource: source);
    }

    protected override bool ReleaseHandle()
    {
        if (_pinnedSource is not null)
        {
            _pinnedSource.DangerousRelease();
            return true;
        }

        return PInvoke.RegCloseKey((HKEY)handle) == WIN32_ERROR.ERROR_SUCCESS;
    }
}
