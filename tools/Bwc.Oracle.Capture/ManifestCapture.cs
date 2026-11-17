using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Bwc.Oracle.Capture;

// `manifest`: reads the embedded RT_MANIFEST of classic regedit.exe as an image resource (LoadLibraryEx with
// LOAD_LIBRARY_AS_IMAGE_RESOURCE; regedit is never run) and records its requestedExecutionLevel. Tracked output:
// regedit-manifest.json (level, uiAccess, the resource id, and the SHA-256 of the manifest bytes).
internal static class ManifestCapture
{
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const int RtManifest = 24;
    private const string AsmNamespace = "urn:schemas-microsoft-com:asm.v3";

    private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNamesW(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr hResData);

    internal static void Run(RepoPaths paths)
    {
        byte[] manifest;
        int resourceId;
        IntPtr module = LoadLibraryExW(RegeditProcess.RegeditPath, IntPtr.Zero, LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LoadLibraryEx failed for {RegeditProcess.RegeditPath}.");
        }

        try
        {
            var names = new List<IntPtr>();
            if (!EnumResourceNamesW(module, (IntPtr)RtManifest, (h, t, n, l) => { names.Add(n); return true; }, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumResourceNames(RT_MANIFEST) failed.");
            }

            if (names.Count == 0)
            {
                throw new InvalidDataException("regedit.exe has no RT_MANIFEST resource.");
            }

            IntPtr name = names[0];
            resourceId = name.ToInt64() < 0x10000 ? (int)name.ToInt64() : -1;
            IntPtr info = FindResourceW(module, name, (IntPtr)RtManifest);
            if (info == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FindResource(RT_MANIFEST) failed.");
            }

            IntPtr data = LoadResource(module, info);
            uint size = SizeofResource(module, info);
            IntPtr pointer = LockResource(data);
            if (pointer == IntPtr.Zero || size == 0)
            {
                throw new InvalidDataException("the RT_MANIFEST resource is empty.");
            }

            manifest = new byte[size];
            Marshal.Copy(pointer, manifest, 0, (int)size);
        }
        finally
        {
            FreeLibrary(module);
        }

        string xml = Encoding.UTF8.GetString(manifest).TrimStart('﻿');
        XDocument document = XDocument.Parse(xml);
        XElement level = document.Descendants(XName.Get("requestedExecutionLevel", AsmNamespace)).FirstOrDefault()
            ?? throw new InvalidDataException("the manifest has no requestedExecutionLevel element.");

        string levelName = (string?)level.Attribute("level") ?? throw new InvalidDataException("requestedExecutionLevel has no level attribute.");
        string uiAccess = (string?)level.Attribute("uiAccess") ?? "false";
        string digest = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();

        Report.WriteJson(paths.ManifestJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture manifest",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["note"] = "The embedded RT_MANIFEST read as an image resource. regedit.exe was not started.",
            ["manifestResourceId"] = resourceId,
            ["manifestBytes"] = manifest.Length,
            ["manifestSha256"] = digest,
            ["requestedExecutionLevel"] = new JsonObject
            {
                ["level"] = levelName,
                ["uiAccess"] = string.Equals(uiAccess, "true", StringComparison.OrdinalIgnoreCase),
            },
        });
    }
}
