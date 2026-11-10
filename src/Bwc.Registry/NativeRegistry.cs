using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Opens keys and always selects the 64-bit registry view. Mutating calls live only in
/// <see cref="RegistryWriter"/>.
/// </summary>
internal static class NativeRegistry
{
    internal static unsafe WIN32_ERROR TryOpenKey(
        RegistryPath path,
        REG_SAM_FLAGS access,
        out SafeRegistryKeyHandle? handle)
    {
        if (path.IsRoot)
        {
            handle = SafeRegistryKeyHandle.FromPredefined(path.Root);
            return WIN32_ERROR.ERROR_SUCCESS;
        }

        string relativePath = string.Join('\\', path.Components);
        HKEY opened;

        fixed (char* relativePathPointer = relativePath)
        {
            WIN32_ERROR error = PInvoke.RegOpenKeyEx(
                (HKEY)path.Root.ToHkey(),
                relativePathPointer,
                0,
                access | REG_SAM_FLAGS.KEY_WOW64_64KEY,
                &opened);

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                handle = null;
                return error;
            }
        }

        handle = SafeRegistryKeyHandle.FromOwned((nint)opened.Value);
        return WIN32_ERROR.ERROR_SUCCESS;
    }
}
