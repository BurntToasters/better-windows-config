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
        out SafeRegistryKeyHandle? handle) =>
        TryOpenKey(path, access, rootOverride: null, out handle);

    internal static unsafe WIN32_ERROR TryOpenKey(
        RegistryPath path,
        REG_SAM_FLAGS access,
        SafeRegistryKeyHandle? rootOverride,
        out SafeRegistryKeyHandle? handle)
    {
        if (rootOverride is null)
        {
            return TryOpenLocalKey(path, access, out handle);
        }

        if (path.Root is not RegistryRoot.LocalMachine and not RegistryRoot.Users)
        {
            handle = null;
            return WIN32_ERROR.ERROR_INVALID_PARAMETER;
        }

        if (path.IsRoot)
        {
            handle = SafeRegistryKeyHandle.FromBorrowed(rootOverride.DangerousGetHandle());
            return WIN32_ERROR.ERROR_SUCCESS;
        }

        return OpenKeyUnder(rootOverride, string.Join('\\', path.Components), access, out handle);
    }

    private static unsafe WIN32_ERROR TryOpenLocalKey(
        RegistryPath path,
        REG_SAM_FLAGS access,
        out SafeRegistryKeyHandle? handle)
    {
        if (path.IsRoot)
        {
            handle = SafeRegistryKeyHandle.FromPredefined(path.Root);
            return WIN32_ERROR.ERROR_SUCCESS;
        }

        return OpenKeyUnder(path.Root, string.Join('\\', path.Components), access, out handle);
    }

    private static unsafe WIN32_ERROR OpenKeyUnder(
        RegistryRoot root,
        string relativePath,
        REG_SAM_FLAGS access,
        out SafeRegistryKeyHandle? handle) =>
        OpenKeyUnder((HKEY)root.ToHkey(), relativePath, access, out handle);

    private static unsafe WIN32_ERROR OpenKeyUnder(
        SafeRegistryKeyHandle root,
        string relativePath,
        REG_SAM_FLAGS access,
        out SafeRegistryKeyHandle? handle) =>
        OpenKeyUnder((HKEY)root.DangerousGetHandle(), relativePath, access, out handle);

    private static unsafe WIN32_ERROR OpenKeyUnder(
        HKEY root,
        string relativePath,
        REG_SAM_FLAGS access,
        out SafeRegistryKeyHandle? handle)
    {
        HKEY opened;

        fixed (char* relativePathPointer = relativePath)
        {
            WIN32_ERROR error = PInvoke.RegOpenKeyEx(
                root,
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
