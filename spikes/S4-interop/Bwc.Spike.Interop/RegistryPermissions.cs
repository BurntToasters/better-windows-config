using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;

namespace Bwc.Spike.Interop;

/// <summary>
/// Minimal ISecurityInformation (oleacc/aclui contract) for one registry key, shown through aclui!EditSecurity.
/// SI_READONLY is set and SetSecurity returns E_ACCESSDENIED, so nothing can be written.
/// </summary>
[GeneratedComInterface]
[Guid("965FC360-16FF-11D0-91CB-00AA00BBB723")]
internal unsafe partial interface ISecurityInformation
{
    [PreserveSig] int GetObjectInformation(SI_OBJECT_INFO* pObjectInfo);
    [PreserveSig] int GetSecurity(uint RequestedInformation, nint* ppSecurityDescriptor, int fDefault);
    [PreserveSig] int SetSecurity(uint SecurityInformation, nint pSecurityDescriptor);
    [PreserveSig] int GetAccessRights(Guid* pguidObjectType, uint dwFlags, SI_ACCESS** ppAccess, uint* pcAccesses, uint* piDefaultAccess);
    [PreserveSig] int MapGeneric(Guid* pguidObjectType, byte* pAceFlags, uint* pMask);
    [PreserveSig] int GetInheritTypes(SI_INHERIT_TYPE** ppInheritTypes, uint* pcInheritTypes);
    [PreserveSig] int PropertySheetPageCallback(nint hwnd, uint uMsg, uint uPage);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SI_OBJECT_INFO
{
    public uint dwFlags;
    public nint hInstance;
    public nint pszServerName;
    public nint pszObjectName;
    public nint pszPageTitle;
    public Guid guidObjectType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SI_ACCESS
{
    public nint pguid;
    public uint mask;
    public nint pszName;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SI_INHERIT_TYPE
{
    public nint pguid;
    public uint dwFlags;
    public nint pszName;
}

[GeneratedComClass]
internal sealed unsafe partial class RegistryKeySecurityInformation : ISecurityInformation, IDisposable
{
    // aclui.h
    internal const uint SI_EDIT_ALL = 0x00000003;   // SI_EDIT_OWNER | SI_EDIT_AUDITS (SI_EDIT_PERMS is 0)
    internal const uint SI_ADVANCED = 0x00000010;
    internal const uint SI_READONLY = 0x00000008;
    internal const uint SI_ACCESS_SPECIFIC = 0x00010000;
    internal const uint SI_ACCESS_GENERAL = 0x00020000;
    internal const uint SI_ACCESS_CONTAINER = 0x00040000;

    // winnt.h registry access masks and generic bits
    private const uint KEY_QUERY_VALUE = 0x0001;
    private const uint KEY_SET_VALUE = 0x0002;
    private const uint KEY_CREATE_SUB_KEY = 0x0004;
    private const uint KEY_ENUMERATE_SUB_KEYS = 0x0008;
    private const uint KEY_NOTIFY = 0x0010;
    private const uint KEY_CREATE_LINK = 0x0020;
    private const uint KEY_READ = 0x00020019;
    private const uint KEY_WRITE = 0x00020006;
    private const uint KEY_EXECUTE = 0x00020019;
    private const uint KEY_ALL_ACCESS = 0x000F003F;
    private const uint DELETE = 0x00010000;
    private const uint READ_CONTROL = 0x00020000;
    private const uint WRITE_DAC = 0x00040000;
    private const uint WRITE_OWNER = 0x00080000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_EXECUTE = 0x20000000;
    private const uint GENERIC_ALL = 0x10000000;
    private const uint SACL_SECURITY_INFORMATION_MASK = Win32.SACL_SECURITY_INFORMATION;

    private static readonly (string Name, uint Mask, uint Flags)[] AccessRights =
    {
        ("Full Control", KEY_ALL_ACCESS, SI_ACCESS_GENERAL | SI_ACCESS_CONTAINER),
        ("Read", KEY_READ, SI_ACCESS_GENERAL | SI_ACCESS_CONTAINER),
        ("Write", KEY_WRITE, SI_ACCESS_GENERAL | SI_ACCESS_CONTAINER),
        ("Query Value", KEY_QUERY_VALUE, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Set Value", KEY_SET_VALUE, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Create Subkey", KEY_CREATE_SUB_KEY, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Enumerate Subkeys", KEY_ENUMERATE_SUB_KEYS, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Notify", KEY_NOTIFY, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Create Link", KEY_CREATE_LINK, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Delete", DELETE, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Read Control", READ_CONTROL, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Write DAC", WRITE_DAC, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Write Owner", WRITE_OWNER, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
    };

    private const int DefaultAccessIndex = 1; // "Read"

    private readonly nint _hKey;
    private readonly string _objectName;
    private nint _objectNamePtr;
    private nint _accessArray;
    private bool _disposed;

    public RegistryKeySecurityInformation(nint hKey, string objectName)
    {
        _hKey = hKey;
        _objectName = objectName;
    }

    public int GetObjectInformation(SI_OBJECT_INFO* pObjectInfo)
    {
        if (pObjectInfo is null) return Win32.E_POINTER;
        if (_objectNamePtr == 0) _objectNamePtr = Marshal.StringToCoTaskMemUni(_objectName);
        *pObjectInfo = new SI_OBJECT_INFO
        {
            dwFlags = SI_EDIT_ALL | SI_ADVANCED | SI_READONLY,
            hInstance = 0,
            pszServerName = 0,
            pszObjectName = _objectNamePtr,
            pszPageTitle = 0,
            guidObjectType = Guid.Empty,
        };
        return Win32.S_OK;
    }

    public int GetSecurity(uint requestedInformation, nint* ppSecurityDescriptor, int fDefault)
    {
        if (ppSecurityDescriptor is null) return Win32.E_POINTER;
        *ppSecurityDescriptor = 0;
        var requested = requestedInformation;
        var rc = QuerySecurity(requested, out var sd);
        if (rc == Win32.ERROR_PRIVILEGE_NOT_HELD && (requested & SACL_SECURITY_INFORMATION_MASK) != 0)
        {
            // Auditing (SACL) needs SeSecurityPrivilege; without it return the owner/DACL only.
            requested &= ~SACL_SECURITY_INFORMATION_MASK;
            rc = QuerySecurity(requested, out sd);
        }
        if (rc != Win32.ERROR_SUCCESS)
        {
            return HResultFromWin32(rc);
        }
        // Ownership: aclui releases the descriptor it receives (LocalFree). It is LocalAlloc'd and never freed here;
        // freeing it in Dispose caused a double free and a heap-corruption exit (see README).
        *ppSecurityDescriptor = sd;
        return Win32.S_OK;
    }

    /// <summary>The dialog is read-only; this must never write the key.</summary>
    public int SetSecurity(uint securityInformation, nint pSecurityDescriptor)
    {
        return Win32.E_ACCESSDENIED;
    }

    public int GetAccessRights(Guid* pguidObjectType, uint dwFlags, SI_ACCESS** ppAccess, uint* pcAccesses, uint* piDefaultAccess)
    {
        if (ppAccess is null || pcAccesses is null || piDefaultAccess is null) return Win32.E_POINTER;
        if (_accessArray == 0)
        {
            var count = AccessRights.Length;
            _accessArray = Marshal.AllocCoTaskMem(count * sizeof(SI_ACCESS));
            var entries = (SI_ACCESS*)_accessArray;
            for (var i = 0; i < count; i++)
            {
                entries[i] = new SI_ACCESS
                {
                    pguid = 0, // no object-type GUID for registry keys
                    mask = AccessRights[i].Mask,
                    pszName = Marshal.StringToCoTaskMemUni(AccessRights[i].Name),
                    dwFlags = AccessRights[i].Flags,
                };
            }
        }
        *ppAccess = (SI_ACCESS*)_accessArray;
        *pcAccesses = (uint)AccessRights.Length;
        *piDefaultAccess = DefaultAccessIndex;
        return Win32.S_OK;
    }

    /// <summary>KEY generic mapping: GENERIC_READ/WRITE/EXECUTE/ALL to KEY_* rights.</summary>
    public int MapGeneric(Guid* pguidObjectType, byte* pAceFlags, uint* pMask)
    {
        if (pMask is null) return Win32.E_POINTER;
        var mask = *pMask;
        if ((mask & GENERIC_READ) != 0) mask |= KEY_READ;
        if ((mask & GENERIC_WRITE) != 0) mask |= KEY_WRITE;
        if ((mask & GENERIC_EXECUTE) != 0) mask |= KEY_EXECUTE;
        if ((mask & GENERIC_ALL) != 0) mask |= KEY_ALL_ACCESS;
        mask &= ~(GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE | GENERIC_ALL);
        *pMask = mask;
        return Win32.S_OK;
    }

    /// <summary>Keys do not inherit by object type here, so no inherit types are offered.</summary>
    public int GetInheritTypes(SI_INHERIT_TYPE** ppInheritTypes, uint* pcInheritTypes)
    {
        if (ppInheritTypes is not null) *ppInheritTypes = null;
        if (pcInheritTypes is not null) *pcInheritTypes = 0;
        return Win32.E_NOTIMPL;
    }

    public int PropertySheetPageCallback(nint hwnd, uint uMsg, uint uPage)
    {
        return Win32.S_OK;
    }

    private uint QuerySecurity(uint requested, out nint securityDescriptor)
    {
        securityDescriptor = 0;
        uint size = 0;
        var rc = Win32.RegGetKeySecurity(_hKey, requested, 0, &size);
        if (rc != Win32.ERROR_INSUFFICIENT_BUFFER && rc != Win32.ERROR_SUCCESS)
        {
            return rc;
        }
        var buffer = Win32.LocalAlloc(0 /* LMEM_FIXED */, Math.Max(size, 1u));
        if (buffer == 0)
        {
            return Win32.ERROR_NOT_ENOUGH_MEMORY;
        }
        rc = Win32.RegGetKeySecurity(_hKey, requested, buffer, &size);
        if (rc != Win32.ERROR_SUCCESS)
        {
            Win32.LocalFree(buffer);
            return rc;
        }
        securityDescriptor = buffer;
        return Win32.ERROR_SUCCESS;
    }

    private static int HResultFromWin32(uint win32)
    {
        return win32 == 0 ? 0 : unchecked((int)(0x80070000u | (win32 & 0xFFFF)));
    }

    /// <summary>Frees native buffers. Call after EditSecurity returns; aclui does not free them.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_objectNamePtr != 0) Marshal.FreeCoTaskMem(_objectNamePtr);
        if (_accessArray != 0)
        {
            var entries = (SI_ACCESS*)_accessArray;
            for (var i = 0; i < AccessRights.Length; i++)
            {
                Marshal.FreeCoTaskMem(entries[i].pszName);
            }
            Marshal.FreeCoTaskMem(_accessArray);
        }
        _objectNamePtr = 0;
        _accessArray = 0;
    }
}

/// <summary>Opens the aclui security editor for HKCU\Software\BWC-Spike-S4 (read-only).</summary>
internal static class RegistryPermissionsDialog
{
    public const string KeyPath = @"Software\BWC-Spike-S4";

    public static void EnsureKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
    }

    /// <summary>Returns the HRESULT from EditSecurity (S_OK or S_FALSE once the dialog is closed).</summary>
    public static int Show(nint ownerHwnd)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false)
            ?? throw new InvalidOperationException("HKCU\\" + KeyPath + " is missing.");
        using var info = new RegistryKeySecurityInformation(key.Handle.DangerousGetHandle(), "BWC-Spike-S4");
        return Win32.EditSecurity(ownerHwnd, info);
    }
}
