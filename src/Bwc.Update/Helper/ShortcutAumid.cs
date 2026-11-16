using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bwc.Update;

/// <summary>
/// Writes <c>System.AppUserModel.ID</c> (PKEY_AppUserModel_ID) into a Start menu shortcut through
/// <c>IPropertyStore</c>, then
/// loads the file again and compares. Manual COM, like <see cref="ShellLaunch"/>. The caller has already checked the path
/// (<see cref="ShortcutAumidRules"/>). Nothing here starts a process, reads an environment variable, or writes a log.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShortcutAumid
{
    private const ushort VtLpwstr = 31;
    private const uint StgmRead = 0;
    private const uint StgmReadWrite = 2;
    private const int PropertyId = 5;

    // PKEY_AppUserModel_ID is { 9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3 }, 5.
    private static readonly Guid AppIdFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>
    /// Success, or <see cref="UpdateFailure.Io"/> with the step that failed. The shortcut is written in place.
    /// </summary>
    public static Outcome<string> Write(string shortcut, string appId)
    {
        var releases = new List<object>();
        try
        {
            var linkType = Type.GetTypeFromCLSID(ClsidShellLink);
            if (linkType is null || Activator.CreateInstance(linkType) is not { } link)
            {
                return Outcome<string>.Fail(UpdateFailure.Io, "the shell link object could not be created");
            }

            releases.Add(link);
            if (link is not IPersistFile file || link is not IPropertyStore store)
            {
                return Outcome<string>.Fail(UpdateFailure.Io, "the shell link object lacks the property interfaces");
            }

            var hr = file.Load(shortcut, StgmReadWrite);
            if (hr != 0)
            {
                return Outcome<string>.Fail(UpdateFailure.Io, "the shortcut could not be loaded, HRESULT 0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }

            var key = new PropertyKey { Format = AppIdFormat, Id = PropertyId };
            var value = new PropVariant { Type = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                hr = store.SetValue(ref key, ref value);
                if (hr == 0)
                {
                    hr = store.Commit();
                }

                if (hr == 0)
                {
                    hr = file.Save(shortcut, true);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }

            if (hr != 0)
            {
                return Outcome<string>.Fail(UpdateFailure.Io, "the property could not be written, HRESULT 0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }

            var readBack = ReadBack(shortcut, releases);
            return string.Equals(readBack, appId, StringComparison.Ordinal)
                ? Outcome<string>.Success(appId)
                : Outcome<string>.Fail(UpdateFailure.Io, "the shortcut reads back '" + (readBack ?? "(nothing)") + "' instead of the app id");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or NotSupportedException
            or ArgumentException or UnauthorizedAccessException or IOException)
        {
            return Outcome<string>.Fail(UpdateFailure.Io, "the shell call failed: " + ex.GetType().Name);
        }
        finally
        {
            foreach (var released in releases)
            {
                if (Marshal.IsComObject(released))
                {
                    _ = Marshal.ReleaseComObject(released);
                }
            }
        }
    }

    private static string? ReadBack(string shortcut, List<object> releases)
    {
        var linkType = Type.GetTypeFromCLSID(ClsidShellLink);
        if (linkType is null || Activator.CreateInstance(linkType) is not { } link)
        {
            return null;
        }

        releases.Add(link);
        if (link is not IPersistFile file || link is not IPropertyStore store || file.Load(shortcut, StgmRead) != 0)
        {
            return null;
        }

        var key = new PropertyKey { Format = AppIdFormat, Id = PropertyId };
        if (store.GetValue(ref key, out var read) != 0)
        {
            return null;
        }

        try
        {
            return read.Type == VtLpwstr && read.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(read.Pointer) : null;
        }
        finally
        {
            _ = PropVariantClear(ref read);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    // PROPVARIANT: vt, three reserved words, and a 16-byte union (24 bytes on 64-bit Windows).
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Reserved4;
    }

#pragma warning disable SYSLIB1096 // Classic COM interop, as in ShellLaunch.
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

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        // IPersist.GetClassID, then the IPersistFile members.
        [PreserveSig]
        int GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [PreserveSig]
        int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
#pragma warning restore SYSLIB1096

#pragma warning disable SYSLIB1054 // One read-only call does not justify LibraryImport plumbing.
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
#pragma warning restore SYSLIB1054
}
