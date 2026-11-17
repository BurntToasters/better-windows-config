using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Bwc.E2E.Hives;

/// <summary>
/// Native calls of the elevated hive test. <see cref="HasHivePrivileges"/> reads the process token and changes nothing.
/// <see cref="UnloadMount"/> enables SeRestorePrivilege and SeBackupPrivilege only around its own RegUnLoadKey call.
/// <see cref="UnloadMount"/> is the only native unload in the E2E project; it runs only in the test's finally block,
/// on HKEY_USERS\BWC-E2E-&lt;run-id&gt;, after the test's own managed handles are closed (hives.md section 14).
/// </summary>
internal static class HiveMountNative
{
    internal const string BackupPrivilege = "SeBackupPrivilege";
    internal const string RestorePrivilege = "SeRestorePrivilege";

    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const int TokenPrivilegesClass = 3;
    private const int LuidAndAttributesSize = 12;
    private const int PrivilegeCountSize = 4;
    private static readonly nint HkeyUsers = new(unchecked((int)0x80000003));

    /// <summary>True when the process is an administrator and both hive privileges are in its token (enabled or not).</summary>
    internal static bool HasHivePrivileges() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) &&
        TokenHasPrivilege(BackupPrivilege) &&
        TokenHasPrivilege(RestorePrivilege);

    /// <summary>
    /// Titles of the visible top-level windows of <paramref name="processId"/> other than <paramref name="main"/>:
    /// a progress window or message box the app still shows. Read only.
    /// </summary>
    internal static IReadOnlyList<string> OtherVisibleWindows(int processId, nint main)
    {
        var titles = new List<string>();
        _ = EnumWindows(
            (hwnd, lParam) =>
            {
                _ = GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == (uint)processId && hwnd != main && IsWindowVisible(hwnd))
                {
                    titles.Add($"0x{hwnd.ToInt64():X} '{Bwc.E2E.Harness.Win32.TitleOf(hwnd)}'");
                }

                return true;
            },
            nint.Zero);
        return titles;
    }

    /// <summary>
    /// Unloads HKEY_USERS\<paramref name="name"/>. Returns the Win32 error, 0 on success. RegUnLoadKey needs
    /// SeRestorePrivilege and SeBackupPrivilege enabled (held is not enough: it returned 1314 in an elevated run), so
    /// both are enabled on this process's token for the call and disabled again after it.
    /// </summary>
    internal static int UnloadMount(string name)
    {
        if (!SetPrivilege(RestorePrivilege, enable: true) || !SetPrivilege(BackupPrivilege, enable: true))
        {
            _ = SetPrivilege(RestorePrivilege, enable: false);
            return ErrorPrivilegeNotHeld;
        }

        try
        {
            return RegUnLoadKeyW(HkeyUsers, name);
        }
        finally
        {
            _ = SetPrivilege(BackupPrivilege, enable: false);
            _ = SetPrivilege(RestorePrivilege, enable: false);
        }
    }

    private static bool SetPrivilege(string name, bool enable)
    {
        if (!LookupPrivilegeValueW(null, name, out Luid luid) ||
            !OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
        {
            return false;
        }

        try
        {
            var state = new TokenPrivilege1
            {
                Count = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = enable ? SePrivilegeEnabled : 0 },
            };
            return AdjustTokenPrivileges(token, false, ref state, 0, nint.Zero, nint.Zero) &&
                Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

#pragma warning disable SYSLIB1054 // A few token and unload calls do not justify LibraryImport plumbing.
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegUnLoadKeyW")]
    private static extern int RegUnLoadKeyW(nint hKey, string lpSubKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivilege1 newState,
        int bufferLength,
        nint previousState,
        nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054

    private static bool TokenHasPrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, name, out Luid wanted))
            {
                return false;
            }

            _ = GetTokenInformation(token, TokenPrivilegesClass, nint.Zero, 0, out int length);
            if (length <= 0)
            {
                return false;
            }

            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, length, out _))
                {
                    return false;
                }

                int count = Marshal.ReadInt32(buffer);
                for (var index = 0; index < count; index++)
                {
                    nint entry = buffer + PrivilegeCountSize + (index * LuidAndAttributesSize);
                    var held = Marshal.PtrToStructure<LuidAndAttributes>(entry);
                    if (held.Luid.LowPart == wanted.LowPart && held.Luid.HighPart == wanted.HighPart)
                    {
                        return true;
                    }
                }

                return false;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>TOKEN_PRIVILEGES with one entry. Every field is 4-byte aligned, so it matches Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege1
    {
        public uint Count;
        public LuidAndAttributes Privilege;
    }
}
