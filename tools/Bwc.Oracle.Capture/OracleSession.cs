using System.Runtime.ExceptionServices;

namespace Bwc.Oracle.Capture;

// Wraps every command that starts classic regedit. The whole session runs under the GUI mutex, because the
// Regedit applet state belongs to the desktop user and another worker's regedit may write it on exit.
// Inside the lock: snapshot the applet key tree, delete any stale BWC-Oracle tree, run the body, then always
// delete BWC-Oracle and restore the applet key tree exactly, even when the body failed.
internal static class OracleSession
{
    private const int RequiredDpi = 96;

    internal static void Run(string name, Action body)
    {
        GuiLock.Run(() =>
        {
            AppletSnapshot before = AppletSnapshot.Capture();
            Console.WriteLine($"{name}: applet state before: {before.Summary()}");

            Exception? failure = null;
            try
            {
                RegistryGuard.DeleteOracleTree();
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                RegistryGuard.DeleteOracleTree();
                Console.WriteLine($"{name}: HKCU\\Software\\BWC-Oracle removed");
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            try
            {
                before.Restore();
                Console.WriteLine($"{name}: applet state restored: {before.Summary()}");
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        });
    }

    // Sets this process to per-monitor DPI awareness and returns the system DPI. UI captures measure at 96 DPI.
    // A desktop at another scale is refused unless --allow-dpi was given, and the DPI is then recorded in every output.
    internal static uint RequireDpi(bool allowDpi)
    {
        Win32.SetProcessDpiAwarenessContext(Msg.DpiAwarePerMonitorV2);
        uint systemDpi = Win32.GetDpiForSystem();
        if (systemDpi != RequiredDpi && !allowDpi)
        {
            throw new InvalidOperationException(
                $"refused: system DPI is {systemDpi}, but classic-regedit parity is measured at {RequiredDpi} DPI (100%). " +
                "Set the display scale to 100%, or pass --allow-dpi to record values at this DPI.");
        }

        return systemDpi;
    }
}
