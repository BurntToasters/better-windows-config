using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bwc.Core;

/// <summary>
/// Thin wrappers that read the integrity level of this process and of the process at the other end of a pipe. Every
/// failure returns <see cref="IntegrityLevel.Unknown"/>, which the single-instance rule refuses.
/// </summary>
internal static partial class IntegrityNative
{
    private const uint TokenQueryAccess = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int TokenIntegrityLevelClass = 25;

    public static IntegrityLevel ReadCurrent()
    {
        return ReadProcessLevel(GetCurrentProcess(), ownsHandle: false);
    }

    /// <summary>
    /// The level of the client connected to a server pipe handle. The client process id comes from the pipe, then
    /// the process is opened and its token read. A client that already exited, or whose process or token cannot be
    /// opened, is unknown.
    /// </summary>
    public static IntegrityLevel ReadPipeClient(SafePipeHandle serverPipe)
    {
        try
        {
            if (serverPipe.IsInvalid || serverPipe.IsClosed)
            {
                return IntegrityLevel.Unknown;
            }

            if (!GetNamedPipeClientProcessId(serverPipe, out uint processId) || processId == 0)
            {
                return IntegrityLevel.Unknown;
            }

            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            return ReadProcessLevel(process, ownsHandle: true);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            SafeTrace.WriteLine("SingleInstance could not read the client process: " + ex.Message);
            return IntegrityLevel.Unknown;
        }
    }

    private static IntegrityLevel ReadProcessLevel(IntPtr process, bool ownsHandle)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQueryAccess, out token))
            {
                return IntegrityLevel.Unknown;
            }

            _ = GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return IntegrityLevel.Unknown;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, length, out _))
            {
                return IntegrityLevel.Unknown;
            }

            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID.
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            byte subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0)
            {
                return IntegrityLevel.Unknown;
            }

            int offset = 8 + ((subAuthorityCount - 1) * 4);
            return IntegrityPolicy.FromRid(unchecked((uint)Marshal.ReadInt32(sid, offset)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SafeTrace.WriteLine("SingleInstance could not read the integrity level: " + ex.Message);
            return IntegrityLevel.Unknown;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                _ = CloseHandle(token);
            }

            if (ownsHandle && process != IntPtr.Zero)
            {
                _ = CloseHandle(process);
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
