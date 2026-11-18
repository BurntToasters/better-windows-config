using System.Runtime.InteropServices;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Reads System.AppUserModel.ID from the shell property store of a top-level window. Read only: nothing is set, committed
/// or changed. The store of a window of another process may be refused or may hold no id, so the result tells the two
/// apart and the caller decides what a missing id means (docs/failure-modes/gpedit-scaffold.md, AUMID is not readable).
/// </summary>
internal static class WindowAppUserModelId
{
    private const ushort VtLpwstr = 31;
    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary>The outcome of a read: the id when the store held one, otherwise why not.</summary>
    internal readonly record struct Result(string? Id, string Detail);

    public static Result Read(nint hwnd)
    {
        Guid iid = PropertyStoreIid;
        int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore? store);
        if (hr < 0 || store is null)
        {
            return new Result(null, $"SHGetPropertyStoreForWindow failed with 0x{hr:X8}.");
        }

        try
        {
            var key = new PropertyKey { FormatId = AppUserModelFormat, PropertyId = 5 };
            hr = store.GetValue(ref key, out PropVariant value);
            if (hr < 0)
            {
                return new Result(null, $"IPropertyStore.GetValue failed with 0x{hr:X8}.");
            }

            try
            {
                if (value.Type != VtLpwstr)
                {
                    return new Result(null, $"The window property store has no AppUserModel.ID (value type {value.Type}).");
                }

                int convert = PropVariantToStringAlloc(ref value, out nint text);
                if (convert < 0 || text == nint.Zero)
                {
                    return new Result(null, $"PropVariantToStringAlloc failed with 0x{convert:X8}.");
                }

                try
                {
                    string? id = Marshal.PtrToStringUni(text);
                    return string.IsNullOrEmpty(id)
                        ? new Result(null, "The window property store holds an empty AppUserModel.ID.")
                        : new Result(id, "Read from the window property store.");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT on a 64-bit process: the type at 0, the value union at 8, 24 bytes in all.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public nint Value;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

#pragma warning disable SYSLIB1054 // One read-only call does not justify LibraryImport plumbing.
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        nint hwnd,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToStringAlloc(ref PropVariant value, out nint text);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
#pragma warning restore SYSLIB1054
}
