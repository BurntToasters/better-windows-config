using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Owns a registry key handle and closes it with RegCloseKey. Predefined roots are wrapped as
/// non-owning handles so they are never closed.
/// </summary>
internal sealed class SafeRegistryKeyHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeRegistryKeyHandle(nint handle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    internal static SafeRegistryKeyHandle FromOwned(nint handle) => new(handle, ownsHandle: true);

    internal static SafeRegistryKeyHandle FromPredefined(RegistryRoot root) =>
        new(root.ToHkey(), ownsHandle: false);

    protected override bool ReleaseHandle()
    {
        return PInvoke.RegCloseKey((HKEY)handle) == WIN32_ERROR.ERROR_SUCCESS;
    }
}
