using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Bwc.Registry;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// The aclui <c>ISecurityInformation</c> contract for one registry key. aclui owns the security
/// descriptor returned by <see cref="RegistryKeySecurityInformation.GetSecurity"/> and releases it
/// with <c>LocalFree</c>; this class never frees that buffer.
/// </summary>
[GeneratedComInterface]
[Guid("965FC360-16FF-11D0-91CB-00AA00BBB723")]
internal unsafe partial interface ISecurityInformation
{
    [PreserveSig]
    int GetObjectInformation(SI_OBJECT_INFO* pObjectInfo);

    [PreserveSig]
    int GetSecurity(uint requestedInformation, nint* ppSecurityDescriptor, int fDefault);

    [PreserveSig]
    int SetSecurity(uint securityInformation, nint pSecurityDescriptor);

    [PreserveSig]
    int GetAccessRights(
        Guid* pguidObjectType,
        uint dwFlags,
        SI_ACCESS** ppAccess,
        uint* pcAccesses,
        uint* piDefaultAccess);

    [PreserveSig]
    int MapGeneric(Guid* pguidObjectType, byte* pAceFlags, uint* pMask);

    [PreserveSig]
    int GetInheritTypes(SI_INHERIT_TYPE** ppInheritTypes, uint* pcInheritTypes);

    [PreserveSig]
    int PropertySheetPageCallback(nint hwnd, uint uMsg, uint uPage);
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

/// <summary>
/// Writable aclui callback object for one registry key. Security descriptor buffers returned to
/// aclui are allocated with <c>LocalAlloc</c> and are intentionally not freed here.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class RegistryKeySecurityInformation : ISecurityInformation, IDisposable
{
    // aclui.h
    private const uint SI_EDIT_ALL = 0x00000003;
    private const uint SI_ADVANCED = 0x00000010;
    private const uint SI_ACCESS_SPECIFIC = 0x00010000;
    private const uint SI_ACCESS_GENERAL = 0x00020000;
    private const uint SI_ACCESS_CONTAINER = 0x00040000;

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

    private const int S_OK = 0;
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int ErrorPrivilegeNotHeld = 1314;

    private const int DefaultAccessIndex = 1; // "Read"

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

    private readonly RegistryReader _reader;
    private readonly RegistryWriter _writer;
    private readonly RegistryPath _path;
    private readonly string _objectName;
    private nint _objectNamePtr;
    private nint _accessArray;
    private bool _disposed;

    public RegistryKeySecurityInformation(
        RegistryReader reader,
        RegistryWriter writer,
        RegistryPath path,
        string objectName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(objectName);

        _reader = reader;
        _writer = writer;
        _path = path;
        _objectName = objectName;
    }

    public int GetObjectInformation(SI_OBJECT_INFO* pObjectInfo)
    {
        if (pObjectInfo is null)
        {
            return E_POINTER;
        }

        if (_objectNamePtr == 0)
        {
            _objectNamePtr = Marshal.StringToCoTaskMemUni(_objectName);
        }

        *pObjectInfo = new SI_OBJECT_INFO
        {
            dwFlags = SI_EDIT_ALL | SI_ADVANCED,
            hInstance = 0,
            pszServerName = 0,
            pszObjectName = _objectNamePtr,
            pszPageTitle = 0,
            guidObjectType = Guid.Empty,
        };

        return S_OK;
    }

    public int GetSecurity(uint requestedInformation, nint* ppSecurityDescriptor, int fDefault)
    {
        _ = fDefault;

        if (ppSecurityDescriptor is null)
        {
            return E_POINTER;
        }

        *ppSecurityDescriptor = 0;

        try
        {
            byte[] descriptor = ReadDescriptor((RegistrySecurityInformation)requestedInformation);
            if (descriptor.Length == 0)
            {
                return E_FAIL;
            }

            nint buffer = RegistryPermissionsDialog.AllocateDescriptorBuffer(descriptor);
            if (buffer == 0)
            {
                return E_OUTOFMEMORY;
            }

            *ppSecurityDescriptor = buffer;
            return S_OK;
        }
        catch (RegistryOperationException ex)
        {
            return ex.HResult;
        }
        catch (Exception)
        {
            return E_FAIL;
        }
    }

    public int SetSecurity(uint securityInformation, nint pSecurityDescriptor)
    {
        try
        {
            if (pSecurityDescriptor == 0)
            {
                return E_INVALIDARG;
            }

            byte[] descriptor = RegistrySecurityDescriptor.CopyFromNative(pSecurityDescriptor);
            _writer.SetKeySecurity(
                _path,
                (RegistrySecurityInformation)securityInformation,
                descriptor);
            return S_OK;
        }
        catch (RegistryOperationException ex)
        {
            return ex.HResult;
        }
        catch (ArgumentException)
        {
            return E_INVALIDARG;
        }
        catch (Exception)
        {
            return E_FAIL;
        }
    }

    public int GetAccessRights(
        Guid* pguidObjectType,
        uint dwFlags,
        SI_ACCESS** ppAccess,
        uint* pcAccesses,
        uint* piDefaultAccess)
    {
        _ = pguidObjectType;
        _ = dwFlags;

        if (ppAccess is null || pcAccesses is null || piDefaultAccess is null)
        {
            return E_POINTER;
        }

        if (_accessArray == 0)
        {
            int count = AccessRights.Length;
            _accessArray = Marshal.AllocCoTaskMem(count * sizeof(SI_ACCESS));
            var entries = (SI_ACCESS*)_accessArray;

            for (var index = 0; index < count; index++)
            {
                entries[index] = new SI_ACCESS
                {
                    pguid = 0,
                    mask = AccessRights[index].Mask,
                    pszName = Marshal.StringToCoTaskMemUni(AccessRights[index].Name),
                    dwFlags = AccessRights[index].Flags,
                };
            }
        }

        *ppAccess = (SI_ACCESS*)_accessArray;
        *pcAccesses = (uint)AccessRights.Length;
        *piDefaultAccess = DefaultAccessIndex;
        return S_OK;
    }

    public int MapGeneric(Guid* pguidObjectType, byte* pAceFlags, uint* pMask)
    {
        _ = pguidObjectType;
        _ = pAceFlags;

        if (pMask is null)
        {
            return E_POINTER;
        }

        uint mask = *pMask;
        if ((mask & GENERIC_READ) != 0) mask |= KEY_READ;
        if ((mask & GENERIC_WRITE) != 0) mask |= KEY_WRITE;
        if ((mask & GENERIC_EXECUTE) != 0) mask |= KEY_EXECUTE;
        if ((mask & GENERIC_ALL) != 0) mask |= KEY_ALL_ACCESS;
        mask &= ~(GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE | GENERIC_ALL);
        *pMask = mask;
        return S_OK;
    }

    public int GetInheritTypes(SI_INHERIT_TYPE** ppInheritTypes, uint* pcInheritTypes)
    {
        if (ppInheritTypes is not null)
        {
            *ppInheritTypes = null;
        }

        if (pcInheritTypes is not null)
        {
            *pcInheritTypes = 0;
        }

        return E_NOTIMPL;
    }

    public int PropertySheetPageCallback(nint hwnd, uint uMsg, uint uPage)
    {
        _ = hwnd;
        _ = uMsg;
        _ = uPage;
        return S_OK;
    }

    private byte[] ReadDescriptor(RegistrySecurityInformation requested)
    {
        try
        {
            return _reader.GetKeySecurity(_path, requested);
        }
        catch (RegistryOperationException ex)
            when ((requested & RegistrySecurityInformation.Sacl) != 0 &&
                ex.Win32Error == ErrorPrivilegeNotHeld)
        {
            // Auditing (SACL) needs SeSecurityPrivilege. Without it, still show owner, group,
            // and DACL instead of failing the whole dialog.
            return _reader.GetKeySecurity(
                _path,
                requested & ~RegistrySecurityInformation.Sacl);
        }
    }

    /// <summary>
    /// Frees the COM strings and the access-right array after <c>EditSecurity</c> returns. The
    /// security descriptor buffer belongs to aclui and is deliberately not freed here.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_objectNamePtr != 0)
        {
            Marshal.FreeCoTaskMem(_objectNamePtr);
            _objectNamePtr = 0;
        }

        if (_accessArray != 0)
        {
            var entries = (SI_ACCESS*)_accessArray;
            for (var index = 0; index < AccessRights.Length; index++)
            {
                Marshal.FreeCoTaskMem(entries[index].pszName);
                entries[index].pszName = 0;
            }

            Marshal.FreeCoTaskMem(_accessArray);
            _accessArray = 0;
        }
    }
}

/// <summary>Shows the standard aclui security editor for one registry key.</summary>
internal static unsafe partial class RegistryPermissionsDialog
{
    private const int LocalMemFixed = 0;

    /// <summary>
    /// Runs <c>EditSecurity</c> and returns its HRESULT. aclui owns the modal loop and disables the
    /// owner window while the dialog is open.
    /// </summary>
    public static int Show(
        nint ownerHwnd,
        RegistryReader reader,
        RegistryStore store,
        RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(path);

        if (ownerHwnd == 0)
        {
            throw new ArgumentException(
                "The permissions dialog needs the main window handle as its owner.",
                nameof(ownerHwnd));
        }

        using var information = new RegistryKeySecurityInformation(
            reader,
            store.Writer,
            path,
            path.ToDisplayString());
        return EditSecurity(ownerHwnd, information);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalAlloc(uint uFlags, nuint uBytes);

    [LibraryImport("aclui.dll")]
    private static partial int EditSecurity(nint hwndOwner, ISecurityInformation psi);

    internal static nint AllocateDescriptorBuffer(byte[] descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        nint buffer = LocalAlloc(LocalMemFixed, (nuint)descriptor.Length);
        if (buffer == 0)
        {
            return 0;
        }

        Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
        return buffer;
    }
}
