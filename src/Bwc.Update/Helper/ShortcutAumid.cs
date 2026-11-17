using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bwc.Update;

/// <summary> Writes <c>System.AppUserModel.ID</c> (PKEY_AppUserModel_ID) into a Start menu shortcut through
/// <c>IPropertyStore</c>, then loads the file again and compares. Typed vtable calls, like <see cref="ShellChain"/>.
/// The caller has already checked the path (<see cref="ShortcutAumidRules"/>). Nothing here starts a process, reads an
/// environment variable, or writes a log (update-elevation.md SA, HN11).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ShortcutAumid
{
    private const ushort VtLpwstr = 31;
    private const uint StgmRead = 0;
    private const uint StgmReadWrite = 2;
    private const uint PropertyId = 5;

    // PKEY_AppUserModel_ID is { 9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3 }, 5.
    private static readonly Guid AppIdFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary>
    /// Success, or <see cref="UpdateFailure.Io"/> with the step that failed. The shortcut is written in place.
    /// </summary>
    public static Outcome<string> Write(string shortcut, string appId)
    {
        using var apartment = ComApartment.EnterSta();
        if (!apartment.Usable)
        {
            return Outcome<string>.Fail(UpdateFailure.Io, "COM could not be initialized");
        }

        (ComObject? file, ComObject? store, string? error) = OpenLink();
        if (file is null || store is null)
        {
            return Outcome<string>.Fail(UpdateFailure.Io, error ?? "the shell link object could not be created");
        }

        using (file)
        using (store)
        {
            int hr = Load(file, shortcut, StgmReadWrite);
            if (hr != 0)
            {
                return Outcome<string>.Fail(UpdateFailure.Io, "the shortcut could not be loaded, HRESULT 0x" + Hex(hr));
            }

            hr = SetAppId(file, store, shortcut, appId);
            if (hr != 0)
            {
                return Outcome<string>.Fail(UpdateFailure.Io, "the property could not be written, HRESULT 0x" + Hex(hr));
            }
        }

        var readBack = ReadAppId(shortcut);
        return string.Equals(readBack, appId, StringComparison.Ordinal)
            ? Outcome<string>.Success(appId)
            : Outcome<string>.Fail(UpdateFailure.Io, "the shortcut reads back '" + (readBack ?? "(nothing)") + "' instead of the app id");
    }

    /// <summary>The app id of the shortcut, read through a new link object. Null when it has none or is
    /// unreadable.</summary>
    public static string? ReadAppId(string shortcut)
    {
        using var apartment = ComApartment.EnterSta();
        if (!apartment.Usable)
        {
            return null;
        }

        (ComObject? file, ComObject? store, _) = OpenLink();
        if (file is null || store is null)
        {
            return null;
        }

        using (file)
        using (store)
        {
            if (Load(file, shortcut, StgmRead) != 0)
            {
                return null;
            }

            var key = new PropertyKey { Format = AppIdFormat, Id = PropertyId };
            ComVariant read = default;
            if (((delegate* unmanaged<nint, PropertyKey*, ComVariant*, int>)ComObject.Slot(store.Pointer, ComSlots.PropertyStoreGetValue))(
                store.Pointer, &key, &read) != 0)
            {
                return null;
            }

            try
            {
                return read.Vt == VtLpwstr && read.Value != 0 ? Marshal.PtrToStringUni(read.Value) : null;
            }
            finally
            {
                _ = ComNative.PropVariantClear(&read);
            }
        }
    }

    // A new shell link object seen through IPersistFile and through IPropertyStore (two owners of two references).
    private static (ComObject? File, ComObject? Store, string? Error) OpenLink()
    {
        int hr = ComNative.CoCreateInstance(ComIds.ShellLinkClass, 0, ComNative.ClsctxInprocServer, ComIds.PersistFile, out nint pointer);
        ComObject? file = ComObject.Own(pointer);
        if (hr != 0 || file is null)
        {
            file?.Dispose();
            return (null, null, "the shell link object could not be created");
        }

        hr = file.QueryInterface(ComIds.PropertyStore, out ComObject? store);
        if (hr != 0 || store is null)
        {
            store?.Dispose();
            file.Dispose();
            return (null, null, "the shell link object lacks the property interfaces");
        }

        return (file, store, null);
    }

    private static int Load(ComObject file, string shortcut, uint mode)
    {
        fixed (char* path = shortcut)
        {
            return ((delegate* unmanaged<nint, char*, uint, int>)ComObject.Slot(file.Pointer, ComSlots.PersistFileLoad))(file.Pointer, path, mode);
        }
    }

    // SetValue, Commit, and Save. The string for the value is COM task memory that is freed after the call (the store
    // copies it), and the result is the first HRESULT that is not S_OK.
    private static int SetAppId(ComObject file, ComObject store, string shortcut, string appId)
    {
        var key = new PropertyKey { Format = AppIdFormat, Id = PropertyId };
        var value = new ComVariant { Vt = VtLpwstr, Value = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            int hr = ((delegate* unmanaged<nint, PropertyKey*, ComVariant*, int>)ComObject.Slot(store.Pointer, ComSlots.PropertyStoreSetValue))(
                store.Pointer, &key, &value);
            if (hr == 0)
            {
                hr = ((delegate* unmanaged<nint, int>)ComObject.Slot(store.Pointer, ComSlots.PropertyStoreCommit))(store.Pointer);
            }

            if (hr == 0)
            {
                fixed (char* path = shortcut)
                {
                    hr = ((delegate* unmanaged<nint, char*, int, int>)ComObject.Slot(file.Pointer, ComSlots.PersistFileSave))(file.Pointer, path, 1);
                }
            }

            return hr;
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Value);
        }
    }

    private static string Hex(int hr) => hr.ToString("X8", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }
}
