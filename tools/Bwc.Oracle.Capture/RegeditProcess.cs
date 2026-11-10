using System.Diagnostics;

namespace Bwc.Oracle.Capture;

// Launches classic regedit.exe unelevated. regedit requests highestAvailable, so the
// __COMPAT_LAYER=RunAsInvoker shim is set on the child only, and never on this process.
internal static class RegeditProcess
{
    internal const string RegeditPath = @"C:\Windows\regedit.exe";

    internal static Process Start(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(RegeditPath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        Process process = Process.Start(psi) ?? throw new InvalidOperationException("regedit.exe did not start.");
        Console.WriteLine($"regedit: started pid {process.Id}: regedit {string.Join(' ', arguments)}");
        return process;
    }

    // Runs a non-interactive regedit command (/e, /a) and returns its exit code.
    // On timeout only the process started here is killed, then the call throws.
    internal static int RunToExit(IEnumerable<string> arguments, TimeSpan timeout)
    {
        using Process process = Start(arguments);
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }

            throw new TimeoutException($"regedit pid {process.Id} did not exit within {timeout.TotalSeconds}s; killed it.");
        }

        return process.ExitCode;
    }
}
