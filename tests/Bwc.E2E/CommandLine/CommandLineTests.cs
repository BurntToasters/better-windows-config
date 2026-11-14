using System.Diagnostics;
using System.Text;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.CommandLine;

/// <summary>
/// Runs the Registry Editor executable with /e, /a, /s, and an unknown switch. Every check uses
/// the sandbox root, and no AppSession is created because the command line paths never show a
/// window.
/// </summary>
[TestClass]
public sealed class CommandLineTests
{
    // Outside the sandbox root (Software\BWC-E2E\<run-id>) but named for this run.
    private static string OutsideSubKey => @"Software\BWC-E2E-Outside-" + GuiGate.Config.RunId;

    private static int _checkStart;

    [TestMethod]
    [TestCategory("CommandLine")]
    [TestCategory("Mutating")]
    public void CommandLine_ExportsImportsAndRejectsOutsideSandbox()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("commandline.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "command-line", config.RunId);
        string v5File = Path.Combine(tempDir, "export-v5.reg");
        string regedit4File = Path.Combine(tempDir, "export-4.reg");
        string outsideFile = Path.Combine(tempDir, "outside.reg");

        // The outside key is removed in finally only when the app created it during this test (outsideAppCreated).
        // A key that already exists belongs to someone else, so the test fails before any write and leaves it alone.
        bool outsidePreexisted = OutsideKeyExists();
        bool outsideAppCreated = false;
        E2EResults.Record(
            "commandline.outside.absent",
            !outsidePreexisted,
            $"HKCU\\{OutsideSubKey} existed before the test={outsidePreexisted}.");
        if (outsidePreexisted)
        {
            Assert.Fail($"HKCU\\{OutsideSubKey} already exists. The test will not modify or delete it.");
        }

        try
        {
            sandbox.Cleanup();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            Directory.CreateDirectory(tempDir);
            SeedSandbox(sandbox);

            string expectedRoot = $@"HKEY_CURRENT_USER\{sandbox.RunRootPath}";
            string expectedChild = expectedRoot + @"\Child";

            CommandResult export = Run(config, sandbox, "/e", v5File, sandbox.RunRootDisplayPath);
            E2EResults.Record(
                "commandline.export.exit",
                export.Exited && export.ExitCode == 0,
                $"exited={export.Exited}, exit={export.ExitCode}, stderr='{export.StandardError.Trim()}'.");

            byte[] v5Bytes = File.Exists(v5File) ? File.ReadAllBytes(v5File) : Array.Empty<byte>();
            bool v5Bom = v5Bytes.Length >= 2 && v5Bytes[0] == 0xFF && v5Bytes[1] == 0xFE;
            string v5Text = v5Bom
                ? Encoding.Unicode.GetString(v5Bytes, 2, v5Bytes.Length - 2)
                : string.Empty;
            bool v5Content = v5Text.StartsWith(
                    "Windows Registry Editor Version 5.00\r\n\r\n",
                    StringComparison.Ordinal)
                && v5Text.Contains("[" + expectedRoot + "]", StringComparison.OrdinalIgnoreCase)
                && v5Text.Contains("[" + expectedChild + "]", StringComparison.OrdinalIgnoreCase)
                && v5Text.Contains("@=\"default text\"", StringComparison.Ordinal)
                && v5Text.Contains("\"Text\"=\"hello\"", StringComparison.Ordinal)
                && v5Text.Contains("\"Dword\"=dword:01020304", StringComparison.Ordinal)
                && v5Text.Contains("\"Binary\"=hex:00,01,02,fe,ff", StringComparison.Ordinal);
            E2EResults.Record(
                "commandline.export.v5",
                export.Exited && export.ExitCode == 0 && v5Bom && v5Content,
                $"exists={File.Exists(v5File)}, bom={v5Bom}, content={v5Content}, bytes={v5Bytes.Length}.");

            CommandResult ansi = Run(config, sandbox, "/a", regedit4File, sandbox.RunRootDisplayPath);
            byte[] ansiBytes = File.Exists(regedit4File)
                ? File.ReadAllBytes(regedit4File)
                : Array.Empty<byte>();
            bool noBom = ansiBytes.Length > 0 && !(ansiBytes[0] == 0xFF && ansiBytes[1] == 0xFE);
            string ansiText = ansiBytes.Length == 0 ? string.Empty : Encoding.ASCII.GetString(ansiBytes);
            bool ansiContent = ansiText.StartsWith("REGEDIT4\r\n\r\n", StringComparison.Ordinal)
                && ansiText.Contains("[" + expectedRoot + "]", StringComparison.OrdinalIgnoreCase)
                && ansiText.Contains("\"Text\"=\"hello\"", StringComparison.Ordinal);
            E2EResults.Record(
                "commandline.export.regedit4",
                ansi.Exited && ansi.ExitCode == 0 && noBom && ansiContent,
                $"exit={ansi.ExitCode}, noBom={noBom}, content={ansiContent}, bytes={ansiBytes.Length}.");

            sandbox.DeleteSubKeyTree(string.Empty);
            bool deleted = !sandbox.Exists(string.Empty);
            E2EResults.Record(
                "commandline.import.delete",
                deleted,
                $"run root exists after delete={!deleted}.");

            E2EGuards.RequireImportFileInsideSandbox(v5File, sandbox, "commandline.import.target");
            CommandResult import = Run(config, sandbox, "/s", v5File);
            bool restored = sandbox.Exists(string.Empty)
                && sandbox.ReadString(string.Empty, "Text") == "hello"
                && sandbox.ReadString(string.Empty, string.Empty) == "default text"
                && sandbox.ReadDword(string.Empty, "Dword") == 0x01020304
                && sandbox.Exists("Child");
            byte[]? binary = sandbox.ReadBinary(string.Empty, "Binary");
            bool binaryRestored = binary is { Length: 5 }
                && binary[0] == 0x00
                && binary[1] == 0x01
                && binary[2] == 0x02
                && binary[3] == 0xFE
                && binary[4] == 0xFF;
            E2EResults.Record(
                "commandline.import.v5",
                import.Exited && import.ExitCode == 0 && restored && binaryRestored,
                $"exit={import.ExitCode}, restored={restored}, binary={binaryRestored}, stderr='{import.StandardError.Trim()}'.");

            // Deliberate probe: this import targets a key outside the sandbox. It is the fail-closed
            // assertion for SandboxWritePolicy, so it only ever runs on the e2e flavor (guarded above).
            string outsideText =
                "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[HKEY_CURRENT_USER\\" + OutsideSubKey + "]\r\n"
                + "\"Value\"=\"outside\"\r\n";
            File.WriteAllBytes(outsideFile, EncodeV5(outsideText));

            CommandResult denied = Run(config, sandbox, "/s", outsideFile);
            bool outsideCreated = OutsideKeyExists();
            outsideAppCreated = outsideCreated;
            E2EResults.Record(
                "commandline.import.outside.denied",
                denied.Exited && denied.ExitCode != 0 && !outsideCreated,
                $"exit={denied.ExitCode}, outsideCreated={outsideCreated}, stderr='{denied.StandardError.Trim()}'.");

            CommandResult unknown = Run(config, sandbox, "/z");
            E2EResults.Record(
                "commandline.unknown.exit",
                unknown.Exited && unknown.ExitCode == 2,
                $"exited={unknown.Exited}, exit={unknown.ExitCode}, stderr='{unknown.StandardError.Trim()}'.");
            E2EResults.Record(
                "commandline.unknown.nowindow",
                unknown.Exited && !unknown.SawWindow,
                $"exited={unknown.Exited}, sawWindow={unknown.SawWindow}.");

            FailIfNeeded();
        }
        finally
        {
            sandbox.Cleanup();
            if (outsideAppCreated)
            {
                DeleteOutsideKey();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                E2EResults.Record("commandline.cleanup.temp", false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Silent import exit codes. A refused file exits 1 and writes nothing. A file with one malformed line exits 0
    /// and imports the rest, as classic regedit does. A file with one line the write policy refuses exits 1 and
    /// still imports the lines that can be written. None of the runs shows a window.
    /// </summary>
    [TestMethod]
    [TestCategory("CommandLine")]
    [TestCategory("Mutating")]
    public void CommandLine_SilentImportExitCodesFollowTheResult()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("commandline.silent.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "command-line-silent", config.RunId);
        bool outsidePreexisted = OutsideKeyExists();
        bool outsideAppCreated = false;
        E2EResults.Record(
            "commandline.silent.outsideAbsent",
            !outsidePreexisted,
            $"HKCU\\{OutsideSubKey} existed before the test={outsidePreexisted}.");
        if (outsidePreexisted)
        {
            Assert.Fail($"HKCU\\{OutsideSubKey} already exists. The test will not modify or delete it.");
        }

        try
        {
            sandbox.Cleanup();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            Directory.CreateDirectory(tempDir);
            string sandboxRoot = @"HKEY_CURRENT_USER\" + sandbox.RunRootPath;

            // Refused: no version header, so the whole file is refused and nothing is written.
            string refusedFile = Path.Combine(tempDir, "refused.reg");
            File.WriteAllBytes(refusedFile, EncodeV5(
                "[" + sandboxRoot + "\\Import\\Refused]\r\n"
                + "\"Value\"=\"refused\"\r\n"));
            CommandResult refused = Run(config, sandbox, "/s", refusedFile);
            bool refusedWritten = sandbox.Exists("Import\\Refused");
            E2EResults.Record(
                "commandline.silent.refused.exit",
                refused.Exited && refused.ExitCode == 1 && !refused.SawWindow && !refusedWritten,
                $"exited={refused.Exited}, exit={refused.ExitCode}, sawWindow={refused.SawWindow}, written={refusedWritten}.");
            E2EResults.Record(
                "commandline.silent.refused.text",
                refused.StandardError.Contains("is not a registry file", StringComparison.Ordinal),
                $"stderr='{refused.StandardError.Trim()}'.");

            // One malformed line: the sections around it import and the exit code is 0.
            string malformedFile = Path.Combine(tempDir, "malformed.reg");
            File.WriteAllBytes(malformedFile, EncodeV5(
                "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[" + sandboxRoot + "\\Import\\Before]\r\n"
                + "\"Value\"=\"before\"\r\n\r\n"
                + "this line is not a registry entry\r\n\r\n"
                + "[" + sandboxRoot + "\\Import\\After]\r\n"
                + "\"Value\"=\"after\"\r\n"));
            CommandResult malformed = Run(config, sandbox, "/s", malformedFile);
            bool malformedRest = sandbox.ReadString("Import\\Before", "Value") == "before"
                && sandbox.ReadString("Import\\After", "Value") == "after";
            E2EResults.Record(
                "commandline.silent.malformed.exit",
                malformed.Exited && malformed.ExitCode == 0 && !malformed.SawWindow && malformedRest,
                $"exited={malformed.Exited}, exit={malformed.ExitCode}, sawWindow={malformed.SawWindow}, rest imported={malformedRest}.");

            // One line the write policy refuses: the section that can be written still imports, and the exit code is 1.
            string deniedFile = Path.Combine(tempDir, "denied.reg");
            File.WriteAllBytes(deniedFile, EncodeV5(
                "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[" + sandboxRoot + "\\Import\\Good]\r\n"
                + "\"Value\"=\"good\"\r\n\r\n"
                + "[HKEY_CURRENT_USER\\" + OutsideSubKey + "]\r\n"
                + "\"Value\"=\"outside\"\r\n"));
            CommandResult denied = Run(config, sandbox, "/s", deniedFile);
            bool goodWritten = sandbox.ReadString("Import\\Good", "Value") == "good";
            bool outsideAbsent = !OutsideKeyExists();
            outsideAppCreated = !outsideAbsent;
            E2EResults.Record(
                "commandline.silent.denied.exit",
                denied.Exited && denied.ExitCode == 1 && !denied.SawWindow && goodWritten && outsideAbsent,
                $"exited={denied.Exited}, exit={denied.ExitCode}, sawWindow={denied.SawWindow}, good written={goodWritten}, outside absent={outsideAbsent}.");
            E2EResults.Record(
                "commandline.silent.denied.text",
                denied.StandardError.Contains("Not all data was successfully written", StringComparison.Ordinal),
                $"stderr='{denied.StandardError.Trim()}'.");

            FailIfNeeded();
        }
        finally
        {
            sandbox.Cleanup();
            if (outsideAppCreated)
            {
                DeleteOutsideKey();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                E2EResults.Record("commandline.silent.cleanup.temp", false, ex.Message);
            }
        }
    }

    private static void SeedSandbox(SandboxRegistry sandbox)
    {
        using (RegistryKey key = sandbox.CreateSubKey(string.Empty))
        {
            key.SetValue(string.Empty, "default text", RegistryValueKind.String);
            key.SetValue("Text", "hello", RegistryValueKind.String);
            key.SetValue("Dword", 0x01020304, RegistryValueKind.DWord);
            key.SetValue("Binary", new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF }, RegistryValueKind.Binary);
        }

        using (RegistryKey child = sandbox.CreateSubKey("Child"))
        {
            child.SetValue("Qword", 0x1122334455667788UL, RegistryValueKind.QWord);
        }
    }

    /// <summary>
    /// A whole-registry /e streams to a temp file and moves it over the target. A successful run leaves a valid
    /// version 5 file and no temp file. A run whose target is locked fails, keeps the old content, and leaves no temp file.
    /// </summary>
    [TestMethod]
    [TestCategory("CommandLine")]
    [TestCategory("Mutating")]
    public void CommandLine_WholeRegistryExportReplacesTheTargetAtomically()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("commandline.whole.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "command-line", config.RunId + "-whole");
        string target = Path.Combine(tempDir, "whole.reg");
        try
        {
            sandbox.Cleanup();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            Directory.CreateDirectory(tempDir);
            File.WriteAllText(target, OldWholeContent, Encoding.ASCII);

            WholeExport export = RunWholeRegistryExport(config, sandbox, target);
            byte[] bytes = File.ReadAllBytes(target);
            bool bom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
            string text = bom ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2) : string.Empty;
            bool validRegFile = text.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n", StringComparison.Ordinal) &&
                text.Contains("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase);
            Record(
                "commandline.whole.export",
                export.Exited && export.ExitCode == 0 && validRegFile,
                $"exited={export.Exited}, exit={export.ExitCode}, bom={bom}, valid .reg={validRegFile}, bytes={bytes.Length}, stderr='{export.StandardError.Trim()}'.");
            Record(
                "commandline.whole.noTempAfterSuccess",
                LeftoversBeside(tempDir, target).Length == 0,
                $"other files beside the target={string.Join(",", LeftoversBeside(tempDir, target))}.");

            File.WriteAllText(target, OldWholeContent, Encoding.ASCII);
            WholeExport failed;
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                failed = RunWholeRegistryExport(config, sandbox, target);
            }

            string kept = File.ReadAllText(target, Encoding.ASCII);
            Record(
                "commandline.whole.lockedTarget",
                failed.Exited && failed.ExitCode != 0 && string.Equals(kept, OldWholeContent, StringComparison.Ordinal),
                $"exited={failed.Exited}, exit={failed.ExitCode}, old content kept={string.Equals(kept, OldWholeContent, StringComparison.Ordinal)}.");
            Record(
                "commandline.whole.noTempAfterFailure",
                LeftoversBeside(tempDir, target).Length == 0,
                $"other files beside the target={string.Join(",", LeftoversBeside(tempDir, target))}.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // The temp folder is scoped to this run and is removed best effort.
            }
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Whole-registry export checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private const string OldWholeContent = "OLD CONTENT, KEEP ME\r\n";
    private static readonly TimeSpan WholeRegistryTimeout = TimeSpan.FromMinutes(15);

    private readonly record struct WholeExport(bool Exited, int ExitCode, string StandardError);

    private static void Record(string id, bool pass, string notes) => _ = E2EResults.Record(id, pass, notes);

    /// <summary>Files in the folder other than the target. A stray temp file is reported here.</summary>
    private static string[] LeftoversBeside(string folder, string target) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// Runs /e with no key, which is the whole registry. The window path never shows, so the wait is longer than
    /// the 10 second limit <see cref="Run"/> uses for the sandbox exports.
    /// </summary>
    private static WholeExport RunWholeRegistryExport(E2EConfig config, SandboxRegistry sandbox, string file)
    {
        var startInfo = new ProcessStartInfo(config.RegeditExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/e");
        startInfo.ArgumentList.Add(file);
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("command-line-whole");
        startInfo.Environment["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{config.RegeditExePath}'.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        bool exited = process.WaitForExit(WholeRegistryTimeout);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        _ = stdout.Wait(TimeSpan.FromSeconds(5));
        string error = stderr.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result : string.Empty;
        return new WholeExport(exited, exited ? process.ExitCode : -1, error);
    }

    private static CommandResult Run(
        E2EConfig config,
        SandboxRegistry sandbox,
        params string[] arguments)
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
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("command-line");
        startInfo.Environment["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Process.Start returned no process for '{config.RegeditExePath}'.");

        bool sawWindow = false;
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!process.HasExited && DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                sawWindow = true;
            }

            Thread.Sleep(50);
        }

        bool exited = process.HasExited;
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        string standardError;
        try
        {
            standardError = process.StandardError.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            standardError = string.Empty;
        }

        return new CommandResult(process.ExitCode, exited, sawWindow, standardError);
    }

    private static bool OutsideKeyExists()
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = hkcu.OpenSubKey(OutsideSubKey);
        return key is not null;
    }

    private static void DeleteOutsideKey()
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = hkcu.OpenSubKey(OutsideSubKey);
        if (key is not null)
        {
            hkcu.DeleteSubKeyTree(OutsideSubKey, throwOnMissingSubKey: false);
        }
    }

    private static byte[] EncodeV5(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[body.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        body.CopyTo(bytes, 2);
        return bytes;
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Command line checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private sealed record CommandResult(
        int ExitCode,
        bool Exited,
        bool SawWindow,
        string StandardError);
}
