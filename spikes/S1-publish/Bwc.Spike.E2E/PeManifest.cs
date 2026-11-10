using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.Spike.E2E;

/// <summary>Reads the embedded application manifest (RT_MANIFEST, id 1) of a PE file without running it.</summary>
internal static class PeManifest
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private static readonly IntPtr RT_MANIFEST = (IntPtr)24;
    private static readonly IntPtr CREATEPROCESS_MANIFEST_RESOURCE_ID = (IntPtr)1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    public static string ReadXml(string exePath)
    {
        var module = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibraryEx failed for " + exePath);
        try
        {
            var info = FindResource(module, CREATEPROCESS_MANIFEST_RESOURCE_ID, RT_MANIFEST);
            if (info == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "no RT_MANIFEST id 1 in " + exePath);
            var size = SizeofResource(module, info);
            // For LOAD_LIBRARY_AS_DATAFILE, LoadResource returns a pointer straight into the mapped file.
            var data = LoadResource(module, info);
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            // The manifest is UTF-8 with a byte-order mark, which would otherwise stop XML parsing at position 1.
            return Encoding.UTF8.GetString(bytes).TrimStart('﻿', ' ', '\r', '\n', '\t');
        }
        finally
        {
            FreeLibrary(module);
        }
    }
}
