using System.Diagnostics;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Command line messages (MSG-022, MSG-062). A switch that takes a filename and has none reports classic string 43 on
/// standard error and exits 2. A binary registry hive given to /s is refused with classic string 123, names the file, and
/// exits 1, with nothing written. Both runs use only the process; no window is created and the sandbox is checked before
/// and after. The texts are the classic strings as the oracle dump records them (regedit-resources.json).
/// </summary>
[TestClass]
public sealed class CommandLineMessageTests
{
    // RT_STRING 43, as the oracle dump records it.
    private const string MissingFilenameText = "Command line argument requires a filename and none was specified.";

    // RT_STRING 123 as the oracle dump records it, with %1 as the file name the user gave.
    private const string BinaryFileTemplate =
        "Cannot import {0}: The specified file is not a registry script.\r\nYou can only import binary registry files from within the registry editor.";

    private const string ProbeValue = "Probe";

    [TestMethod]
    [TestCategory("ImportExport")]
    public void CommandLine_SwitchWithoutFilenameReportsStringFortyThree()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "command-line", config.RunId, "missing-filename");
        try
        {
            Directory.CreateDirectory(tempDir);
            Seed(sandbox);

            foreach (string switchName in new[] { "/s", "/e", "/a" })
            {
                CommandResult result = Run(config, sandbox, switchName);
                string expectedLine = "BwcRegedit: " + MissingFilenameText;
                bool textFound = result.StandardError.Split('\n').Any(line => line.Trim() == expectedLine);
                E2EResults.Record(
                    "commandline.missing." + switchName.TrimStart('/'),
                    result.Exited && result.ExitCode == 2 && textFound,
                    $"{switchName} alone: exited={result.Exited}, exit={result.ExitCode} (expected 2), " +
                        $"string 43 on stderr={textFound}, stderr='{result.StandardError.Trim()}'.");
            }

            // A second operation after a valid one is a usage error. Nothing is exported to the file the first switch names.
            string exportFile = Path.Combine(tempDir, "second-operation.reg");
            CommandResult second = Run(config, sandbox, "/e", exportFile, "/s");
            E2EResults.Record(
                "commandline.missing.second-operation",
                second.Exited && second.ExitCode == 2 && !File.Exists(exportFile),
                $"'/e file /s': exited={second.Exited}, exit={second.ExitCode} (expected 2), " +
                    $"file written={File.Exists(exportFile)}.");

            string probe = sandbox.ReadString(string.Empty, ProbeValue) ?? string.Empty;
            E2EResults.Record(
                "commandline.missing.unchanged",
                probe == ProbeValue,
                $"sandbox value after the refused switches='{probe}', expected '{ProbeValue}'.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    public void CommandLine_BinaryHiveRefusedWithStringOneTwentyThree()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "command-line", config.RunId, "binary-hive");
        try
        {
            Directory.CreateDirectory(tempDir);
            Seed(sandbox);

            // A registry hive starts with the lowercase signature "regf". Nothing after it is read before the refusal.
            string hive = Path.Combine(tempDir, "binary-hive.dat");
            byte[] bytes = new byte[4096];
            Encoding.ASCII.GetBytes("regf").CopyTo(bytes, 0);
            File.WriteAllBytes(hive, bytes);

            CommandResult result = Run(config, sandbox, "/s", hive);
            // The console writes the resource text with LF line ends, so the CRLF of the oracle text is compared the same way.
            string expected = "BwcRegedit: " + string.Format(BinaryFileTemplate, hive).Replace("\r\n", "\n", StringComparison.Ordinal);
            string actual = result.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal);
            bool textFound = actual.Contains(expected, StringComparison.Ordinal);
            E2EResults.Record(
                "commandline.binary.refused",
                result.Exited && result.ExitCode == 1 && textFound,
                $"exited={result.Exited}, exit={result.ExitCode} (expected 1), string 123 on stderr={textFound}, stderr='{result.StandardError.Trim()}'.");

            string probe = sandbox.ReadString(string.Empty, ProbeValue) ?? string.Empty;
            E2EResults.Record(
                "commandline.binary.unchanged",
                probe == ProbeValue,
                $"sandbox value after the refusal='{probe}', expected '{ProbeValue}'.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.SetString(string.Empty, ProbeValue, ProbeValue);
    }

    private static CommandResult Run(E2EConfig config, SandboxRegistry sandbox, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(config.RegeditExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("command-line-messages");
        startInfo.Environment["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{config.RegeditExePath}'.");

        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        bool exited = process.WaitForExit(15000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        _ = stdout.Wait(TimeSpan.FromSeconds(5));
        string error = stderr.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result : string.Empty;
        return new CommandResult(exited ? process.ExitCode : -1, exited, error);
    }

    private static void FailIfNeeded(int checkStart)
    {
        var failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Command line message checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private sealed record CommandResult(int ExitCode, bool Exited, string StandardError);
}
