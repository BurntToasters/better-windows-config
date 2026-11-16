using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Bwc.E2E.Installer;

/// <summary>
/// Read-only native and ACL helpers of the machine-mode installer tests (docs/failure-modes/installer-e2e.md, 49 to 75).
/// Tokens are read, never changed: the tests never enable a privilege in their own process, because the elevated helper
/// they start inherits the state and the privilege check (75) depends on it being off. Nothing here writes a file,
/// a registry value, or a security descriptor.
/// </summary>
internal static class MachineNative
{
    public const string DebugPrivilege = "SeDebugPrivilege";
    public const int MediumIntegrity = 0x2000;
    public const int HighIntegrity = 0x3000;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const int TokenPrivilegesClass = 3;
    private const int TokenIntegrityLevelClass = 25;
    private const uint SePrivilegeEnabled = 0x2;
    private const int LuidAndAttributesSize = 12;
    private const int PrivilegeCountSize = 4;
    private const int SidSubAuthorityOffset = 8;
    private const int SidSubAuthorityCountOffset = 1;
    private const ushort VtLpwstr = 31;

    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary>The identity of the group SIDs the ACL checks name.</summary>
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>NT SERVICETrustedInstaller, which owns most of the folders of Program Files.</summary>
    public static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static readonly Guid ProgramFilesFolderId = new("905E63B6-C1BF-494E-B29C-65B732D3D21A");

    /// <summary>
    /// The Program Files folder of this process's own bitness (the 64-bit one in a 64-bit test host), from the shell's known
    /// folder, never from an environment variable: ProgramW6432 is a value the caller of the test run controls.
    /// </summary>
    public static string ProgramFilesFolder()
    {
        Guid id = ProgramFilesFolderId;
        int hr = SHGetKnownFolderPath(ref id, 0, nint.Zero, out nint text);
        try
        {
            if (hr < 0 || text == nint.Zero)
            {
                throw new COMException("SHGetKnownFolderPath(FOLDERID_ProgramFiles) failed", hr);
            }

            return Marshal.PtrToStringUni(text) ?? throw new COMException("SHGetKnownFolderPath returned no text", hr);
        }
        finally
        {
            if (text != nint.Zero)
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
    }

    /// <summary>True when the owner is Administrators, SYSTEM, or TrustedInstaller (a user's own SID is not).</summary>
    public static bool OwnerIsTrusted(FolderAcl acl) =>
        acl.Owner is not null && (acl.Owner.Equals(Administrators) || acl.Owner.Equals(LocalSystem) || acl.Owner.Equals(TrustedInstaller));

    private static readonly SecurityIdentifier[] ShouldNotWrite =
    [
        new(WellKnownSidType.BuiltinUsersSid, null),
        new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.WorldSid, null),
    ];

    private const FileSystemRights WriteMask =
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    /// <summary>
    /// True when this process is an elevated administrator: the Administrators group is enabled in the token (a filtered
    /// token has it deny-only) and the integrity level is high. Same rule as the existing Elevated tests.
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return false;
        }

        int rid = CurrentIntegrityRid();
        return rid < 0 || rid >= HighIntegrity;
    }

    /// <summary>The integrity level RID of the process token (0x2000 medium, 0x3000 high), or -1 if unreadable.</summary>
    public static int IntegrityRid(int processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
        {
            return -1;
        }

        try
        {
            return OpenProcessToken(process, TokenQuery, out nint token) ? ReadIntegrity(token) : -1;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    public static int CurrentIntegrityRid() => IntegrityRid(Environment.ProcessId);

    /// <summary>
    /// The state of one privilege in another process's token: held, and enabled. False and false when the process or the
    /// privilege cannot be read.
    /// </summary>
    /// <summary>
    /// Enables or disables <paramref name="name"/> on this process's own token. Returns false when the token does not
    /// hold it or the call fails. A child started afterwards inherits the new state.
    /// </summary>
    public static bool SetOwnPrivilege(string name, bool enable)
    {
        if (!LookupPrivilegeValueW(null, name, out Luid luid))
        {
            return false;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
        {
            return false;
        }

        try
        {
            var state = new TokenPrivilege1 { Count = 1, Luid = luid, Attributes = enable ? SePrivilegeEnabled : 0 };
            bool adjusted = AdjustTokenPrivileges(token, false, ref state, 0, nint.Zero, nint.Zero);
            return adjusted && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    public static (bool Held, bool Enabled) PrivilegeState(int processId, string name)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
        {
            return (false, false);
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token))
            {
                return (false, false);
            }

            try
            {
                return ReadPrivilege(token, name);
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>
    /// The System.AppUserModel.ID of a shortcut, read through the shell property store, or null when it has none. Throws
    /// <see cref="Win32Exception"/> or <see cref="COMException"/> when the property store cannot be opened or read.
    /// </summary>
    public static string? ShortcutAppUserModelId(string shortcutPath)
    {
        Guid iid = PropertyStoreIid;
        int hr = SHGetPropertyStoreFromParsingName(shortcutPath, nint.Zero, 0, ref iid, out IPropertyStore? store);
        if (hr < 0 || store is null)
        {
            throw new COMException("SHGetPropertyStoreFromParsingName failed for " + shortcutPath, hr);
        }

        try
        {
            var key = new PropertyKey { FormatId = AppUserModelFormat, PropertyId = 5 };
            hr = store.GetValue(ref key, out PropVariant value);
            if (hr < 0)
            {
                throw new COMException("IPropertyStore.GetValue failed for " + shortcutPath, hr);
            }

            try
            {
                if (value.Type != VtLpwstr)
                {
                    return null;
                }

                int convert = PropVariantToStringAlloc(ref value, out nint text);
                if (convert < 0 || text == nint.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(text);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// What the ACL of a folder says, for the checks that read it. Entries are by SID. <see cref="Sddl"/> has the owner and
    /// the DACL, and is what the artifact records.
    /// </summary>
    public sealed record FolderAcl(
        string Sddl,
        SecurityIdentifier? Owner,
        bool InheritanceProtected,
        IReadOnlyList<AclEntry> Entries);

    public sealed record AclEntry(SecurityIdentifier Sid, bool Allow, FileSystemRights Rights, bool Inherited, bool InheritOnly);

    public static FolderAcl ReadAcl(string folder)
    {
        DirectorySecurity security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var entries = new List<AclEntry>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            entries.Add(new AclEntry(
                (SecurityIdentifier)rule.IdentityReference,
                rule.AccessControlType == AccessControlType.Allow,
                rule.FileSystemRights,
                rule.IsInherited,
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        }

        return new FolderAcl(
            security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access),
            security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
            security.AreAccessRulesProtected,
            entries);
    }

    /// <summary>
    /// Allow entries that apply to the folder itself, for Users, Authenticated Users, or Everyone, and grant a write right.
    /// Empty means standard users cannot write the folder through its own ACL (an inherit-only entry does not apply to it).
    /// </summary>
    public static IReadOnlyList<string> UserWriteEntries(FolderAcl acl) =>
        acl.Entries
            .Where(entry => entry.Allow && !entry.InheritOnly && (entry.Rights & WriteMask) != 0 && ShouldNotWrite.Contains(entry.Sid))
            .Select(entry => entry.Sid.Value + " " + entry.Rights)
            .ToList();

    /// <summary>
    /// What is wrong with a protected root of the elevated helper, or an empty list. The rule (update-elevation.md HP1): the
    /// owner is Administrators, inheritance is off, and the only entries are allow entries of SYSTEM and Administrators with
    /// full control, both present.
    /// </summary>
    public static IReadOnlyList<string> ProtectedAclProblems(FolderAcl acl)
    {
        var problems = new List<string>();
        if (acl.Owner is null || !acl.Owner.Equals(Administrators))
        {
            problems.Add("owner is " + (acl.Owner?.Value ?? "unknown") + ", expected " + Administrators.Value);
        }

        if (!acl.InheritanceProtected)
        {
            problems.Add("the DACL still inherits from the parent");
        }

        foreach (AclEntry entry in acl.Entries)
        {
            if (!entry.Allow)
            {
                problems.Add("deny entry for " + entry.Sid.Value);
            }
            else if (!entry.Sid.Equals(Administrators) && !entry.Sid.Equals(LocalSystem))
            {
                problems.Add("allow entry for " + entry.Sid.Value + " (" + entry.Rights + ")");
            }
            else if ((entry.Rights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
            {
                problems.Add("entry for " + entry.Sid.Value + " is not full control (" + entry.Rights + ")");
            }
        }

        if (!acl.Entries.Any(entry => entry.Allow && entry.Sid.Equals(Administrators)))
        {
            problems.Add("no allow entry for Administrators");
        }

        if (!acl.Entries.Any(entry => entry.Allow && entry.Sid.Equals(LocalSystem)))
        {
            problems.Add("no allow entry for SYSTEM");
        }

        return problems;
    }

    /// <summary>The text of <c>icacls &lt;folder&gt;</c> (a read), or the reason it could not run.</summary>
    public static string IcaclsText(string folder)
    {
        var info = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Default,
        };
        info.ArgumentList.Add(folder);
        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("icacls did not start");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return "icacls did not finish within 30 s";
            }

            return output.Result.TrimEnd() + (error.Result.Length > 0 ? Environment.NewLine + error.Result.TrimEnd() : string.Empty);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return "icacls could not run: " + ex.Message;
        }
    }

    private static int ReadIntegrity(nint token)
    {
        try
        {
            _ = GetTokenInformation(token, TokenIntegrityLevelClass, nint.Zero, 0, out int length);
            if (length <= 0)
            {
                return -1;
            }

            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, length, out _))
                {
                    return -1;
                }

                // TOKEN_MANDATORY_LABEL starts with the SID pointer. The integrity RID is the last sub-authority.
                nint sid = Marshal.ReadIntPtr(buffer);
                int count = Marshal.ReadByte(sid, SidSubAuthorityCountOffset);
                return count == 0 ? -1 : Marshal.ReadInt32(sid, SidSubAuthorityOffset + (4 * (count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static (bool Held, bool Enabled) ReadPrivilege(nint token, string name)
    {
        if (!LookupPrivilegeValueW(null, name, out Luid wanted))
        {
            return (false, false);
        }

        _ = GetTokenInformation(token, TokenPrivilegesClass, nint.Zero, 0, out int length);
        if (length <= 0)
        {
            return (false, false);
        }

        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, length, out _))
            {
                return (false, false);
            }

            int count = Marshal.ReadInt32(buffer);
            for (int index = 0; index < count; index++)
            {
                int offset = PrivilegeCountSize + (index * LuidAndAttributesSize);
                uint low = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                int high = Marshal.ReadInt32(buffer, offset + 4);
                uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                if (low == wanted.LowPart && high == wanted.HighPart)
                {
                    return (true, (attributes & SePrivilegeEnabled) != 0);
                }
            }

            return (false, false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>TOKEN_PRIVILEGES with one entry. Every field is 4-byte aligned, so it matches Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege1
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT on a 64-bit process: the type at 0, the value union at 8, 24 bytes in all.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public nint Value;
    }

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

#pragma warning disable SYSLIB1054 // A few read-only calls do not justify LibraryImport plumbing.
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivilege1 newState,
        int bufferLength,
        nint previousState,
        nint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path,
        nint bindContext,
        int flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out nint path);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToStringAlloc(ref PropVariant value, out nint text);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
#pragma warning restore SYSLIB1054
}
