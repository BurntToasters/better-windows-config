using System.Runtime.InteropServices;

namespace Bwc.Spike.UI;

/// <summary>Process-level probes used by both heads: token elevation and AppUserModelID.</summary>
public static class ProcessProbe
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>True when the current process token has TokenElevation != 0 (full admin token).</summary>
    public static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        try
        {
            if (!GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation failed");
            return elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Sets the explicit AppUserModelID for this process. Must run before the first window.</summary>
    public static void SetAppUserModelId(string appId)
    {
        int hr = SetCurrentProcessExplicitAppUserModelID(appId);
        if (hr != 0)
            throw new System.Runtime.InteropServices.COMException("SetCurrentProcessExplicitAppUserModelID failed", hr);
    }
}
