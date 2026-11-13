using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The installer-facing process commands (docs/contracts/update-cli.md, section P). Only copies of
// cmd.exe run here, started without a window, so nothing with a window is ever closed.
[TestClass]
public sealed class ProcessCommandsTests
{
    [TestMethod]
    public void CloseRunning_None_Running_Exits_Zero()
    {
        using var dir = new TempDir();

        var code = ProcessCommands.CloseRunning(dir.Sub("install"), [], 1, TextWriter.Null);

        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public void CloseRunning_Still_Running_Exits_Three()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        using var running = StartLongRunningTool(installDir);
        var output = new StringWriter();
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [], 1, output);

            Assert.AreEqual(3, code, output.ToString());
            StringAssert.Contains(output.ToString(), running.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "the pid is printed");
            StringAssert.Contains(output.ToString(), "BwcRegedit", "the name is printed");
        }
        finally
        {
            Kill(running);
        }
    }

    [TestMethod]
    public void CloseRunning_Excludes_Own_And_Listed_Pids()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        using var running = StartLongRunningTool(installDir);
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [running.Id], 1, TextWriter.Null);

            Assert.AreEqual(0, code, "a listed pid is never signaled and never counted");
        }
        finally
        {
            Kill(running);
        }
    }

    [TestMethod]
    public void WaitPid_Missing_Exits_Zero()
    {
        Assert.AreEqual(0, ProcessCommands.WaitForPid(2147483646, 1, TextWriter.Null));
    }

    [TestMethod]
    public void WaitPid_Live_Times_Out_Exits_Three()
    {
        using var dir = new TempDir();
        using var running = StartLongRunningTool(dir.Sub("install"));
        try
        {
            var code = ProcessCommands.WaitForPid(running.Id, 1, TextWriter.Null);

            Assert.AreEqual(3, code, "a process still running at the timeout is reported");
        }
        finally
        {
            Kill(running);
        }
    }

    [TestMethod]
    public void WaitPid_Exits_In_Time()
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c ping -n 2 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start cmd");

        var code = ProcessCommands.WaitForPid(process.Id, 30, TextWriter.Null);

        Assert.AreEqual(0, code);
        Assert.IsTrue(process.HasExited);
    }

    private static Process StartLongRunningTool(string installDir)
    {
        var copy = Path.Combine(installDir, "BwcRegedit.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), copy);
        var psi = new ProcessStartInfo(copy)
        {
            Arguments = "/c ping -n 60 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the tool copy");
        Thread.Sleep(300);
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }
}
