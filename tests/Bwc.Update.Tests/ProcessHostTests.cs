using System.Diagnostics;
using Bwc.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Real-process cases for the Windows host. Only harmless processes are started (copies of cmd.exe).
[TestClass]
public sealed class ProcessHostTests
{
    [TestMethod]
    public void Missing_Pid_Treated_As_Exited()
    {
        var live = new WindowsUpdateProcessHost().LiveProcesses([2147483646], Path.GetTempPath());

        Assert.AreEqual(0, live.Count);
    }

    [TestMethod]
    public void Scan_Counts_Tool_Under_Install_Dir_As_Running()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var otherDir = dir.Sub("other");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var inInstall = Path.Combine(installDir, "BwcRegedit.exe");
        var elsewhere = Path.Combine(otherDir, "BwcRegedit.exe");
        File.Copy(cmd, inInstall);
        File.Copy(cmd, elsewhere);

        using var running = StartLongRunning(inInstall);
        using var other = StartLongRunning(elsewhere);
        try
        {
            var live = new WindowsUpdateProcessHost().LiveProcesses([], installDir);

            Assert.IsTrue(live.Contains(running.Id), "a tool running from the install folder blocks the install");
            Assert.IsFalse(live.Contains(other.Id), "a copy running from another folder does not block the install");
        }
        finally
        {
            Kill(running);
            Kill(other);
        }
    }

    // HC5: the update gate counts every catalog tool that runs from the install folder, and none that runs elsewhere.
    [TestMethod]
    public void Scan_Counts_Every_Catalog_Tool_Under_Install_Dir()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var otherDir = dir.Sub("other");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var started = new List<(string Exe, Process Inside, Process Outside)>();
        try
        {
            foreach (var tool in ToolCatalog.Tools)
            {
                var inInstall = Path.Combine(installDir, tool.ExeName);
                var elsewhere = Path.Combine(otherDir, tool.ExeName);
                File.Copy(cmd, inInstall);
                File.Copy(cmd, elsewhere);
                started.Add((tool.ExeName, StartLongRunning(inInstall), StartLongRunning(elsewhere)));
            }

            var live = new WindowsUpdateProcessHost().LiveProcesses([], installDir);

            foreach (var (exe, inside, outside) in started)
            {
                Assert.IsTrue(live.Contains(inside.Id), exe + " running from the install folder blocks the install");
                Assert.IsFalse(live.Contains(outside.Id), exe + " running from another folder does not block the install");
            }
        }
        finally
        {
            foreach (var (_, inside, outside) in started)
            {
                Kill(inside);
                Kill(outside);
            }
        }
    }

    private static Process StartLongRunning(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = "/c ping -n 60 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
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
