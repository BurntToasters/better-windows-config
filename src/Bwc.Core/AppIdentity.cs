using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bwc.Core;

public static partial class AppIdentity
{
    public const string HubAppUserModelId = "BetterWindowsConfig.Hub";
    public const string RegeditAppUserModelId = "BetterWindowsConfig.Regedit";
    public const string GpeditAppUserModelId = "BetterWindowsConfig.Gpedit";

    /// <summary>Sets the explicit AppUserModelID of this process. Call before the first window is created.</summary>
    public static void SetCurrentProcessAppUserModelId(string appUserModelId)
    {
        int hr = SetCurrentProcessExplicitAppUserModelID(appUserModelId);
        if (hr >= 0)
        {
            return;
        }

#if DEBUG
        throw new COMException($"SetCurrentProcessExplicitAppUserModelID('{appUserModelId}') failed.", hr);
#else
        Trace.TraceWarning($"SetCurrentProcessExplicitAppUserModelID('{appUserModelId}') failed: 0x{hr:X8}");
#endif
    }

    /// <summary>The explicit AppUserModelID of this process, or null when none is set or it cannot be read.</summary>
    public static string? GetCurrentProcessAppUserModelId()
    {
        int hr = GetCurrentProcessExplicitAppUserModelID(out nint text);
        if (hr < 0 || text == 0)
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

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    private static partial int GetCurrentProcessExplicitAppUserModelID(out nint appId);
}
