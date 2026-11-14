using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Session scope of the running-process check (docs/failure-modes/update-verifier.md, section S9 to S11).
// The process list is fed by a fake source, but every process it names is a real copy of cmd.exe.
[TestClass]
public sealed class ProcessSessionTests
{
    private const string ToolName = "BwcRegedit";

    [TestMethod]
    public void Other_Session_Process_Ignored()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        using var running = StartCopyFrom(installDir);
        var source = new FixedSource(ToolName, new ProcessEntry(running.Id, ProcessScan.CurrentSessionId() + 1));

        var found = ProcessScan.RunningFrom(installDir, new HashSet<int>(), source, ProcessScan.CurrentSessionId());

        Assert.AreEqual(0, found.Count, "a process of another session does not block the install");
        Kill(running);
    }

    [TestMethod]
    public void Same_Session_Process_Counted()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        using var running = StartCopyFrom(installDir);
        var source = new FixedSource(ToolName, new ProcessEntry(running.Id, ProcessScan.CurrentSessionId()));

        var found = ProcessScan.RunningFrom(installDir, new HashSet<int>(), source, ProcessScan.CurrentSessionId());

        Assert.AreEqual(1, found.Count, "a process of this session that runs from the install folder blocks the install");
        Assert.AreEqual(running.Id, found[0].Pid);
        Kill(running);
    }

    [TestMethod]
    public void Unknown_Session_Counts_As_Running()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        using var running = StartCopyFrom(installDir);
        var source = new FixedSource(ToolName, new ProcessEntry(running.Id, null));

        var found = ProcessScan.RunningFrom(installDir, new HashSet<int>(), source, ProcessScan.CurrentSessionId());

        Assert.AreEqual(1, found.Count, "a process whose session cannot be read fails closed");
        Kill(running);
    }

    private static Process StartCopyFrom(string installDir)
    {
        var copy = Path.Combine(installDir, ToolName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), copy);
        var psi = new ProcessStartInfo(copy)
        {
            Arguments = "/c ping -n 60 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + copy);
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

    /// <summary>Names one tool and returns the given entries for it, and nothing for any other name.</summary>
    private sealed class FixedSource(string name, params ProcessEntry[] entries) : IProcessSource
    {
        public IReadOnlyList<ProcessEntry> ByName(string processName) =>
            string.Equals(processName, name, StringComparison.Ordinal) ? entries : [];
    }
}
