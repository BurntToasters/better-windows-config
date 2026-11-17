using System.Runtime.InteropServices;

namespace Bwc.Update;

// The COM layer of the helper (update-elevation.md HN8 to HN12). NativeAOT has no classic COM interop
// ([ComImport], late-bound InvokeMember), so every call is a typed call through the object's vtable, and every
// interface pointer has one owner (ComObject) that releases it. Nothing here looks a name up at run time.

/// <summary>Interface and class ids, written from the Windows SDK headers.</summary>
internal static class ComIds
{
    public static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");
    public static readonly Guid ShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    public static readonly Guid ServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    public static readonly Guid ShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid ShellFolderViewDual = new("E7A1AF80-4D96-11CF-960C-0080C7F4EE85");
    public static readonly Guid ShellDispatch2 = new("A4C6892C-3BA9-11D2-9DEA-00C04FB16162");
    public static readonly Guid PersistFile = new("0000010B-0000-0000-C000-000000000046");
    public static readonly Guid PropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>SID_STopLevelBrowser, the service id of the shell's top level browser.</summary>
    public static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    public static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
}

/// <summary>Vtable slots (the index in the interface's table, IUnknown first), counted from the SDK headers.</summary>
internal static class ComSlots
{
    public const int QueryInterface = 0;
    public const int Release = 2;

    // IServiceProvider
    public const int ServiceProviderQueryService = 3;

    // IShellWindows: IUnknown (3), IDispatch (4), then Count, Item, NewEnum, Register, RegisterPending, Revoke,
    // OnNavigate, OnActivated, and FindWindowSW.
    public const int ShellWindowsFindWindowSW = 15;

    // IShellBrowser: IUnknown (3), IOleWindow (2), and ten more before QueryActiveShellView.
    public const int ShellBrowserQueryActiveShellView = 15;

    // IShellView: IUnknown (3), IOleWindow (2), and ten more before GetItemObject.
    public const int ShellViewGetItemObject = 15;

    // IShellFolderViewDual: IUnknown (3), IDispatch (4), then get_Application.
    public const int FolderViewDualGetApplication = 7;

    // IShellDispatch2: IShellDispatch has 23 methods after IDispatch, then IsRestricted (30) and ShellExecute.
    public const int ShellDispatch2ShellExecute = 31;

    // IPersistFile: IUnknown (3), GetClassID, IsDirty, Load, Save.
    public const int PersistFileLoad = 5;
    public const int PersistFileSave = 6;

    // IPropertyStore: IUnknown (3), GetCount, GetAt, GetValue, SetValue, Commit.
    public const int PropertyStoreGetValue = 5;
    public const int PropertyStoreSetValue = 6;
    public const int PropertyStoreCommit = 7;

    // IShellLinkW: used by the tests to build a shortcut.
    public const int ShellLinkSetPath = 20;
}

/// <summary>VARIANT and PROPVARIANT: a type, three reserved words, and a 16-byte union (24 bytes on 64-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Value2;
}

internal static partial class ComNative
{
    public const uint ClsctxInprocServer = 0x1;
    public const uint ClsctxAll = 0x17;

    /// <summary>The thread is already in another apartment mode. COM works, but the caller must not uninitialize
    /// it.</summary>
    public const int RpcEChangedMode = unchecked((int)0x80010106);

    public const uint CoinitApartmentThreaded = 0x2;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    [LibraryImport("ole32.dll")]
    public static unsafe partial int PropVariantClear(ComVariant* value);
}

/// <summary>
/// COM initialization for the calling thread, single-threaded apartment. It is undone only when this call did the
/// initializing (S_OK or S_FALSE). A thread that is already in another mode still works, so it counts as usable.
/// </summary>
internal readonly struct ComApartment : IDisposable
{
    private readonly bool _uninitialize;

    private ComApartment(bool uninitialize, bool usable)
    {
        _uninitialize = uninitialize;
        Usable = usable;
    }

    public bool Usable { get; }

    public static ComApartment EnterSta()
    {
        int hr = ComNative.CoInitializeEx(0, ComNative.CoinitApartmentThreaded);
        return new ComApartment(hr >= 0, hr >= 0 || hr == ComNative.RpcEChangedMode);
    }

    public void Dispose()
    {
        if (_uninitialize)
        {
            ComNative.CoUninitialize();
        }
    }
}

/// <summary> Owns one interface pointer and releases it once, when it is disposed. The pointer is never copied out for
/// longer than a call, so there is exactly one release per reference that was acquired.
/// </summary>
internal sealed unsafe class ComObject : IDisposable
{
    private const int EPointer = unchecked((int)0x80004003);
    private const int ENoInterface = unchecked((int)0x80004002);

    private nint _pointer;

    private ComObject(nint pointer)
    {
        _pointer = pointer;
    }

    /// <summary>The pointer, or 0 after it was released.</summary>
    public nint Pointer => Volatile.Read(ref _pointer);

    /// <summary>Takes over one reference. Null for a null pointer (nothing to release).</summary>
    public static ComObject? Own(nint pointer) => pointer == 0 ? null : new ComObject(pointer);

    /// <summary>The function pointer at <paramref name="index"/> in the vtable of <paramref name="self"/>.</summary>
    public static void* Slot(nint self, int index) => ((void**)*(void***)self)[index];

    /// <summary> IUnknown::QueryInterface. On success <paramref name="result"/> owns its own reference. On any failure
    /// it is null: a pointer that a failing call left behind is released here.
    /// </summary>
    public int QueryInterface(in Guid iid, out ComObject? result)
    {
        result = null;
        nint self = Pointer;
        if (self == 0)
        {
            return EPointer;
        }

        nint found = 0;
        int hr;
        fixed (Guid* id = &iid)
        {
            hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(self, ComSlots.QueryInterface))(self, id, &found);
        }

        ComObject? owned = Own(found);
        if (hr != 0 || owned is null)
        {
            owned?.Dispose();
            return hr != 0 ? hr : ENoInterface;
        }

        result = owned;
        return 0;
    }

    public void Dispose()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(pointer, ComSlots.Release))(pointer);
        }
    }
}
