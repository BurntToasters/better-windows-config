using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Core;
using Bwc.Registry;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// A failed write of a key's security, from the Permissions dialog. <see cref="Win32Error"/> is the root key's
/// code, or the first subkey's code when the root changed and subkeys did not. <see cref="OwnerChange"/> is set when
/// the write changed the owner. <see cref="SubkeyErrors"/> lists the Win32 code of each subkey that did not change.
/// </summary>
internal readonly record struct SecurityWriteFailure(int Win32Error, bool OwnerChange, IReadOnlyList<int> SubkeyErrors);

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

/// <summary>The Win32 MSG layout, used only to pump messages while a write runs on the worker thread.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public int pointX;
    public int pointY;
}

/// <summary>
/// Writable aclui callback object for one registry key. Security descriptor buffers returned to
/// aclui are allocated with <c>LocalAlloc</c> and are intentionally not freed here.
/// </summary>
/// <summary>
/// The aclui <c>IEffectivePermission</c> contract. aclui queries for it on the security object, and the Effective
/// Access page of Advanced Security Settings needs it (oracle: "View effective access" in regedit-security.json).
/// </summary>
[GeneratedComInterface]
[Guid("3853DC76-9F35-407c-88A1-D19344365FBC")]
internal unsafe partial interface IEffectivePermission
{
    [PreserveSig]
    int GetEffectivePermission(
        Guid* pguidObjectType,
        nint pUserSid,
        nint pszServerName,
        nint pSD,
        nint* ppObjectTypeList,
        uint* pcObjectTypeListLength,
        uint** ppGrantedAccessList,
        uint* pcGrantedAccessListLength);
}

/// <summary>
/// The aclui <c>ISecurityObjectTypeInfo</c> contract. aclui asks it where each inherited entry comes from, for the
/// "Inherited from" column and to grey out the check boxes an inherited entry sets (permissions.md section 41).
/// </summary>
[GeneratedComInterface]
[Guid("FC3066EB-79EF-444b-9111-D18A75EBF2FA")]
internal unsafe partial interface ISecurityObjectTypeInfo
{
    [PreserveSig]
    int GetInheritSource(uint securityInformation, nint pAcl, nint* ppInheritArray);
}

/// <summary>The accctrl.h <c>INHERITED_FROM</c> layout on 64-bit: a 4 byte gap, padding, then a name pointer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InheritedFrom
{
    public int GenerationGap;
    public nint AncestorName;
}

[GeneratedComClass]
internal sealed unsafe partial class RegistryKeySecurityInformation
    : ISecurityInformation, IEffectivePermission, ISecurityObjectTypeInfo, IDisposable
{
    // aclui.h
    private const uint SI_EDIT_ALL = 0x00000003;
    private const uint SI_CONTAINER = 0x00000004;
    private const uint SI_ADVANCED = 0x00000010;
    private const uint SI_OWNER_RECURSE = 0x00000100;
    private const uint SI_RESET_DACL_TREE = 0x00004000;
    private const uint SI_EDIT_EFFECTIVE = 0x00020000;
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

    // winnt.h ACE flag, cleared by MapGeneric (see there).
    private const byte ObjectInheritAce = 0x01;

    private const int S_OK = 0;
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const int E_PENDING = unchecked((int)0x8000000A);
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private const int DefaultAccessIndex = 1; // "Read"

    // The first entries of AccessRights are the basic page: Full Control and Read.
    private const uint BasicAccessCount = 2;

    // The deepest ancestor chain read for the "Inherited from" column. Registry paths are far shallower.
    private const int MaxInheritLevels = 64;

    // The SECURITY_INFORMATION bits the engine accepts. aclui's SI_* bits are outside this mask.
    private const uint SecurityInformationMask =
        (uint)(RegistrySecurityInformation.Owner
            | RegistrySecurityInformation.Group
            | RegistrySecurityInformation.Dacl
            | RegistrySecurityInformation.Sacl
            | RegistrySecurityInformation.ProtectedDacl
            | RegistrySecurityInformation.UnprotectedDacl
            | RegistrySecurityInformation.ProtectedSacl
            | RegistrySecurityInformation.UnprotectedSacl);

    private const uint ProtectionMask =
        (uint)(RegistrySecurityInformation.ProtectedDacl | RegistrySecurityInformation.UnprotectedDacl);

    // Resource key for the name aclui shows, and the ACE flags. The names are in Resources.resw (oracle 7090 to 7092).
    private static readonly (string NameKey, uint Flags)[] InheritTypes =
    {
        ("PermissionsInheritThisKeyOnly", RegistryInheritanceModes.ThisKeyOnly),
        ("PermissionsInheritThisKeyAndSubkeys", RegistryInheritanceModes.ThisKeyAndSubkeys),
        ("PermissionsInheritSubkeysOnly", RegistryInheritanceModes.SubkeysOnly),
    };

    private int _writeInProgress;
    private readonly nint _ownerHwnd;

    // user32.h message values for the pump that runs while the write is on the worker thread.
    private const uint PmRemove = 0x0001;
    private const uint WmQuit = 0x0012;

    // user32.h message box flags and results, for the replace confirmation.
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbDefButton2 = 0x00000100;
    private const int IdYes = 6;

    // objbase.h: the caller aborted the operation, so nothing was written.
    private const int E_ABORT = unchecked((int)0x80004004);

    private static readonly (string Name, uint Mask, uint Flags)[] AccessRights =
    {
        ("Full Control", KEY_ALL_ACCESS, SI_ACCESS_GENERAL | SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
        ("Read", KEY_READ, SI_ACCESS_GENERAL | SI_ACCESS_CONTAINER),
        // Classic shows only Full Control and Read as general rows, plus a Special row (oracle regedit-security.json).
        ("Write", KEY_WRITE, SI_ACCESS_SPECIFIC | SI_ACCESS_CONTAINER),
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
    private readonly string? _serverName;
    private nint _objectNamePtr;
    private nint _serverNamePtr;
    private nint _accessArray;
    private nint _inheritArray;
    private bool _disposed;

    /// <param name="serverName">The remote computer that holds the key, or null for the local computer.</param>
    public RegistryKeySecurityInformation(
        RegistryReader reader,
        RegistryWriter writer,
        RegistryPath path,
        string objectName,
        string? serverName,
        nint ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(objectName);

        _reader = reader;
        _writer = writer;
        _path = path;
        _objectName = objectName;
        _serverName = string.IsNullOrEmpty(serverName) ? null : serverName;
        _ownerHwnd = ownerHwnd;
    }

    /// <summary>
    /// The last write that failed in <see cref="SetSecurity"/>, or null. The dialog reads it after aclui returns,
    /// so the classic save text can be shown for the failure.
    /// </summary>
    public SecurityWriteFailure? WriteFailure { get; private set; }

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

        // A remote key names its computer, so aclui resolves accounts and reads the descriptor there.
        if (_serverName is not null && _serverNamePtr == 0)
        {
            _serverNamePtr = Marshal.StringToCoTaskMemUni(_serverName);
        }

        *pObjectInfo = new SI_OBJECT_INFO
        {
            dwFlags = SI_EDIT_ALL | SI_ADVANCED | SI_CONTAINER | SI_RESET_DACL_TREE | SI_OWNER_RECURSE | SI_EDIT_EFFECTIVE,
            hInstance = 0,
            pszServerName = _serverNamePtr,
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
            byte[] descriptor = PresentInheritance(ReadDescriptor((RegistrySecurityInformation)requestedInformation));
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
            Log.Warn(
                $"Registry permissions could not read the security descriptor (requested 0x{requestedInformation:X8}, " +
                $"Win32 error {ex.Win32Error}).");
            return ex.HResult;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or InvalidCastException)
        {
            Log.Exception("Registry permissions could not read the security descriptor.", ex);
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

            // aclui passes its own SI_* bits in the same value. Only the SECURITY_INFORMATION bits go to the engine.
            var information = (RegistrySecurityInformation)(securityInformation & SecurityInformationMask);
            bool propagate = (securityInformation & SI_RESET_DACL_TREE) != 0;
            bool ownerRecurse = (securityInformation & SI_OWNER_RECURSE) != 0 &&
                (information & RegistrySecurityInformation.Owner) != 0;
            Log.Info(
                $"Registry permissions SetSecurity information=0x{(uint)information:X8} propagate={propagate} ownerRecurse={ownerRecurse}.");
            if (information == RegistrySecurityInformation.None && !propagate)
            {
                return S_OK;
            }

            if (Interlocked.Exchange(ref _writeInProgress, 1) != 0)
            {
                return E_PENDING;
            }

            try
            {
                if (propagate && !ConfirmReplaceSubkeys())
                {
                    return E_ABORT;
                }

                byte[] descriptor = RegistrySecurityDescriptor.CopyFromNative(pSecurityDescriptor);
                WriteFailure = null;
                bool ownerChange = (information & RegistrySecurityInformation.Owner) != 0;
                try
                {
                    RunOnWorker(() => WriteSecurity(information, descriptor, propagate, ownerRecurse));
                }
                catch (RegistrySecurityWalkException walk)
                {
                    // The root changed and some subkeys did not. aclui keeps the sheet open, and the dialog shows the
                    // classic text for the subkeys (section 33 of permissions.md).
                    WriteFailure = new SecurityWriteFailure(
                        walk.Failures[0].Win32Error,
                        ownerChange,
                        walk.Failures.Select(failure => failure.Win32Error).ToArray());
                    return walk.HResult;
                }
                catch (Exception ex)
                {
                    WriteFailure = new SecurityWriteFailure(
                        ex is RegistryOperationException registry ? registry.Win32Error : -1,
                        ownerChange,
                        Array.Empty<int>());
                    throw;
                }

                return S_OK;
            }
            finally
            {
                _ = Interlocked.Exchange(ref _writeInProgress, 0);
            }
        }
        catch (RegistryOperationException ex)
        {
            return ex.HResult;
        }
        catch (ArgumentException ex)
        {
            Log.Warn("Registry permissions rejected an argument: " + ex.Message);
            return E_INVALIDARG;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException)
        {
            Log.Exception("Registry permissions could not write the security descriptor.", ex);
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
        // The basic page asks with no flags and lists what it is given. The Advanced pages ask with SI_ADVANCED and need
        // every right. Returning the long list to the basic page gave 13 rows where classic shows Full Control, Read and
        // "Special permissions" (permissions.md section 32).
        *pcAccesses = (dwFlags & SI_ADVANCED) != 0 ? (uint)AccessRights.Length : BasicAccessCount;
        *piDefaultAccess = DefaultAccessIndex;
        return S_OK;
    }


    public int MapGeneric(Guid* pguidObjectType, byte* pAceFlags, uint* pMask)
    {
        _ = pguidObjectType;

        // A key has no object children, so OBJECT_INHERIT_ACE (0x1) means nothing on a key. Classic regedit clears it
        // here. Measured in docs/parity/oracle/permissions-inherit.json: 0x1 is labeled like 0x0, 0x3 like 0x2,
        // 0xB like 0xA.
        if (pAceFlags is not null)
        {
            *pAceFlags &= unchecked((byte)~ObjectInheritAce);
        }

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
        if (ppInheritTypes is null || pcInheritTypes is null)
        {
            return E_POINTER;
        }

        if (_inheritArray == 0)
        {
            int count = InheritTypes.Length;
            _inheritArray = Marshal.AllocCoTaskMem(count * sizeof(SI_INHERIT_TYPE));
            var entries = (SI_INHERIT_TYPE*)_inheritArray;

            for (var index = 0; index < count; index++)
            {
                entries[index] = new SI_INHERIT_TYPE
                {
                    pguid = 0,
                    dwFlags = InheritTypes[index].Flags,
                    pszName = Marshal.StringToCoTaskMemUni(RegeditStrings.Get(InheritTypes[index].NameKey)),
                };
            }
        }

        *ppInheritTypes = (SI_INHERIT_TYPE*)_inheritArray;
        *pcInheritTypes = (uint)InheritTypes.Length;
        return S_OK;
    }

    /// <summary>
    /// Writes the security on a worker thread. The root gets the information aclui chose. When the
    /// "Replace all child object permission entries" check box is set, the children are then computed
    /// from the root's DACL in a second, propagating call. That call carries the DACL and the protection
    /// state only, because the engine refuses audit entries during propagation.
    /// </summary>
    private void WriteSecurity(
        RegistrySecurityInformation information,
        byte[] descriptor,
        bool propagate,
        bool ownerRecurse)
    {
        // The DACL and group go first, then the propagation of the DACL. An owner that is not the current user removes
        // the user's implicit WRITE_DAC, so the owner walk runs last (permissions.md section 42, item C).
        RegistrySecurityInformation rootInformation = ownerRecurse
            ? information & ~RegistrySecurityInformation.Owner
            : information;
        if (rootInformation != RegistrySecurityInformation.None)
        {
            _writer.SetKeySecurity(_path, rootInformation, descriptor);
        }

        if (propagate)
        {
            var propagation = RegistrySecurityInformation.Dacl | (information & (RegistrySecurityInformation)ProtectionMask);
            _writer.SetKeySecurity(_path, descriptor, propagation, propagateToSubkeys: true);
        }

        if (ownerRecurse)
        {
            _writer.SetKeyOwnerTree(_path, descriptor);
        }
    }

    /// <summary>
    /// Asks the classic MSG-178 question before a propagation, with No as the default. aclui calls this on the UI
    /// thread and waits for the answer, so the question is a native message box owned by the main window.
    /// </summary>
    private bool ConfirmReplaceSubkeys()
    {
        string text = string.Format(
            CultureInfo.CurrentCulture,
            RegeditStrings.Get("MsgReplaceSubkeysConfirm"),
            _objectName);
        // The question belongs to the aclui window that asked it, so it opens over that window and not behind it.
        nint active = GetActiveWindow();
        int answer = MessageBoxW(
            active != 0 ? active : _ownerHwnd,
            text,
            RegeditStrings.Get("MsgCaptionRegistryEditor"),
            MbYesNo | MbIconQuestion | MbDefButton2);
        return answer == IdYes;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread-pool thread. The UI thread keeps pumping window messages
    /// while it waits, so the security editor keeps repainting during a long propagation. Exceptions from
    /// <paramref name="work"/> are rethrown on this thread.
    /// </summary>
    private static void RunOnWorker(Action work)
    {
        Task task = Task.Run(work);
        while (!task.IsCompleted)
        {
            PumpMessages();
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }

    private static void PumpMessages()
    {
        while (PeekMessageW(out NativeMessage message, 0, 0, 0, PmRemove) != 0)
        {
            if (message.message == WmQuit)
            {
                // Leave the quit request for the message loop that owns it, and stop pumping.
                PostQuitMessage((int)message.wParam);
                return;
            }

            _ = TranslateMessage(in message);
            _ = DispatchMessageW(in message);
        }
    }

    /// <summary>
    /// Returns the access that <paramref name="pUserSid"/> holds on the key, for the Effective Access page. The DACL is
    /// read in order: the owner holds READ_CONTROL and WRITE_DAC, an allow entry adds the rights not yet denied, and a
    /// deny entry removes the rights not yet allowed. Entries for the SID and for Everyone count. For the current
    /// user, the entries for its groups count too. A null DACL grants everything. Permissions.md section 38.
    /// </summary>
    public int GetEffectivePermission(
        Guid* pguidObjectType,
        nint pUserSid,
        nint pszServerName,
        nint pSD,
        nint* ppObjectTypeList,
        uint* pcObjectTypeListLength,
        uint** ppGrantedAccessList,
        uint* pcGrantedAccessListLength)
    {
        _ = pguidObjectType;
        _ = pszServerName;

        if (ppObjectTypeList is null || pcObjectTypeListLength is null || ppGrantedAccessList is null ||
            pcGrantedAccessListLength is null)
        {
            return E_POINTER;
        }

        *ppObjectTypeList = 0;
        *pcObjectTypeListLength = 0;
        *ppGrantedAccessList = null;
        *pcGrantedAccessListLength = 0;

        try
        {
            byte[] descriptor = pSD != 0
                ? RegistrySecurityDescriptor.CopyFromNative(pSD)
                : _reader.GetKeySecurity(_path, RegistrySecurityInformation.Owner | RegistrySecurityInformation.Dacl);
            SecurityIdentifier sid = pUserSid != 0
                ? new SecurityIdentifier(pUserSid)
                : WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current user SID.");

            // aclui frees the granted-access list with LocalFree. The object-type list is one static entry with level 0
            // that points at a zero GUID, the plain object case. It is shared and never freed (permissions.md section 42).
            uint* granted = (uint*)RegistryPermissionsDialog.AllocateLocal(sizeof(uint));
            if (granted is null)
            {
                return E_OUTOFMEMORY;
            }

            *granted = EffectiveMask(descriptor, sid);

            *ppObjectTypeList = PlainObjectTypeList();
            *pcObjectTypeListLength = 1;
            *ppGrantedAccessList = granted;
            *pcGrantedAccessListLength = 1;
            return S_OK;
        }
        catch (RegistryOperationException ex)
        {
            return ex.HResult;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Exception("Registry permissions could not compute the effective access.", ex);
            return E_FAIL;
        }
    }

    private static nint _plainObjectTypeList;

    /// <summary>
    /// One OBJECT_TYPE_LIST entry (level 0, sbz 0, pointer to a zero GUID) in native memory that lives as long as the
    /// process. aclui reads it and does not free it.
    /// </summary>
    private static nint PlainObjectTypeList()
    {
        if (_plainObjectTypeList != 0)
        {
            return _plainObjectTypeList;
        }

        // 16 bytes for the zero GUID, then 16 for the entry (WORD, WORD, pad, GUID*).
        nint block = (nint)NativeMemory.AllocZeroed(32);
        *(nint*)(block + 24) = block;
        nint previous = Interlocked.CompareExchange(ref _plainObjectTypeList, block + 16, 0);
        if (previous != 0)
        {
            NativeMemory.Free((void*)block);
            return previous;
        }

        return block + 16;
    }

    private static uint EffectiveMask(byte[] descriptor, SecurityIdentifier sid)
    {
        var raw = new RawSecurityDescriptor(descriptor, 0);
        if (raw.DiscretionaryAcl is null)
        {
            return KEY_ALL_ACCESS;
        }

        uint allowed = 0;
        uint denied = 0;
        if (raw.Owner is not null && raw.Owner.Equals(sid))
        {
            allowed |= READ_CONTROL | WRITE_DAC;
        }

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        bool isCurrentUser = current.User is not null && current.User.Equals(sid);

        foreach (GenericAce ace in raw.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce qualified)
            {
                continue;
            }

            if ((qualified.AceFlags & AceFlags.InheritOnly) != 0)
            {
                // An inherit-only entry is for the children, not for the key (permissions.md section 42, item F).
                continue;
            }

            SecurityIdentifier aceSid = qualified.SecurityIdentifier;
            bool applies = aceSid.Equals(sid) || aceSid.Equals(everyone);
            if (!applies && isCurrentUser)
            {
                foreach (IdentityReference group in current.Groups ?? new IdentityReferenceCollection())
                {
                    applies |= group.Equals(aceSid);
                }
            }

            if (!applies)
            {
                continue;
            }

            uint mask = MapGenericRights((uint)qualified.AccessMask);
            if (qualified.AceQualifier == AceQualifier.AccessAllowed)
            {
                allowed |= mask & ~denied;
            }
            else if (qualified.AceQualifier == AceQualifier.AccessDenied)
            {
                denied |= mask & ~allowed;
            }
        }

        return allowed & ~denied;
    }

    private static uint MapGenericRights(uint mask)
    {
        if ((mask & GENERIC_READ) != 0) mask |= KEY_READ;
        if ((mask & GENERIC_WRITE) != 0) mask |= KEY_WRITE;
        if ((mask & GENERIC_EXECUTE) != 0) mask |= KEY_EXECUTE;
        if ((mask & GENERIC_ALL) != 0) mask |= KEY_ALL_ACCESS;
        return mask & ~(GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE | GENERIC_ALL);
    }

    /// <summary>
    /// Tells aclui where each entry of <paramref name="pAcl"/> was inherited from. An explicit entry has gap 0 and no
    /// name. An inherited entry names the nearest ancestor that holds the same entry explicitly, as aclui shows it in the
    /// "Inherited from" column (permissions.md section 41). The block is one LocalAlloc, which aclui frees.
    /// </summary>
    public int GetInheritSource(uint securityInformation, nint pAcl, nint* ppInheritArray)
    {
        if (ppInheritArray is null)
        {
            return E_POINTER;
        }

        *ppInheritArray = 0;
        if ((securityInformation & (uint)RegistrySecurityInformation.Dacl) == 0 || pAcl == 0)
        {
            return E_NOTIMPL;
        }

        try
        {
            int size = *(ushort*)(pAcl + 2);
            byte[] aclBytes = new byte[size];
            Marshal.Copy(pAcl, aclBytes, 0, size);
            var acl = new RawAcl(aclBytes, 0);

            (int Gap, string? Name)[] sources = InheritSources(acl);
            nint block = BuildInheritBlock(sources);
            if (block == 0)
            {
                return E_OUTOFMEMORY;
            }

            *ppInheritArray = block;
            return S_OK;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RegistryOperationException)
        {
            Log.Exception("Registry permissions could not work out where the entries were inherited from.", ex);
            return E_FAIL;
        }
    }

    private (int Gap, string? Name)[] InheritSources(RawAcl acl)
    {
        var sources = new (int Gap, string? Name)[acl.Count];
        List<(RegistryPath Path, RawAcl? Dacl)>? ancestors = null;

        for (int index = 0; index < acl.Count; index++)
        {
            GenericAce ace = acl[index];
            if ((ace.AceFlags & AceFlags.Inherited) == 0)
            {
                continue;
            }

            ancestors ??= ReadAncestorDacls();
            if (ancestors.Count == 0)
            {
                continue;
            }

            // The parent is the fallback when no ancestor holds the entry explicitly any more.
            sources[index] = (1, InheritSourceName(ancestors[0].Path));
            for (int level = 0; level < ancestors.Count; level++)
            {
                GenericAce? match = FindSameEntry(ancestors[level].Dacl, ace);
                if (match is null)
                {
                    break;
                }

                if ((match.AceFlags & AceFlags.Inherited) == 0)
                {
                    sources[index] = (level + 1, InheritSourceName(ancestors[level].Path));
                    break;
                }
            }
        }

        return sources;
    }

    /// <summary>The DACLs of the key's ancestors, nearest first. An ancestor that cannot be read ends the list.</summary>
    private List<(RegistryPath Path, RawAcl? Dacl)> ReadAncestorDacls()
    {
        var ancestors = new List<(RegistryPath Path, RawAcl? Dacl)>();
        RegistryPath? current = _path.Parent;
        while (current is not null && ancestors.Count < MaxInheritLevels)
        {
            try
            {
                byte[] descriptor = _reader.GetKeySecurity(current, RegistrySecurityInformation.Dacl);
                ancestors.Add((current, new RawSecurityDescriptor(descriptor, 0).DiscretionaryAcl));
            }
            catch (RegistryOperationException)
            {
                break;
            }

            current = current.Parent;
        }

        return ancestors;
    }

    private static GenericAce? FindSameEntry(RawAcl? dacl, GenericAce wanted)
    {
        if (dacl is null || wanted is not QualifiedAce wantedAce)
        {
            return null;
        }

        foreach (GenericAce candidate in dacl)
        {
            if (candidate is QualifiedAce qualified &&
                (qualified.AceFlags & AceFlags.ContainerInherit) != 0 &&
                qualified.AceQualifier == wantedAce.AceQualifier &&
                qualified.AccessMask == wantedAce.AccessMask &&
                qualified.SecurityIdentifier.Equals(wantedAce.SecurityIdentifier))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The root without HKEY_ and the key path, as classic shows it: CURRENT_USER\Software.</summary>
    private static string InheritSourceName(RegistryPath path)
    {
        string root = path.Root.DisplayName();
        const string Prefix = "HKEY_";
        if (root.StartsWith(Prefix, StringComparison.Ordinal))
        {
            root = root[Prefix.Length..];
        }

        return path.Components.Count == 0 ? root : root + "\\" + string.Join('\\', path.Components);
    }

    private static nint BuildInheritBlock((int Gap, string? Name)[] sources)
    {
        int entries = sources.Length * sizeof(InheritedFrom);
        int total = entries;
        foreach ((int _, string? name) in sources)
        {
            total += name is null ? 0 : (name.Length + 1) * sizeof(char);
        }

        nint block = RegistryPermissionsDialog.AllocateLocal(Math.Max(total, 1));
        if (block == 0)
        {
            return 0;
        }

        var array = (InheritedFrom*)block;
        char* text = (char*)(block + entries);
        for (int index = 0; index < sources.Length; index++)
        {
            array[index].GenerationGap = sources[index].Gap;
            array[index].AncestorName = 0;
            string? name = sources[index].Name;
            if (name is null)
            {
                continue;
            }

            array[index].AncestorName = (nint)text;
            name.AsSpan().CopyTo(new Span<char>(text, name.Length));
            text[name.Length] = '\0';
            text += name.Length + 1;
        }

        return block;
    }

    public int PropertySheetPageCallback(nint hwnd, uint uMsg, uint uPage)
    {
        _ = hwnd;
        _ = uMsg;
        _ = uPage;
        return S_OK;
    }

    /// <summary>
    /// The registry holds no inherited marks, but aclui needs them to tell inherited entries from explicit ones: they
    /// drive the "Inherited from" column, the greyed check boxes and what Apply sends (permissions.md section 41). A
    /// descriptor with a DACL gets them from the parent's entries. A parent that cannot be read leaves it as stored.
    /// </summary>
    private byte[] PresentInheritance(byte[] descriptor)
    {
        if (_path.Parent is not { } parentPath || !RegistryDescriptorHasDacl(descriptor))
        {
            return descriptor;
        }

        try
        {
            byte[] parent = _reader.GetKeySecurity(parentPath, RegistrySecurityInformation.Dacl);
            return RegistrySecurityPresentation.MarkInheritedEntries(descriptor, parent);
        }
        catch (Exception ex) when (ex is RegistryOperationException or ArgumentException)
        {
            return descriptor;
        }
    }

    private static bool RegistryDescriptorHasDacl(byte[] descriptor) =>
        new RawSecurityDescriptor(descriptor, 0).DiscretionaryAcl is not null;

    /// <summary>
    /// Reads the requested parts. A request for the SACL without SeSecurityPrivilege fails with ERROR_ACCESS_DENIED (5)
    /// from the registry, and the HRESULT reaches aclui, which shows its own "can't be displayed" text. The SACL is
    /// never dropped from the answer silently (permissions.md section 37).
    /// </summary>
    private byte[] ReadDescriptor(RegistrySecurityInformation requested) => _reader.GetKeySecurity(_path, requested);

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

        if (_serverNamePtr != 0)
        {
            Marshal.FreeCoTaskMem(_serverNamePtr);
            _serverNamePtr = 0;
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

        if (_inheritArray != 0)
        {
            var entries = (SI_INHERIT_TYPE*)_inheritArray;
            for (var index = 0; index < InheritTypes.Length; index++)
            {
                Marshal.FreeCoTaskMem(entries[index].pszName);
                entries[index].pszName = 0;
            }

            Marshal.FreeCoTaskMem(_inheritArray);
            _inheritArray = 0;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(out NativeMessage message, nint hWnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(in NativeMessage message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in NativeMessage message);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial nint GetActiveWindow();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}

/// <summary>Shows the standard aclui security editor for one registry key.</summary>
internal static unsafe partial class RegistryPermissionsDialog
{
    private const int LocalMemFixed = 0;

    /// <summary>
    /// Runs <c>EditSecurity</c> and returns 0, or the HRESULT of its last error when it failed. aclui owns the
    /// modal loop and disables the owner window while the dialog is open. <paramref name="serverName"/> is the
    /// remote computer of the key, or null for the local computer.
    /// </summary>
    public static int Show(
        nint ownerHwnd,
        RegistryReader reader,
        RegistryStore store,
        RegistryPath path,
        string? serverName,
        out SecurityWriteFailure? writeFailure)
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
            path.ToDisplayString(),
            serverName,
            ownerHwnd);
        bool shown = EditSecurity(ownerHwnd, information);
        int lastError = Marshal.GetLastWin32Error();
        writeFailure = information.WriteFailure;
        if (shown)
        {
            return 0;
        }

        // HRESULT_FROM_WIN32. A zero last error still means failure, so it maps to E_FAIL.
        return lastError == 0 ? unchecked((int)0x80004005) : unchecked((int)(0x80070000u | (uint)(lastError & 0xFFFF)));
    }

    /// <summary>
    /// The resource key of the classic save text for a failed write (oracle 7070 to 7076). A failed subkey list wins over
    /// the root code: the root changed, so classic reports the subkeys (permissions.md section 33).
    /// </summary>
    internal static string SaveMessageKey(SecurityWriteFailure failure)
    {
        const int accessDenied = 5;
        const int keyDeleted = 1018;

        if (failure.SubkeyErrors.Count > 0)
        {
            if (failure.OwnerChange)
            {
                return "MsgOwnerSetFailed";
            }

            if (failure.SubkeyErrors.Contains(accessDenied))
            {
                return "MsgSecuritySubkeysDenied";
            }

            return failure.SubkeyErrors.Contains(keyDeleted) ? "MsgSecuritySubkeysDeleted" : "MsgSecuritySubkeysFailed";
        }

        if (failure.Win32Error == accessDenied)
        {
            return "MsgSecuritySaveDenied";
        }

        if (failure.OwnerChange)
        {
            return "MsgOwnerSetFailed";
        }

        return failure.Win32Error == keyDeleted ? "MsgSecuritySaveDeleted" : "MsgSecuritySetFailed";
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalAlloc(uint uFlags, nuint uBytes);

    /// <summary>aclui.h: <c>BOOL EditSecurity(HWND, LPSECURITYINFO)</c>. Zero means it could not show the editor.</summary>
    [LibraryImport("aclui.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EditSecurity(nint hwndOwner, ISecurityInformation psi);

    /// <summary>A fixed <c>LocalAlloc</c> block that the caller hands to aclui, which frees it with <c>LocalFree</c>.</summary>
    internal static nint AllocateLocal(int bytes) => LocalAlloc(LocalMemFixed, (nuint)bytes);

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
