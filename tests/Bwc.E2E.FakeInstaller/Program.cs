using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bwc.E2E.FakeInstaller;

/// <summary>
/// The stand-in installer for the Hub install E2E test. It is not an installer. It writes one record file
/// (the path comes from BWC_E2E_INSTALLER_RECORD), then starts the Hub again from the /D= folder when the test
/// sets BWC_E2E_INSTALLER_RELAUNCH_HUB=1, because the installer starts the Hub (docs/contracts/update-manifest.md).
/// It writes nothing else, installs nothing, and exits 0.
/// </summary>
internal static class Program
{
    public const string RecordVariable = "BWC_E2E_INSTALLER_RECORD";
    public const string RelaunchVariable = "BWC_E2E_INSTALLER_RELAUNCH_HUB";

    private const string HubExeName = "BetterWindowsConfig.exe";
    private const string DirectoryPrefix = "/D=";
    private const int ExitOk = 0;
    private const int ExitNoRecordPath = 2;
    private const int ExitRecordNotWritten = 4;
    private const int ExitHubNotStarted = 6;

    private static int Main(string[] args)
    {
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
        bool relaunch = Environment.GetEnvironmentVariable(RelaunchVariable) == "1";

        var lines = new List<string>
        {
            "timestampUtc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "processPath=" + ownPath,
            "sha256=" + sha256,
            "commandLine=" + Environment.CommandLine,
            "argc=" + args.Length.ToString(CultureInfo.InvariantCulture),
        };
        for (int i = 0; i < args.Length; i++)
        {
            lines.Add("arg" + i.ToString(CultureInfo.InvariantCulture) + "=" + args[i]);
        }

        lines.Add("relaunchRequested=" + (relaunch ? "1" : "0"));

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

        if (!relaunch)
        {
            return ExitOk;
        }

        string? installDir = DirectoryArgument(args);
        return installDir is not null && StartHub(installDir) ? ExitOk : ExitHubNotStarted;
    }

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
