using System.Runtime.InteropServices;

namespace Bwc.Core;

/// <summary>
/// Whether this process runs elevated or at high integrity. Copied from the updater's probe, which is not
/// referenced from here. Unknown is treated as elevated (fail closed).
/// </summary>
internal static class ElevationProbe
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const uint SecurityMandatoryHighRid = 0x3000;

    private static readonly Lazy<bool> Cached = new(Compute);

    public static bool IsElevatedOrHighIntegrity()
    {
        return Cached.Value;
    }

    /// <summary>
    /// The pure decision. A flag that could not be read is null; any unknown, or a token that could not be opened,
    /// counts as elevated.
    /// </summary>
    public static bool Classify(bool tokenOpened, bool? elevated, bool? highIntegrity)
    {
        if (!tokenOpened || elevated is null || highIntegrity is null)
        {
            return true;
        }

        return elevated.Value || highIntegrity.Value;
    }

    private static bool Compute()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            {
                return Classify(false, null, null);
            }

            try
            {
                return Classify(true, ReadElevated(token), ReadHighIntegrity(token));
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return true;
        }
    }

    private static bool? ReadElevated(IntPtr token)
    {
        if (!GetTokenInformation(token, TokenElevationClass, out uint elevated, sizeof(uint), out _))
        {
            return null;
        }

        return elevated != 0;
    }

    private static bool? ReadHighIntegrity(IntPtr token)
    {
        _ = GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out int length);
        if (length <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, length, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID.
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return null;
            }

            var subAuthorities = Marshal.ReadByte(sid, 1);
            if (subAuthorities == 0)
            {
                return null;
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
