# Writes System.AppUserModel.ID into a Start menu shortcut, then reads it back to confirm.
#
# Called by installer/include/bwc-common.nsh after CreateShortCut. Exit codes:
#   0  the shortcut carries the requested AppUserModelID
#   4  the property could not be written or did not read back (the installer logs a warning)

param(
    [Parameter(Mandatory = $true)][string]$ShortcutPath,
    [Parameter(Mandatory = $true)][string]$AppUserModelId
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace BwcShortcutId
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr value;
        public IntPtr reserved4;
    }

    // Only GetValue and SetValue are called. The earlier slots are declared so the vtable lines up.
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLink
    {
    }

    public static class Writer
    {
        private const ushort VT_LPWSTR = 31;
        private const uint STGM_READ = 0;
        private const uint STGM_READWRITE = 2;
        // PKEY_AppUserModel_ID
        private static readonly Guid AppIdKey = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

        public static string Apply(string shortcutPath, string appId)
        {
            object link = new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, STGM_READWRITE);
            var store = (IPropertyStore)link;
            var key = new PropertyKey { fmtid = AppIdKey, pid = 5 };
            var value = new PropVariant { vt = VT_LPWSTR, value = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
                ((IPersistFile)link).Save(shortcutPath, true);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.value);
            }

            object check = new ShellLink();
            ((IPersistFile)check).Load(shortcutPath, STGM_READ);
            var readStore = (IPropertyStore)check;
            PropVariant read;
            readStore.GetValue(ref key, out read);
            string text = read.vt == VT_LPWSTR && read.value != IntPtr.Zero
                ? Marshal.PtrToStringUni(read.value)
                : null;
            return text;
        }
    }
}
'@
}
catch {
    [Console]::Error.WriteLine("bwc-set-aumid: could not load the shell helper: $($_.Exception.Message)")
    exit 4
}

try {
    $readBack = [BwcShortcutId.Writer]::Apply((Resolve-Path -LiteralPath $ShortcutPath).Path, $AppUserModelId)
}
catch {
    [Console]::Error.WriteLine("bwc-set-aumid: could not write the property: $($_.Exception.Message)")
    exit 4
}

if ($readBack -ne $AppUserModelId) {
    [Console]::Error.WriteLine("bwc-set-aumid: read back '$readBack' instead of '$AppUserModelId'")
    exit 4
}

Write-Output "bwc-set-aumid: $AppUserModelId set on $ShortcutPath"
exit 0
