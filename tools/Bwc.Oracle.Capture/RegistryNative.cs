using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bwc.Oracle.Capture;

// Raw advapi32 access for the cases Microsoft.Win32.Registry cannot express:
// writing REG_* types that RegistryKey.SetValue would coerce, and reading the
// unmodified type and bytes back for the seed-tree dump.
internal static class RegistryNative
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorMoreData = 234;
    internal const int ErrorFileNotFound = 2;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW", SetLastError = true)]
    internal static extern int RegSetValueExW(
        SafeRegistryHandle hKey,
        string lpValueName,
        uint reserved,
        uint dwType,
        byte[]? lpData,
        uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW", SetLastError = true)]
    internal static extern int RegQueryValueExW(
        SafeRegistryHandle hKey,
        string lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        byte[]? lpData,
        ref uint lpcbData);

    // RegSetValueExW appends a terminating NUL to REG_SZ/REG_EXPAND_SZ/REG_MULTI_SZ data that lacks one.
    // NtSetValueKey (the call underneath) stores the bytes exactly as given, which the no-NUL seed case needs.
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll", EntryPoint = "NtSetValueKey", SetLastError = false)]
    internal static extern int NtSetValueKey(
        SafeRegistryHandle keyHandle,
        ref UnicodeString valueName,
        uint titleIndex,
        uint type,
        byte[]? data,
        uint dataSize);

    internal static void SetExact(SafeRegistryHandle key, string valueName, uint type, byte[] data)
    {
        IntPtr namePtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(valueName);
        try
        {
            var us = new UnicodeString
            {
                Length = (ushort)(valueName.Length * 2),
                MaximumLength = (ushort)(valueName.Length * 2 + 2),
                Buffer = namePtr,
            };
            int status = NtSetValueKey(key, ref us, 0, type, data, (uint)data.Length);
            if (status != 0)
            {
                throw new InvalidOperationException($"NtSetValueKey failed for '{valueName}': 0x{status:X8}.");
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(namePtr);
        }
    }

    internal readonly record struct RawValue(uint Type, byte[] Data);

    // Reads the raw type and bytes of one value. Returns null when the value does not exist.
    internal static RawValue? QueryRaw(SafeRegistryHandle key, string valueName)
    {
        uint size = 0;
        int rc = RegQueryValueExW(key, valueName, IntPtr.Zero, out uint type, null, ref size);
        if (rc == ErrorFileNotFound)
        {
            return null;
        }

        if (rc != ErrorSuccess && rc != ErrorMoreData)
        {
            throw new System.ComponentModel.Win32Exception(rc, $"RegQueryValueExW probe failed for '{valueName}'.");
        }

        byte[] data = new byte[size];
        uint cb = size;
        rc = RegQueryValueExW(key, valueName, IntPtr.Zero, out type, data, ref cb);
        if (rc != ErrorSuccess)
        {
            throw new System.ComponentModel.Win32Exception(rc, $"RegQueryValueExW failed for '{valueName}'.");
        }

        if (cb != data.Length)
        {
            Array.Resize(ref data, (int)cb);
        }

        return new RawValue(type, data);
    }
}
