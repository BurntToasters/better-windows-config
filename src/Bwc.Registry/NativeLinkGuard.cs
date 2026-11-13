using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Read-only native helpers for the link guard: the predefined root handle, child-name lookup,
/// link detection, and subkey enumeration. None of these create or change a key. Opening a child as
/// a link node needs a create call, so it lives in <see cref="RegistryWriter"/> (RegistryWriter.Guard.cs).
/// </summary>
internal static class NativeLinkGuard
{
    internal const string LinkValueName = "SymbolicLinkValue";

    private const uint KeyWow64_64Key = 0x0100;

    /// <summary>Opens a real, owned handle to a predefined root so it can serve as a walk base.</summary>
    internal static unsafe WIN32_ERROR TryOpenRoot(RegistryRoot root, out SafeRegistryKeyHandle? handle)
    {
        HKEY opened;
        WIN32_ERROR error;

        fixed (char* empty = string.Empty)
        {
            error = PInvoke.RegOpenKeyEx(
                (HKEY)root.ToHkey(),
                empty,
                0,
                REG_SAM_FLAGS.KEY_READ | (REG_SAM_FLAGS)KeyWow64_64Key,
                &opened);
        }

        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle = null;
            return error;
        }

        handle = SafeRegistryKeyHandle.FromOwned((nint)opened.Value);
        return WIN32_ERROR.ERROR_SUCCESS;
    }

    /// <summary>
    /// Sets <paramref name="exists"/> when a subkey named <paramref name="name"/> is listed under
    /// <paramref name="parent"/>. Enumeration lists link nodes by name without following them.
    /// </summary>
    internal static WIN32_ERROR TryFindChildName(SafeRegistryKeyHandle parent, string name, out bool exists)
    {
        exists = false;

        for (uint index = 0; ; index += 1)
        {
            WIN32_ERROR error = TryEnumerateSubKey(parent, index, out string? candidate);
            if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                return WIN32_ERROR.ERROR_SUCCESS;
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return error;
            }

            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                return WIN32_ERROR.ERROR_SUCCESS;
            }
        }
    }

    /// <summary>
    /// Sets <paramref name="isLink"/> to true when the key holds a <c>SymbolicLinkValue</c> of type
    /// <c>REG_LINK</c>. The key must have been opened with query rights. A failed query is returned
    /// as an error so the caller can refuse the write.
    /// </summary>
    internal static WIN32_ERROR TryQueryIsSymbolicLink(SafeHandle key, out bool isLink)
    {
        uint size = 0;
        WIN32_ERROR error = PInvoke.RegQueryValueEx(
            key,
            LinkValueName,
            out REG_VALUE_TYPE type,
            Span<byte>.Empty,
            ref size);

        if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
        {
            isLink = false;
            return WIN32_ERROR.ERROR_SUCCESS;
        }

        if (error == WIN32_ERROR.ERROR_SUCCESS || error == WIN32_ERROR.ERROR_MORE_DATA)
        {
            isLink = type == REG_VALUE_TYPE.REG_LINK;
            return WIN32_ERROR.ERROR_SUCCESS;
        }

        isLink = false;
        return error;
    }

    /// <summary>Returns the name of the subkey at <paramref name="index"/>, or ERROR_NO_MORE_ITEMS at the end.</summary>
    internal static WIN32_ERROR TryEnumerateSubKey(SafeRegistryKeyHandle key, uint index, out string? name)
    {
        Span<char> buffer = stackalloc char[256];
        uint length = (uint)buffer.Length;

        WIN32_ERROR error = PInvoke.RegEnumKeyEx(
            key,
            index,
            buffer,
            ref length,
            Span<char>.Empty);

        if (error == WIN32_ERROR.ERROR_SUCCESS)
        {
            name = new string(buffer[..(int)length]);
            return error;
        }

        name = null;
        return error;
    }
}
