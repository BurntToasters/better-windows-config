using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bwc.Oracle.Resources.Native;

/// A resource type, name or language identifier: either a numeric MAKEINTRESOURCE id or a Unicode string.
internal readonly record struct ResourceId(int? Number, string? Name)
{
    public static ResourceId FromPointer(IntPtr pointer)
    {
        ulong raw = unchecked((ulong)pointer.ToInt64());
        if (raw <= 0xFFFF)
        {
            return new ResourceId((int)raw, null);
        }
        return new ResourceId(null, Marshal.PtrToStringUni(pointer) ?? string.Empty);
    }

    /// Numeric ids first (ascending), then names (ordinal).
    public static int Compare(ResourceId left, ResourceId right)
    {
        if (left.Number is int l)
        {
            return right.Number is int r ? l.CompareTo(r) : -1;
        }
        if (right.Number is int)
        {
            return 1;
        }
        return string.CompareOrdinal(left.Name, right.Name);
    }
}

/// Owns a native pointer for a resource type or name (MAKEINTRESOURCE value or a temporary wide string).
internal sealed class NativeResourceId : IDisposable
{
    private IntPtr _allocated;

    public NativeResourceId(ResourceId id)
    {
        if (id.Number is int number)
        {
            Pointer = new IntPtr(number);
        }
        else
        {
            _allocated = Marshal.StringToHGlobalUni(id.Name ?? string.Empty);
            Pointer = _allocated;
        }
    }

    public IntPtr Pointer { get; }

    public void Dispose()
    {
        if (_allocated != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_allocated);
            _allocated = IntPtr.Zero;
        }
    }
}

/// One (type, name, language) resource. Data is an owned copy; LoadError is set when the bytes could not be read.
internal sealed record ResourceEntry(ResourceId Type, ResourceId Name, ushort Language, uint ByteLength, byte[] Data, string? LoadError);

/// Loads a module as a data file and enumerates every resource through the documented resource APIs.
internal static class ResourceModule
{
    private const uint LoadFlags = Win32.LOAD_LIBRARY_AS_DATAFILE | Win32.LOAD_LIBRARY_AS_IMAGE_RESOURCE;
    private const uint MaxResourceBytes = 16 * 1024 * 1024;

    public static IntPtr Load(string path)
    {
        IntPtr handle = Win32.LoadLibraryEx(path, IntPtr.Zero, LoadFlags);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"LoadLibraryEx failed for '{path}'.");
        }
        return handle;
    }

    public static void Unload(IntPtr handle)
    {
        Win32.FreeLibrary(handle);
    }

    public static List<ResourceEntry> EnumerateAll(IntPtr module)
    {
        var entries = new List<ResourceEntry>();
        foreach (ResourceId type in EnumerateTypes(module))
        {
            foreach (ResourceId name in EnumerateNames(module, type))
            {
                foreach (ushort language in EnumerateLanguages(module, type, name))
                {
                    entries.Add(LoadEntry(module, type, name, language));
                }
            }
        }
        return entries;
    }

    private static List<ResourceId> EnumerateTypes(IntPtr module)
    {
        var found = new List<ResourceId>();
        EnumResTypeProc callback = (_, type, _) =>
        {
            found.Add(ResourceId.FromPointer(type));
            return true;
        };
        bool ok = Win32.EnumResourceTypes(module, callback, IntPtr.Zero);
        int error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);
        ThrowIfFailed(ok, error, "EnumResourceTypes");
        return found;
    }

    private static List<ResourceId> EnumerateNames(IntPtr module, ResourceId type)
    {
        var found = new List<ResourceId>();
        using var nativeType = new NativeResourceId(type);
        EnumResNameProc callback = (_, _, name, _) =>
        {
            found.Add(ResourceId.FromPointer(name));
            return true;
        };
        bool ok = Win32.EnumResourceNames(module, nativeType.Pointer, callback, IntPtr.Zero);
        int error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);
        ThrowIfFailed(ok, error, "EnumResourceNames");
        return found;
    }

    private static List<ushort> EnumerateLanguages(IntPtr module, ResourceId type, ResourceId name)
    {
        var found = new List<ushort>();
        using var nativeType = new NativeResourceId(type);
        using var nativeName = new NativeResourceId(name);
        EnumResLangProc callback = (_, _, _, language, _) =>
        {
            found.Add(language);
            return true;
        };
        bool ok = Win32.EnumResourceLanguages(module, nativeType.Pointer, nativeName.Pointer, callback, IntPtr.Zero);
        int error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);
        ThrowIfFailed(ok, error, "EnumResourceLanguages");
        return found;
    }

    private static ResourceEntry LoadEntry(IntPtr module, ResourceId type, ResourceId name, ushort language)
    {
        using var nativeType = new NativeResourceId(type);
        using var nativeName = new NativeResourceId(name);

        IntPtr info = Win32.FindResourceEx(module, nativeType.Pointer, nativeName.Pointer, language);
        if (info == IntPtr.Zero)
        {
            return Failed(type, name, language, 0, $"FindResourceEx failed (Win32 error {Marshal.GetLastPInvokeError()})");
        }

        uint size = Win32.SizeofResource(module, info);
        if (size == 0)
        {
            return new ResourceEntry(type, name, language, 0, Array.Empty<byte>(), null);
        }
        if (size > MaxResourceBytes)
        {
            return Failed(type, name, language, size, $"resource is {size} bytes, above the {MaxResourceBytes} byte limit");
        }

        IntPtr global = Win32.LoadResource(module, info);
        IntPtr pointer = global == IntPtr.Zero ? IntPtr.Zero : Win32.LockResource(global);
        if (pointer == IntPtr.Zero)
        {
            return Failed(type, name, language, size, $"LoadResource/LockResource failed (Win32 error {Marshal.GetLastPInvokeError()})");
        }

        var data = new byte[size];
        Marshal.Copy(pointer, data, 0, (int)size);
        return new ResourceEntry(type, name, language, size, data, null);
    }

    private static ResourceEntry Failed(ResourceId type, ResourceId name, ushort language, uint size, string error) =>
        new(type, name, language, size, Array.Empty<byte>(), error);

    private static void ThrowIfFailed(bool ok, int error, string api)
    {
        if (!ok && error != 0)
        {
            throw new Win32Exception(error, $"{api} failed.");
        }
    }
}
