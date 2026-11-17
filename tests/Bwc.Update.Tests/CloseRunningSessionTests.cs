using System.Diagnostics;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// --close-running across sessions (docs/failure-modes/update-elevation.md, section SS). The process list comes from a fake
// source, but every process it names is a real copy of cmd.exe, so the image check is the real one. No window is closed:
// the "post WM_CLOSE" step is a recording callback.
[TestClass]
public sealed class CloseRunningSessionTests
{
    private const string ToolName = "BwcRegedit";
    private const string HubName = "BetterWindowsConfig";

    // SS1
    [TestMethod]
    public void CloseRunning_Other_Session_Fails_Before_Any_Close()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var current = ProcessScan.CurrentSessionId();
        using var other = StartCopyFrom(installDir, ToolName);
        using var here = StartCopyFrom(installDir, HubName);
        var source = new MapSource()
            .Add(ToolName, new ProcessEntry(other.Id, current + 7))
            .Add(HubName, new ProcessEntry(here.Id, current));
        var closed = new List<int>();
        var output = new StringWriter();
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [], 1, output, source, current, closed.Add);

            Assert.AreEqual(ProcessCommands.OtherSessionExitCode, code, output.ToString());
            Assert.AreEqual(4, code);
            Assert.AreEqual(0, closed.Count, "nothing is closed, not even this session's process: no change before the check");
            var expected = string.Create(
                CultureInfo.InvariantCulture,
                $"RunningInOtherSession session={current + 7} {ToolName} pid={other.Id}");
            StringAssert.Contains(output.ToString(), expected);
        }
        finally
        {
            Kill(other);
            Kill(here);
        }
    }

    // SS2
    [TestMethod]
    public void CloseRunning_Other_Session_Other_Folder_Ignored()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var elsewhere = dir.Sub("elsewhere");
        var current = ProcessScan.CurrentSessionId();
        using var other = StartCopyFrom(elsewhere, ToolName);
        var source = new MapSource().Add(ToolName, new ProcessEntry(other.Id, current + 1));
        var closed = new List<int>();
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [], 1, TextWriter.Null, source, current, closed.Add);

            Assert.AreEqual(0, code, "a process of another session from another folder does not block the install");
            Assert.AreEqual(0, closed.Count);
        }
        finally
        {
            Kill(other);
        }
    }

    // SS3
    [TestMethod]
    public void CloseRunning_Other_Session_Excluded_Pid_Ignored()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var current = ProcessScan.CurrentSessionId();
        using var other = StartCopyFrom(installDir, ToolName);
        var source = new MapSource().Add(ToolName, new ProcessEntry(other.Id, current + 1));
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [other.Id], 1, TextWriter.Null, source, current, _ => { });

            Assert.AreEqual(0, code, "a listed pid is left out in every session");
        }
        finally
        {
            Kill(other);
        }
    }

    // SS4
    [TestMethod]
    public void CloseRunning_Current_Session_Still_Closed()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var current = ProcessScan.CurrentSessionId();
        using var here = StartCopyFrom(installDir, ToolName);
        var source = new MapSource().Add(ToolName, new ProcessEntry(here.Id, current));
        var closed = new List<int>();
        try
        {
            // The recording callback stands in for WM_CLOSE: it ends the process, as a window that closes would.
            var code = ProcessCommands.CloseRunning(installDir, [], 10, TextWriter.Null, source, current, pid =>
            {
                closed.Add(pid);
                here.Kill();
            });

            Assert.AreEqual(0, code);
            CollectionAssert.AreEqual(new[] { here.Id }, closed, "closed once, as before");
        }
        finally
        {
            Kill(here);
        }
    }

    // SS5
    [TestMethod]
    public void CloseRunning_Unknown_Session_Counts_As_Current()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        using var here = StartCopyFrom(installDir, ToolName);
        var source = new MapSource().Add(ToolName, new ProcessEntry(here.Id, null));
        var closed = new List<int>();
        var output = new StringWriter();
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [], 1, output, source, ProcessScan.CurrentSessionId(), closed.Add);

            Assert.AreEqual(3, code, "an unknown session is closed and waited for like this session, not reported as another one");
            Assert.AreEqual(1, closed.Count);
            Assert.IsFalse(output.ToString().Contains("RunningInOtherSession", StringComparison.Ordinal));
        }
        finally
        {
            Kill(here);
        }
    }

    // SS6
    [TestMethod]
    public void CloseRunning_Other_Session_Lists_Each_Pid_Once()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var current = ProcessScan.CurrentSessionId();
        using var other = StartCopyFrom(installDir, ToolName);
        var entry = new ProcessEntry(other.Id, current + 2);
        var source = new MapSource().Add(ToolName, entry, entry);
        var output = new StringWriter();
        try
        {
            var code = ProcessCommands.CloseRunning(installDir, [], 1, output, source, current, _ => { });

            Assert.AreEqual(4, code);
            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.AreEqual(1, lines.Count(line => line.Contains("pid=" + other.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        }
        finally
        {
            Kill(other);
        }
    }

    [TestMethod]
    public void OtherSessions_Scan_Skips_Exited_Process()
    {
        using var dir = new TempDir();
        var installDir = dir.Sub("install");
        var current = ProcessScan.CurrentSessionId();
        using var gone = StartCopyFrom(installDir, ToolName);
        var pid = gone.Id;
        Kill(gone);
        var source = new MapSource().Add(ToolName, new ProcessEntry(pid, current + 3));

        var found = ProcessScan.RunningFromOtherSessions(installDir, new HashSet<int>(), source, current);

        Assert.AreEqual(0, found.Count, "a process that exited is not listed");
    }

    private static Process StartCopyFrom(string folder, string name)
    {
        var copy = Path.Combine(folder, name + ".exe");
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
                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>Returns the given entries for each named tool, and nothing for any other name.</summary>
    private sealed class MapSource : IProcessSource
    {
        private readonly Dictionary<string, ProcessEntry[]> _entries = new(StringComparer.Ordinal);

        public MapSource Add(string name, params ProcessEntry[] entries)
        {
            _entries[name] = entries;
            return this;
        }

        public IReadOnlyList<ProcessEntry> ByName(string processName) =>
            _entries.TryGetValue(processName, out var entries) ? entries : [];
    }
}
