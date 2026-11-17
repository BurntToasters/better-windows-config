using System.Runtime.InteropServices;

namespace Bwc.Update;

/// <summary>
/// Enables one privilege of this process's token for a scope, and puts it back as it was. The elevated
/// <c>--close-running</c> uses it for <c>SeDebugPrivilege</c> so it can read the image path of a process that belongs to
/// another user (update-elevation.md, HC1). A token that does not hold the privilege is not an error: nothing is enabled.
/// </summary>
public sealed class PrivilegeScope : IDisposable
{
    public const string DebugPrivilege = "SeDebugPrivilege";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;
    private const int TokenPrivilegesClass = 3;

    private readonly Luid _luid;
    private bool _restore;

    private PrivilegeScope(Luid luid)
    {
        _luid = luid;
        _restore = true;
    }

    /// <summary>
    /// Enables <paramref name="name"/> when the token holds it and it is not enabled already. Returns null when the token
    /// does not hold it, when it is already enabled (there is nothing to put back), or when the call fails.
    /// </summary>
    public static PrivilegeScope? TryEnable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!LookupPrivilegeValueW(null, name, out var luid))
        {
            return null;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            if (!TryGetState(token, luid, out var held, out var enabled) || !held || enabled)
            {
                return null;
            }

            return SetState(token, luid, enable: true) ? new PrivilegeScope(luid) : null;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>True when the token holds the privilege and it is enabled. False for anything else.</summary>
    public static bool IsEnabled(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!LookupPrivilegeValueW(null, name, out var luid) || !OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            return TryGetState(token, luid, out var held, out var enabled) && held && enabled;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>True when the token holds the privilege, enabled or not.</summary>
    public static bool IsHeld(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!LookupPrivilegeValueW(null, name, out var luid) || !OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            return TryGetState(token, luid, out var held, out _) && held;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    public void Dispose()
    {
        if (!_restore)
        {
            return;
        }

        _restore = false;
        if (OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            try
            {
                _ = SetState(token, _luid, enable: false);
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
    }

    private static bool SetState(IntPtr token, Luid luid, bool enable)
    {
        var state = new TokenPrivilege1
        {
            Count = 1,
            Luid = luid,
            Attributes = enable ? SePrivilegeEnabled : 0,
        };
        if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
        {
            return false;
        }

        // The call succeeds with a warning when the privilege is not held at all.
        return Marshal.GetLastPInvokeError() != ErrorNotAllAssigned;
    }

    private static bool TryGetState(IntPtr token, Luid luid, out bool held, out bool enabled)
    {
        held = false;
        enabled = false;
        _ = GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, length, out _))
            {
                return false;
            }

            // TOKEN_PRIVILEGES: a 4 byte count, then LUID_AND_ATTRIBUTES entries of 12 bytes (low, high, attributes).
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var offset = 4 + (i * 12);
                var low = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                var high = Marshal.ReadInt32(buffer, offset + 4);
                if (low == luid.Low && high == luid.High)
                {
                    held = true;
                    enabled = (unchecked((uint)Marshal.ReadInt32(buffer, offset + 8)) & SePrivilegeEnabled) != 0;
                    return true;
                }
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege1
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps these signatures plain; the structures are marshalled directly.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivilege1 newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
#pragma warning restore SYSLIB1054
}
