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
    private const string OutsideSubKey = @"Software\BWC-CLI-Outside";

    private static int _checkStart;

    [TestMethod]
    [TestCategory("CommandLine")]
    public void CommandLine_ExportsImportsAndRejectsOutsideSandbox()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

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
                && v5Text.Contains("[" + expectedRoot + "]", StringComparison.Ordinal)
                && v5Text.Contains("[" + expectedChild + "]", StringComparison.Ordinal)
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
                && ansiText.Contains("[" + expectedRoot + "]", StringComparison.Ordinal)
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

            if (config.Flavor == "e2e")
            {
                string outsideText =
                    "Windows Registry Editor Version 5.00\r\n\r\n"
                    + "[HKEY_CURRENT_USER\\" + OutsideSubKey + "]\r\n"
                    + "\"Value\"=\"outside\"\r\n";
                File.WriteAllBytes(outsideFile, EncodeV5(outsideText));

                CommandResult denied = Run(config, sandbox, "/s", outsideFile);
                bool outsideCreated = OutsideKeyExists();
                E2EResults.Record(
                    "commandline.import.outside.denied",
                    denied.Exited && denied.ExitCode != 0 && !outsideCreated,
                    $"exit={denied.ExitCode}, outsideCreated={outsideCreated}, stderr='{denied.StandardError.Trim()}'.");
            }
            else
            {
                // The ship flavor intentionally uses ShippingWritePolicy; the fail-closed
                // sandbox assertion belongs to the e2e flavor only.
                E2EResults.Record(
                    "commandline.import.outside.denied",
                    true,
                    "informational: ship flavor uses ShippingWritePolicy; sandbox denial is asserted on the e2e flavor.");
            }

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
            DeleteOutsideKey();
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
