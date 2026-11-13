#if BWC_E2E
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.System.Registry;

[assembly: InternalsVisibleTo("Bwc.Registry.Tests")]

namespace Bwc.Registry;

/// <summary>
/// E2E-only seam for value names that contain an embedded NUL. The Win32 value APIs take
/// NUL-terminated names, so the only way to create or delete such a value is NtSetValueKey or
/// NtDeleteValueKey with a counted UNICODE_STRING. The seam still asks the write policy first and
/// opens the key with KEY_SET_VALUE, so the sandbox rules apply. Shipping builds do not compile
/// this file, and the public writer never reaches it.
/// </summary>
public sealed partial class RegistryWriter
{
    private const uint StatusObjectNameNotFound = 0xC0000034;

    /// <summary>
    /// Sets a value whose name may contain NUL characters. Test seam only.
    /// </summary>
    internal void SetValueWithCountedName(
        RegistryPath key,
        string valueName,
        uint type,
        ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueName);

        const string Operation = nameof(RegistryWriteOperation.SetValue);
        _policy.EnsureWriteAllowed(key, RegistryWriteOperation.SetValue);

        if (type == (uint)REG_VALUE_TYPE.REG_LINK && !_policy.AllowsLinkValues)
        {
            throw new RegistryOperationException(Operation, key, ErrorAccessDenied);
        }

        using SafeRegistryKeyHandle handle = OpenKey(
            key,
            REG_SAM_FLAGS.KEY_SET_VALUE,
            Operation,
            key);

        uint status = NtSetValueKeyCounted(handle.DangerousGetHandle(), valueName, type, data);
        if (status != 0)
        {
            throw new InvalidOperationException($"NtSetValueKey failed with status 0x{status:X8}.");
        }
    }

    /// <summary>
    /// Deletes a value whose name may contain NUL characters. Returns false when no such value
    /// exists. Test seam only.
    /// </summary>
    internal bool DeleteValueWithCountedName(RegistryPath path, string valueName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(valueName);

        const string Operation = nameof(RegistryWriteOperation.DeleteValue);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.DeleteValue);

        using SafeRegistryKeyHandle handle = OpenKey(
            path,
            REG_SAM_FLAGS.KEY_SET_VALUE,
            Operation,
            path);

        uint status = NtDeleteValueKeyCounted(handle.DangerousGetHandle(), valueName);
        if (status == StatusObjectNameNotFound)
        {
            return false;
        }

        if (status != 0)
        {
            throw new InvalidOperationException($"NtDeleteValueKey failed with status 0x{status:X8}.");
        }

        return true;
    }

    private static unsafe uint NtSetValueKeyCounted(
        IntPtr keyHandle,
        string valueName,
        uint type,
        ReadOnlySpan<byte> data)
    {
        fixed (char* namePointer = valueName)
        fixed (byte* dataPointer = data)
        {
            var name = new CountedUnicodeString
            {
                Length = checked((ushort)(valueName.Length * sizeof(char))),
                MaximumLength = checked((ushort)(valueName.Length * sizeof(char))),
                Buffer = (IntPtr)namePointer,
            };

            return (uint)NtSetValueKey(keyHandle, &name, 0, type, dataPointer, checked((uint)data.Length));
        }
    }

    private static unsafe uint NtDeleteValueKeyCounted(IntPtr keyHandle, string valueName)
    {
        fixed (char* namePointer = valueName)
        {
            var name = new CountedUnicodeString
            {
                Length = checked((ushort)(valueName.Length * sizeof(char))),
                MaximumLength = checked((ushort)(valueName.Length * sizeof(char))),
                Buffer = (IntPtr)namePointer,
            };

            return (uint)NtDeleteValueKey(keyHandle, &name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CountedUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern unsafe int NtSetValueKey(
        IntPtr keyHandle,
        CountedUnicodeString* valueName,
        uint titleIndex,
        uint type,
        byte* data,
        uint dataSize);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern unsafe int NtDeleteValueKey(
        IntPtr keyHandle,
        CountedUnicodeString* valueName);
}
#endif
