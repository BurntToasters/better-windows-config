using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Bwc.E2E.FakeInstaller;

/// <summary>
/// The stand-in installer for the Hub install E2E test. It is not an installer. It writes one record file
/// (the path comes from BWC_E2E_INSTALLER_RECORD), then starts the Hub again from the /D= folder only when the
/// command line has /RELAUNCH, because the real installer starts the new Hub when that switch is given
/// (docs/contracts/update-manifest.md). It writes nothing else, installs nothing, and exits 0.
///
/// Two additions for the elevated machine tests (docs/failure-modes/installer-e2e.md, 63 to 75):
/// - The record also holds the TEMP and TMP it was started with, whether it runs elevated, and the E2E protected root
///   variable, so a test can check what the elevated helper gave the installer. With BWC_E2E_INSTALLER_NO_RELAUNCH=1
///   the Hub is not started (the record says so).
/// - Copied under the name BetterWindowsConfig.exe or BwcRegedit.exe it is a harmless stand-in for that program: it
///   does nothing and waits up to 90 seconds. The tests use it where starting the real Hub would write the user's real
///   settings folder, or where a process must never close.
/// </summary>
internal static class Program
{
    public const string RecordVariable = "BWC_E2E_INSTALLER_RECORD";
    public const string NoRelaunchVariable = "BWC_E2E_INSTALLER_NO_RELAUNCH";
    private const string ProtectedRootVariable = "BWC_E2E_UPDATE_PROTECTED_ROOT";
    private const string StandInHubName = "BetterWindowsConfig";
    private const string StandInRegeditName = "BwcRegedit";
    private const int StandInLifetimeSeconds = 90;
    private const string RelaunchSwitch = "/RELAUNCH";

    private const string HubExeName = "BetterWindowsConfig.exe";
    private const string DirectoryPrefix = "/D=";
    private const int ExitOk = 0;
    private const int ExitNoRecordPath = 2;
    private const int ExitRecordNotWritten = 4;
    private const int ExitHubNotStarted = 6;

    private static int Main(string[] args)
    {
        string? ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (string.Equals(ownName, StandInHubName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ownName, StandInRegeditName, StringComparison.OrdinalIgnoreCase))
        {
            return RunStandIn();
        }

        string? recordPath = Environment.GetEnvironmentVariable(RecordVariable);
        if (string.IsNullOrWhiteSpace(recordPath) || !Path.IsPathFullyQualified(recordPath))
        {
            return ExitNoRecordPath;
        }

        string? ownPath = Environment.ProcessPath;
        if (ownPath is null || !File.Exists(ownPath))
        {
            return ExitNoRecordPath;
        }

        // The hash is of the file this process runs, so a test can compare it with the signed bytes.
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ownPath))).ToLowerInvariant();
        bool relaunch = args.Any(arg => string.Equals(arg, RelaunchSwitch, StringComparison.OrdinalIgnoreCase));
        bool skipRelaunch = Environment.GetEnvironmentVariable(NoRelaunchVariable) == "1";

        var lines = new List<string>
        {
            "timestampUtc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "processPath=" + ownPath,
            "sha256=" + sha256,
            "commandLine=" + Environment.CommandLine,
            "argc=" + args.Length.ToString(CultureInfo.InvariantCulture),
            "processId=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "elevated=" + (IsElevated() ? "1" : "0"),
            "env.TEMP=" + (Environment.GetEnvironmentVariable("TEMP") ?? string.Empty),
            "env.TMP=" + (Environment.GetEnvironmentVariable("TMP") ?? string.Empty),
            "env.protectedRoot=" + (Environment.GetEnvironmentVariable(ProtectedRootVariable) ?? string.Empty),
        };
        for (int i = 0; i < args.Length; i++)
        {
            lines.Add("arg" + i.ToString(CultureInfo.InvariantCulture) + "=" + args[i]);
        }

        lines.Add("relaunchRequested=" + (relaunch ? "1" : "0"));
        lines.Add("relaunchSkipped=" + (relaunch && skipRelaunch ? "1" : "0"));

        try
        {
            // CreateNew: a record that already exists means the stand-in ran before, and it is not overwritten.
            using var stream = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (string line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }
        }
        catch (IOException)
        {
            return ExitRecordNotWritten;
        }
        catch (UnauthorizedAccessException)
        {
            return ExitRecordNotWritten;
        }

        if (!relaunch || skipRelaunch)
        {
            return ExitOk;
        }

        string? installDir = DirectoryArgument(args);
        return installDir is not null && StartHub(installDir) ? ExitOk : ExitHubNotStarted;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// The stand-in for the Hub or Regedit of a test install: no window, no work, no file. It waits so that a test can read
    /// its token and stop it. The console is closed first so nothing flashes. It writes nothing because the shell may start
    /// it at medium integrity, which cannot write the install folder.
    /// </summary>
    private static int RunStandIn()
    {
        _ = FreeConsole();
        Thread.Sleep(TimeSpan.FromSeconds(StandInLifetimeSeconds));
        return ExitOk;
    }

#pragma warning disable SYSLIB1054 // One call that frees the console; LibraryImport plumbing is not worth it.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
#pragma warning restore SYSLIB1054

    /// <summary>The folder after the last /D=, or null. The contract puts /D= last.</summary>
    private static string? DirectoryArgument(string[] args)
    {
        for (int i = args.Length - 1; i >= 0; i--)
        {
            if (args[i].StartsWith(DirectoryPrefix, StringComparison.Ordinal))
            {
                return args[i][DirectoryPrefix.Length..];
            }
        }

        return null;
    }

    private static bool StartHub(string installDir)
    {
        string hub = Path.Combine(installDir, HubExeName);
        if (!File.Exists(hub))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(hub)
        {
            UseShellExecute = false,
            WorkingDirectory = installDir,
        };
        try
        {
            // The Hub is not waited for. Disposing the handle does not stop the Hub.
            using Process? started = Process.Start(startInfo);
            return started is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
