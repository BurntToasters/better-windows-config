using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Hub;

/// <summary> The shipped (E2E flavor) BwcUpdate.exe is NativeAOT (docs/failure-modes/update-elevation.md HE5, HN9,
/// HN10). Two checks run the real binary: it ignores the .NET profiler, host trace, startup hook, EventPipe, and
/// standalone GC variables (a CoreCLR program started the same way is the control that proves each variable has an
/// effect), and the elevated
/// --launch-hub path (the shell chain of typed vtable calls) starts a stand-in Hub through the real desktop shell.
/// Nothing is elevated: the force-elevated seam exists only in the E2E flavor and makes the helper take the elevated
/// branch.
/// </summary>
[TestClass]
public sealed class HelperNativeTests
{
    private const string HelperExeName = "BwcUpdate.exe";
    private const string StandInHubName = "BetterWindowsConfig.exe";
    private const string IgnoredScope = "hub-native-ignored";
    private const string LaunchScope = "hub-native-launch";
    private const string ForceElevatedVariable = "BWC_E2E_UPDATE_FORCE_ELEVATED";
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private static int _checkStart;

    /// <summary> HE1, HE5, HN24: the helper prints its usage and exits 0 with every variable set, and writes none of
    /// the files they name. The control (a CoreCLR stand-in installer) writes the host trace file, so a missing file
    /// means something.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_Helper_Ignores_Runtime_Variables()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        string helperPath = Path.Combine(config.PublishDir, HelperExeName);
        string standInPath = UpdateFixture.StandInPath(config, config.Rid);

        string work = config.TempRootFor(IgnoredScope);
        string helperFolder = Path.Combine(work, "helper");
        Directory.CreateDirectory(helperFolder);
        string helperCopy = Path.Combine(helperFolder, HelperExeName);
        File.Copy(helperPath, helperCopy, overwrite: true);

        // The control first: the same variables, a program that runs on CoreCLR.
        string controlFolder = Path.Combine(work, "control");
        Directory.CreateDirectory(controlFolder);
        string controlCopy = Path.Combine(controlFolder, "control.exe");
        File.Copy(standInPath, controlCopy, overwrite: true);
        RunWithVariables(controlCopy, [], controlFolder, out int controlExit, out _);
        string controlTrace = Path.Combine(controlFolder, "host-trace.txt");
        E2EResults.Record(
            "hub.native.control.trace",
            File.Exists(controlTrace) && new FileInfo(controlTrace).Length > 0,
            $"The CoreCLR control wrote the host trace file (exit {controlExit}), so COREHOST_TRACE does have an effect on a managed program.");

        RunWithVariables(helperCopy, ["--help"], helperFolder, out int exit, out string stdout);
        string[] written = Directory.GetFileSystemEntries(helperFolder)
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, HelperExeName, StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .ToArray();
        E2EResults.Record("hub.native.helper.exit", exit == 0, $"BwcUpdate.exe --help exited {exit} with the runtime variables set.");
        E2EResults.Record(
            "hub.native.helper.usage",
            stdout.StartsWith("Usage:", StringComparison.Ordinal),
            "The helper printed its usage text: " + stdout.Split('\n')[0].Trim());
        E2EResults.Record(
            "hub.native.helper.noFiles",
            written.Length == 0,
            "Files the variables could have made in the helper's folder: " + (written.Length == 0 ? "none" : string.Join(", ", written)));
        string trace = Path.Combine(helperFolder, "host-trace.txt");
        E2EResults.Record("hub.native.helper.noTrace", !File.Exists(trace), "No host trace file at " + trace + ".");

        AssertNoFailures("Native helper ignores runtime variables");
    }

    /// <summary> HN9, HN10, HN12: with the force-elevated seam the helper takes the elevated --launch-hub path, so the
    /// stand-in Hub is started by the desktop shell (FindWindowSW to ShellExecute, all through vtables), not by the
    /// helper: the stand-in's parent is not the helper. Without a shell the exit is 8 and nothing starts. The Elevated
    /// machine test shows the integrity level (launch.medium); this one shows the chain works on a real shell.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_Helper_LaunchHub_Forced_Elevated_Uses_Shell()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();
        string helperPath = Path.Combine(config.PublishDir, HelperExeName);
        string standInPath = UpdateFixture.StandInPath(config, config.Rid);

        string folder = Path.Combine(config.TempRootFor(LaunchScope), "install");
        Directory.CreateDirectory(folder);
        string helperCopy = Path.Combine(folder, HelperExeName);
        string hubStandIn = Path.Combine(folder, StandInHubName);
        File.Copy(helperPath, helperCopy, overwrite: true);
        File.Copy(standInPath, hubStandIn, overwrite: true);

        bool shellPresent = Process.GetProcessesByName("explorer").Length > 0;
        DateTime beforeUtc = DateTime.UtcNow.AddSeconds(-1);
        var startInfo = new ProcessStartInfo(helperCopy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        startInfo.ArgumentList.Add("--launch-hub");
        foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(LaunchScope))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in config.TempEnvironment(LaunchScope))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        startInfo.Environment[ForceElevatedVariable] = "1";

        using Process helper = Process.Start(startInfo)!;
        Task<string> output = helper.StandardOutput.ReadToEndAsync();
        bool exited = helper.WaitForExit((int)HelperTimeout.TotalMilliseconds);
        if (!exited)
        {
            helper.Kill(entireProcessTree: true);
        }

        string text = exited ? output.GetAwaiter().GetResult().Trim() : string.Empty;
        int helperId = helper.Id;
        int exitCode = exited ? helper.ExitCode : -1;

        using Process? started = InstallEvidence.WaitForStartedProcess("BetterWindowsConfig", hubStandIn, 0, beforeUtc, StartTimeout);
        try
        {
            if (!shellPresent)
            {
                E2EResults.Record("hub.native.launch.noShell", exitCode == 8 && started is null, $"No Explorer: exit {exitCode}, started={started is not null}.");
            }
            else
            {
                E2EResults.Record("hub.native.launch.exit", exitCode == 0, $"--launch-hub with the force-elevated seam exited {exitCode}; stdout: {text}");
                E2EResults.Record(
                    "hub.native.launch.json",
                    text == "{\"ok\":true,\"failure\":\"None\",\"detail\":\"Hub started\"}",
                    "The result line is the contract's: " + text);
                E2EResults.Record("hub.native.launch.started", started is not null, started is null ? "No stand-in Hub started from " + hubStandIn + "." : $"The shell started {hubStandIn} (pid {started.Id}).");
                if (started is not null)
                {
                    int parent = ParentProcessId(started);
                    E2EResults.Record(
                        "hub.native.launch.viaShell",
                        parent > 0 && parent != helperId,
                        $"The stand-in's parent is pid {parent} ({ProcessName(parent)}); the helper was pid {helperId}. A plain start would make the helper the parent.");
                }
            }
        }
        finally
        {
            // Only the process that was just verified (by image path, started after this call) is stopped.
            if (started is not null && !started.HasExited)
            {
                started.Kill();
                started.WaitForExit(10_000);
            }
        }

        AssertNoFailures("Native helper launch through the shell");
    }

    // The same set of variables for the control and for the helper. Each one names a file in the program's own folder,
    // or a file that does not exist, so a program that obeys them leaves a trace there or fails to start.
    private static void RunWithVariables(string exe, string[] arguments, string folder, out int exitCode, out string stdout)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string missing = Path.Combine(folder, "does-not-exist.dll");
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COREHOST_TRACE"] = "1",
            ["COREHOST_TRACE_VERBOSITY"] = "4",
            ["COREHOST_TRACEFILE"] = Path.Combine(folder, "host-trace.txt"),
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER"] = "{11111111-2222-3333-4444-555555555555}",
            ["CORECLR_PROFILER_PATH"] = missing,
            ["CORECLR_PROFILER_PATH_64"] = missing,
            ["CORECLR_PROFILER_PATH_ARM64"] = missing,
            ["DOTNET_STARTUP_HOOKS"] = missing,
            ["DOTNET_EnableEventPipe"] = "1",
            ["DOTNET_EventPipeOutputPath"] = Path.Combine(folder, "events.nettrace"),
            ["DOTNET_GCName"] = missing,
            ["DOTNET_GCPath"] = missing,
        };
        foreach (KeyValuePair<string, string> pair in variables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit((int)HelperTimeout.TotalMilliseconds);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        exitCode = exited ? process.ExitCode : -1;
        stdout = exited ? output.GetAwaiter().GetResult() : string.Empty;
        _ = error.GetAwaiter().GetResult();
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return "gone";
        }
    }

    private static int ParentProcessId(Process process)
    {
        var information = new ProcessBasicInformation();
        int status = NtQueryInformationProcess(process.Handle, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status == 0 ? (int)information.InheritedFromUniqueProcessId : -1;
    }

    private static void AssertNoFailures(string label)
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"{label} failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public UIntPtr UniqueProcessId;
        public UIntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);
}
