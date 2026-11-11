using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

/// <summary>
/// Creates, inspects, and removes symbolic link keys for the guard tests. This is the only file
/// in the registry test projects that calls native registry write APIs; scripts/check-registry-writer.js
/// allowlists it by name. Paths are relative to HKEY_CURRENT_USER and must lie under
/// HKCU\Software\BWC-E2E\&lt;run-id&gt;. The writer under test never creates links, so this fixture is the
/// only way to build the inputs the guard must refuse.
/// </summary>
internal static partial class RegistryLinkFixture
{
    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);
    private const uint KeyQueryValue = 0x0001;
    private const uint KeyAllAccess = 0x000F003F;
    private const uint KeyWow64_64Key = 0x0100;
    private const uint RegOptionNonVolatile = 0x00000000;
    private const uint RegOptionCreateLink = 0x00000002;
    private const uint RegOptionOpenLink = 0x00000008;
    private const uint RegLinkType = 6;
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;
    private const string LinkValueName = "SymbolicLinkValue";

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint hKey, string lpSubKey, uint ulOptions, uint samDesired, out nint phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyEx(
        nint hKey,
        string lpSubKey,
        uint reserved,
        string? lpClass,
        uint dwOptions,
        uint samDesired,
        nint lpSecurityAttributes,
        out nint phkResult,
        out uint lpdwDisposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueEx(nint hKey, string lpValueName, uint reserved, uint dwType, byte[] lpData, uint cbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(
        nint hKey,
        string lpValueName,
        nint lpReserved,
        out uint lpType,
        nint lpData,
        ref uint lpcbData);

    [LibraryImport("ntdll.dll", EntryPoint = "NtDeleteKey")]
    private static partial int NtDeleteKey(nint keyHandle);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint hKey);

    /// <summary>Creates a symbolic link named <paramref name="name"/> under <paramref name="parentPath"/>.</summary>
    internal static void CreateLink(string parentPath, string name, string targetPath)
    {
        nint parent = OpenKey(parentPath, KeyAllAccess);
        try
        {
            int status = RegCreateKeyEx(
                parent,
                name,
                0,
                null,
                RegOptionCreateLink | RegOptionNonVolatile,
                KeyAllAccess | KeyWow64_64Key,
                nint.Zero,
                out nint link,
                out _);
            Check(status, "create link " + name);

            try
            {
                byte[] target = Encoding.Unicode.GetBytes(NtPath(targetPath));
                Check(
                    RegSetValueEx(link, LinkValueName, 0, RegLinkType, target, (uint)target.Length),
                    "write link target " + name);
            }
            finally
            {
                RegCloseKey(link);
            }
        }
        finally
        {
            RegCloseKey(parent);
        }
    }

    /// <summary>
    /// Returns true when the child is a symbolic link node. The node is opened without following
    /// the link. A missing child returns false.
    /// </summary>
    internal static bool IsLink(string parentPath, string name)
    {
        if (!ChildExists(parentPath, name))
        {
            return false;
        }

        nint parent = OpenKey(parentPath, KeyQueryValue | KeyWow64_64Key);
        try
        {
            int status = RegCreateKeyEx(
                parent,
                name,
                0,
                null,
                RegOptionOpenLink,
                KeyQueryValue | KeyWow64_64Key,
                nint.Zero,
                out nint node,
                out _);
            Check(status, "open node " + name);

            try
            {
                uint size = 0;
                status = RegQueryValueEx(node, LinkValueName, nint.Zero, out uint type, nint.Zero, ref size);
                if (status == ErrorFileNotFound)
                {
                    return false;
                }

                if (status is not ErrorSuccess and not ErrorMoreData)
                {
                    Check(status, "query link value " + name);
                }

                return type == RegLinkType;
            }
            finally
            {
                RegCloseKey(node);
            }
        }
        finally
        {
            RegCloseKey(parent);
        }
    }

    /// <summary>
    /// Removes every symbolic link node below <paramref name="path"/>, then returns. The link node is
    /// opened with REG_OPTION_OPEN_LINK and removed with NtDeleteKey on that handle. RegDeleteKeyEx on
    /// the name fails on a link, so it is not used. Ordinary keys are walked. Cleanup calls this before
    /// deleting a run tree.
    /// </summary>
    internal static void DeleteLinkNodes(string path)
    {
        using RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: false);
        if (key is null)
        {
            return;
        }

        foreach (string child in key.GetSubKeyNames())
        {
            if (IsLink(path, child))
            {
                RemoveLinkNode(path, child);
                continue;
            }

            DeleteLinkNodes(path + @"\" + child);
        }
    }

    private static void RemoveLinkNode(string parentPath, string name)
    {
        nint parent = OpenKey(parentPath, KeyAllAccess);
        try
        {
            int status = RegCreateKeyEx(
                parent,
                name,
                0,
                null,
                RegOptionOpenLink,
                KeyAllAccess | KeyWow64_64Key,
                nint.Zero,
                out nint node,
                out _);
            Check(status, "open link node " + name);

            try
            {
                Check(NtDeleteKey(node), "remove link node " + name);
            }
            finally
            {
                RegCloseKey(node);
            }
        }
        finally
        {
            RegCloseKey(parent);
        }
    }

    private static bool ChildExists(string parentPath, string name)
    {
        using RegistryKey? parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(parentPath, writable: false);
        return parent is not null && parent.GetSubKeyNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static nint OpenKey(string path, uint access)
    {
        int status = RegOpenKeyEx(HkeyCurrentUser, path, 0, access | KeyWow64_64Key, out nint handle);
        Check(status, "open " + path);
        return handle;
    }

    private static string NtPath(string relativePath)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        return $@"\Registry\User\{sid}\{relativePath}";
    }

    private static void Check(int status, string operation)
    {
        if (status != ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"Registry fixture: {operation} failed with status {status} (0x{status:X8}).",
                new Win32Exception(status));
        }
    }
}
