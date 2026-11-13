using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Bwc.Registry.Privileges;

/// <summary>
/// Enables SeBackupPrivilege and SeRestorePrivilege for the current process token and restores the
/// prior state when disposed. Only the hive methods in <see cref="RegistryWriter"/> use it.
/// </summary>
public sealed class PrivilegeScope : IDisposable
{
    public const string BackupPrivilege = "SeBackupPrivilege";
    public const string RestorePrivilege = "SeRestorePrivilege";

    private const int ErrorInsufficientBuffer = 122;

    private readonly SafeFileHandle _token;
    private readonly LUID _backupLuid;
    private readonly LUID _restoreLuid;
    private readonly bool _disableBackup;
    private readonly bool _disableRestore;
    private int _disposed;

    private PrivilegeScope(
        SafeFileHandle token,
        LUID backupLuid,
        bool disableBackup,
        LUID restoreLuid,
        bool disableRestore)
    {
        _token = token;
        _backupLuid = backupLuid;
        _disableBackup = disableBackup;
        _restoreLuid = restoreLuid;
        _disableRestore = disableRestore;
    }

    /// <summary>
    /// Returns true when the named privilege is present in the current process token, whether it is
    /// enabled or disabled. A missing privilege or a failed token query returns false.
    /// </summary>
    public static bool IsAvailable(string privilegeName)
    {
        if (string.IsNullOrWhiteSpace(privilegeName))
        {
            throw new ArgumentException("A privilege name is required.", nameof(privilegeName));
        }

        if (!TryOpenToken(TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle? token, out _))
        {
            return false;
        }

        using SafeFileHandle openedToken = token!;

        if (!TryLookupPrivilege(privilegeName, out LUID luid, out _) ||
            !TryReadTokenPrivileges(openedToken, out byte[] privileges, out _))
        {
            return false;
        }

        FindPrivilege(privileges, luid, out bool present, out _);
        return present;
    }

    /// <summary>
    /// Enables both hive privileges. On failure the method returns false, leaves no privilege
    /// enabled that this call enabled, and describes the problem in <paramref name="error"/>.
    /// </summary>
    public static bool TryEnable(out PrivilegeScope? scope, out string? error)
    {
        scope = null;
        error = null;

        if (!TryOpenToken(
                TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES,
                out SafeFileHandle? token,
                out error))
        {
            return false;
        }

        try
        {
            if (!TryLookupPrivilege(BackupPrivilege, out LUID backupLuid, out error) ||
                !TryLookupPrivilege(RestorePrivilege, out LUID restoreLuid, out error))
            {
                return false;
            }

            if (!TryReadTokenPrivileges(token!, out byte[] privileges, out error))
            {
                return false;
            }

            FindPrivilege(privileges, backupLuid, out bool backupPresent, out bool backupEnabled);
            FindPrivilege(privileges, restoreLuid, out bool restorePresent, out bool restoreEnabled);

            if (!backupPresent || !restorePresent)
            {
                error = BuildMissingPrivilegeMessage(backupPresent, restorePresent);
                return false;
            }

            bool enableBackup = !backupEnabled;
            bool enableRestore = !restoreEnabled;

            if (!TryAdjustBothPrivileges(token!, backupLuid, restoreLuid, out int adjustError))
            {
                if (enableBackup)
                {
                    DisablePrivilege(token!, backupLuid);
                }

                if (enableRestore)
                {
                    DisablePrivilege(token!, restoreLuid);
                }

                error = $"AdjustTokenPrivileges failed with Win32 error {adjustError}.";
                return false;
            }

            scope = new PrivilegeScope(token!, backupLuid, enableBackup, restoreLuid, enableRestore);
            token = null;
            return true;
        }
        finally
        {
            token?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_disableBackup)
            {
                DisablePrivilege(_token, _backupLuid);
            }

            if (_disableRestore)
            {
                DisablePrivilege(_token, _restoreLuid);
            }
        }
        catch
        {
            // Dispose must never throw. A failed restore leaves the process token as it was.
        }

        try
        {
            _token.Dispose();
        }
        catch
        {
            // A failed handle close is not actionable at this point.
        }
    }

    private static unsafe bool TryOpenToken(
        TOKEN_ACCESS_MASK access,
        out SafeFileHandle? token,
        out string? error)
    {
        token = null;
        error = null;

        HANDLE tokenHandle = default;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), access, &tokenHandle))
        {
            int win32Error = Marshal.GetLastWin32Error();
            error = $"OpenProcessToken failed with Win32 error {win32Error}.";
            return false;
        }

        token = new SafeFileHandle((nint)tokenHandle.Value, ownsHandle: true);
        return true;
    }

    private static bool TryLookupPrivilege(string privilegeName, out LUID luid, out string? error)
    {
        if (!PInvoke.LookupPrivilegeValue(null, privilegeName, out luid))
        {
            int win32Error = Marshal.GetLastWin32Error();
            error = $"The privilege '{privilegeName}' could not be looked up (Win32 error {win32Error}).";
            return false;
        }

        error = null;
        return true;
    }

    private static unsafe bool TryReadTokenPrivileges(
        SafeFileHandle token,
        out byte[] privileges,
        out string? error)
    {
        privileges = Array.Empty<byte>();
        error = null;

        if (!PInvoke.GetTokenInformation(
                token,
                TOKEN_INFORMATION_CLASS.TokenPrivileges,
                null,
                0,
                out uint requiredLength))
        {
            int win32Error = Marshal.GetLastWin32Error();
            if (win32Error != ErrorInsufficientBuffer)
            {
                error = $"GetTokenInformation failed with Win32 error {win32Error}.";
                return false;
            }
        }

        if (requiredLength < sizeof(TOKEN_PRIVILEGES))
        {
            error = "GetTokenInformation returned an invalid token privilege buffer length.";
            return false;
        }

        byte[] buffer = new byte[requiredLength];
        fixed (byte* bufferPointer = buffer)
        {
            if (!PInvoke.GetTokenInformation(
                    token,
                    TOKEN_INFORMATION_CLASS.TokenPrivileges,
                    bufferPointer,
                    (uint)buffer.Length,
                    out _))
            {
                int win32Error = Marshal.GetLastWin32Error();
                error = $"GetTokenInformation failed with Win32 error {win32Error}.";
                return false;
            }
        }

        privileges = buffer;
        return true;
    }

    private static unsafe void FindPrivilege(
        byte[] privileges,
        LUID luid,
        out bool present,
        out bool enabled)
    {
        present = false;
        enabled = false;

        if (privileges.Length < sizeof(TOKEN_PRIVILEGES))
        {
            return;
        }

        fixed (byte* bufferPointer = privileges)
        {
            TOKEN_PRIVILEGES* tokenPrivileges = (TOKEN_PRIVILEGES*)bufferPointer;
            for (uint index = 0; index < tokenPrivileges->PrivilegeCount; index += 1)
            {
                ref LUID_AND_ATTRIBUTES entry = ref tokenPrivileges->Privileges[(int)index];
                if (entry.Luid.LowPart == luid.LowPart && entry.Luid.HighPart == luid.HighPart)
                {
                    present = true;
                    enabled = (entry.Attributes & TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED) ==
                        TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;
                    return;
                }
            }
        }
    }

    private static unsafe bool TryAdjustBothPrivileges(
        SafeFileHandle token,
        LUID backupLuid,
        LUID restoreLuid,
        out int win32Error)
    {
        byte[] stateBuffer = new byte[TOKEN_PRIVILEGES.SizeOf(2)];
        fixed (byte* statePointer = stateBuffer)
        {
            TOKEN_PRIVILEGES* state = (TOKEN_PRIVILEGES*)statePointer;
            state->PrivilegeCount = 2;
            state->Privileges[0].Luid = backupLuid;
            state->Privileges[0].Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;
            state->Privileges[1].Luid = restoreLuid;
            state->Privileges[1].Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;

            BOOL adjusted = PInvoke.AdjustTokenPrivileges(token, false, state, default);
            win32Error = Marshal.GetLastWin32Error();
            return adjusted && win32Error == 0;
        }
    }

    private static unsafe void DisablePrivilege(SafeFileHandle token, LUID luid)
    {
        byte[] stateBuffer = new byte[TOKEN_PRIVILEGES.SizeOf(1)];
        fixed (byte* statePointer = stateBuffer)
        {
            TOKEN_PRIVILEGES* state = (TOKEN_PRIVILEGES*)statePointer;
            state->PrivilegeCount = 1;
            state->Privileges[0].Luid = luid;
            state->Privileges[0].Attributes = default;

            _ = PInvoke.AdjustTokenPrivileges(token, false, state, default);
        }
    }

    private static string BuildMissingPrivilegeMessage(bool backupPresent, bool restorePresent)
    {
        if (!backupPresent && !restorePresent)
        {
            return $"{BackupPrivilege} and {RestorePrivilege} are not available in the current process token.";
        }

        string missing = backupPresent ? RestorePrivilege : BackupPrivilege;
        return $"{missing} is not available in the current process token.";
    }
}
