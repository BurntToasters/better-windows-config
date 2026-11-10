using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bwc.Core;

public static partial class AppIdentity
{
    public const string HubAppUserModelId = "BetterWindowsConfig.Hub";
    public const string RegeditAppUserModelId = "BetterWindowsConfig.Regedit";

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

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
