using System.Runtime.InteropServices;

namespace Bwc.Update;

/// <summary>
/// Whether this process runs elevated or at high integrity. The helper refuses to apply an update in that
/// state: %TEMP% staging is writable by medium-integrity processes, so an elevated helper could load a
/// planted file. Unknown is treated as elevated (fail closed).
/// </summary>
public static class ProcessIdentity
{
#if BWC_E2E
    /// <summary>"1" makes the E2E helper behave as elevated. Compiled only into the E2E flavor.</summary>
    public const string ForceElevatedVariable = "BWC_E2E_UPDATE_FORCE_ELEVATED";

#endif
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const uint SecurityMandatoryHighRid = 0x3000;

    public static bool IsElevatedOrHighIntegrity()
    {
#if BWC_E2E
        // E2E flavor only: lets a test drive the elevated refusals of the real helper without a real elevated token. It
        // changes what the helper believes, never what it can do (HB4).
        if (Environment.GetEnvironmentVariable(ForceElevatedVariable) == "1")
        {
            return true;
        }
#endif
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return true;
        }

        try
        {
            return IsElevated(token) || IsHighIntegrity(token);
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static bool IsElevated(IntPtr token)
    {
        if (!GetTokenInformation(token, TokenElevationClass, out uint elevated, sizeof(uint), out _))
        {
            return true;
        }

        return elevated != 0;
    }

    private static bool IsHighIntegrity(IntPtr token)
    {
        _ = GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out int length);
        if (length <= 0)
        {
            return true;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, length, out _))
            {
                return true;
            }

            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID.
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return true;
            }

            var subAuthorities = Marshal.ReadByte(sid, 1);
            if (subAuthorities == 0)
            {
                return true;
            }

            var rid = unchecked((uint)Marshal.ReadInt32(sid, 8 + ((subAuthorities - 1) * 4)));
            return rid >= SecurityMandatoryHighRid;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

#pragma warning disable SYSLIB1054 // DllImport keeps these signatures plain; the token buffers are marshalled directly.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out uint tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
#pragma warning restore SYSLIB1054
}
