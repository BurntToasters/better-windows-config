using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Small rules around the elevated helper and --launch-hub (docs/failure-modes/update-elevation.md: LH, NL, CD).
[TestClass]
public sealed class ElevationSupportTests
{
    private const string OwnDir = @"C:\Program Files\Better Windows Config";

    // LH1
    [TestMethod]
    public void Launch_Elevated_Uses_Shell_Integrity()
    {
        var host = new FakeHost { Elevated = true };

        var result = HubLauncher.Launch(host, OwnDir, UpdateFailure.InstallerFailed);

        Assert.AreEqual(UpdateFailure.None, result.Failure, result.Detail);
        Assert.AreEqual(1, host.ShellStarted.Count, "the Hub starts through the shell-integrity call");
        Assert.AreEqual(0, host.Started.Count, "never the plain start, which would give an elevated Hub");
    }

    // LH2
    [TestMethod]
    public void Launch_Not_Elevated_Starts_Hub()
    {
        var host = new FakeHost { Elevated = false };

        var result = HubLauncher.Launch(host, OwnDir, null);

        Assert.AreEqual(UpdateFailure.None, result.Failure, result.Detail);
        Assert.AreEqual(1, host.ShellStarted.Count, "the host decides: it starts normally when the token is not elevated");
        CollectionAssert.AreEqual(Array.Empty<string>(), host.ShellStarted[0].Args.ToArray(), "no result argument when none is given");
    }

    // LH3
    [TestMethod]
    public void Launch_Cannot_DeElevate_Launches_Nothing()
    {
        var host = new FakeHost { Elevated = true, ShellResult = ShellStart.CannotDeElevate };

        var result = HubLauncher.Launch(host, OwnDir, UpdateFailure.InstallerFailed);

        Assert.AreEqual(UpdateFailure.DeElevationFailed, result.Failure, result.Detail);
        Assert.AreEqual(0, host.Started.Count, "no fallback to a plain start");
    }

    // LH4
    [TestMethod]
    public void Launch_Hub_Missing_Reported()
    {
        var host = new FakeHost { ShellResult = ShellStart.FileMissing };

        var result = HubLauncher.Launch(host, OwnDir, null);

        Assert.AreEqual(UpdateFailure.HubMissing, result.Failure, result.Detail);
    }

    // LH6
    [TestMethod]
    public void Launch_Hub_Path_And_Arguments_Exact()
    {
        var host = new FakeHost { Elevated = true };

        _ = HubLauncher.Launch(host, OwnDir, UpdateFailure.ElevationDeclined);

        var start = host.ShellStarted.Single();
        Assert.AreEqual(Path.Combine(OwnDir, "BetterWindowsConfig.exe"), start.Exe);
        Assert.AreEqual(OwnDir, start.WorkDir);
        CollectionAssert.AreEqual(new[] { "--update-result", "ElevationDeclined" }, start.Args.ToArray());
    }

    // LH7
    [TestMethod]
    public void Launch_Host_Exception_Is_Hub_Missing_Not_A_Crash()
    {
        var host = new ThrowingHost();

        var result = HubLauncher.Launch(host, OwnDir, null);

        Assert.AreEqual(UpdateFailure.HubMissing, result.Failure, result.Detail);
    }

    // LH5
    [TestMethod]
    [DataRow("InstallerFailed", true)]
    [DataRow("ElevationDeclined", true)]
    [DataRow("RunningInOtherSession", true)]
    [DataRow("NotElevated", true)]
    [DataRow("DeElevationFailed", true)]
    [DataRow("Unexpected", true)]
    [DataRow("None", false)]
    [DataRow("5", false)]
    [DataRow("", false)]
    [DataRow("installerfailed", false)]
    [DataRow("Not A Code", false)]
    [DataRow("Bad\"Code", false)]
    [DataRow("InstallerFailed,Unexpected", false)]
    [DataRow("0x1", false)]
    [DataRow("NoSuchFailure", false)]
    public void LaunchHub_Update_Result_Validation(string value, bool accepted)
    {
        var parsed = UpdateArguments.Parse(["--launch-hub", "--update-result", value]);

        Assert.AreEqual(accepted ? UpdateMode.LaunchHub : UpdateMode.Invalid, parsed.Mode, parsed.Error);
        if (accepted)
        {
            Assert.IsTrue(UpdateArguments.TryParseUpdateResult(parsed.Single("--update-result"), out var failure));
            Assert.AreEqual(value, failure.ToString());
        }
    }

    // LH8
    [TestMethod]
    public void LaunchHub_Arguments_Parse()
    {
        Assert.AreEqual(UpdateMode.LaunchHub, UpdateArguments.Parse(["--launch-hub"]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--launch-hub", "--update-result"]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--launch-hub", "--bogus", "x"]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--launch-hub", "extra"]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--launch-hub", "--install-dir", @"C:\x"]).Mode, "no folder option exists");
        Assert.AreEqual(
            UpdateMode.Invalid,
            UpdateArguments.Parse(["--launch-hub", "--update-result", "Unexpected", "--update-result", "Unexpected"]).Mode);
    }

    // EA2
    [TestMethod]
    public void ApplyElevated_Arguments_Parse()
    {
        var folder = @"C:\Users\x\AppData\Local\Temp\BWC-Update\0123456789abcdef0123456789abcdef";

        var parsed = UpdateArguments.Parse(["--apply-elevated", folder]);

        Assert.AreEqual(UpdateMode.ApplyElevated, parsed.Mode, parsed.Error);
        Assert.AreEqual(folder, parsed.Single("--apply-elevated"));
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--apply-elevated"]).Mode, "a folder is required");
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--apply-elevated", "relative\\folder"]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--apply-elevated", folder, "--apply-elevated", folder]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--apply-elevated", folder, "--bogus", "x"]).Mode);
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--apply-elevated", folder, "extra"]).Mode);
    }

    // EA3
    [TestMethod]
    [DataRow("--install-dir", @"C:\Anywhere")]
    [DataRow("--current-version", "0.0.1")]
    [DataRow("--installer-switch", "/ALLUSERS")]
    [DataRow("--wait-pid", "4")]
    [DataRow("--wait-timeout-ms", "5")]
    [DataRow("--receipt", @"C:\x\receipt.json")]
    [DataRow("--apply", @"C:\x\installer.exe")]
    public void ApplyElevated_Rejects_Caller_Options(string option, string value)
    {
        var folder = @"C:\Users\x\AppData\Local\Temp\BWC-Update\0123456789abcdef0123456789abcdef";

        var parsed = UpdateArguments.Parse(["--apply-elevated", folder, option, value]);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode);
        StringAssert.Contains(parsed.Error, option);
    }

    // NL1 to NL3
    [TestMethod]
    public void Logging_Policy_Matrix()
    {
        foreach (var mode in Enum.GetValues<UpdateMode>())
        {
            switch (mode)
            {
                case UpdateMode.Help:
                case UpdateMode.Invalid:
                    Assert.IsFalse(UpdateLogging.ShouldInitialize(mode, elevated: false), mode + " writes no log");
                    Assert.IsFalse(UpdateLogging.ShouldInitialize(mode, elevated: true), mode + " writes no log");
                    break;
                case UpdateMode.ApplyElevated:
                    Assert.IsFalse(UpdateLogging.ShouldInitialize(mode, elevated: true), "--apply-elevated never logs");
                    Assert.IsFalse(UpdateLogging.ShouldInitialize(mode, elevated: false), "not even without elevation (the E2E flavor)");
                    break;
                default:
                    Assert.IsTrue(UpdateLogging.ShouldInitialize(mode, elevated: false), mode + " logs when not elevated");
                    Assert.IsFalse(UpdateLogging.ShouldInitialize(mode, elevated: true), mode + " writes no log when elevated");
                    break;
            }
        }
    }

    // CD1
    [TestMethod]
    public void Failure_Names_For_Elevation()
    {
        foreach (var name in new[] { "ElevationDeclined", "NotElevated", "RunningInOtherSession", "DeElevationFailed" })
        {
            Assert.IsTrue(Enum.TryParse<UpdateFailure>(name, out var failure) && failure.ToString() == name, name);
            Assert.IsTrue(UpdateArguments.TryParseUpdateResult(name, out _), name + " is a valid --update-result");
        }
    }

    // CD2
    [TestMethod]
    [DataRow("0.1.2", "0.1.2")]
    [DataRow("0.1.2+abc123", "0.1.2")]
    [DataRow("0.2.0-beta.1+0123abc.dirty", "0.2.0-beta.1")]
    [DataRow(" 0.1.2 ", "0.1.2")]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void Helper_Version_Strips_Metadata(string? text, string? expected)
    {
        Assert.AreEqual(expected, HelperVersion.FromInformational(text));
    }

    // CD3
    [TestMethod]
    [DataRow(@"C:\Program Files\Better Windows Config", @"C:\Program Files", true)]
    [DataRow(@"C:\Program Files\Better Windows Config\bin", @"C:\Program Files", true)]
    [DataRow(@"c:\program files\better windows config", @"C:\Program Files", true)]
    [DataRow(@"C:\Program Files X\Better Windows Config", @"C:\Program Files", false)]
    [DataRow(@"D:\Program Files\Better Windows Config", @"C:\Program Files", false)]
    [DataRow(@"C:\Program Files", @"C:\Program Files", false)]
    [DataRow(@"C:\Program Files\Better Windows Config\..\..\Users\x", @"C:\Program Files", false)]
    [DataRow(@"Better Windows Config", @"C:\Program Files", false)]
    [DataRow(@"C:\Program Files\Better Windows Config", @"", false)]
    [DataRow(@"", @"C:\Program Files", false)]
    public void Program_Files_Rule(string dir, string programFiles, bool inside)
    {
        Assert.AreEqual(inside, ProgramFilesRule.IsUnder(dir, programFiles));
    }

    private sealed class ThrowingHost : IUpdateProcessHost
    {
        public string HelperDirectory => OwnDir;

        public IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir) => [];

        public bool IsElevated() => true;

        public ElevatedStart StartElevatedHelper(string helperExe, string stagingFolder) => throw new NotSupportedException();

        public bool StartInstaller(string installerPath, string arguments, string tempFolder) => throw new NotSupportedException();

        public bool StartDetached(string exePath, IReadOnlyList<string> arguments, string workingDirectory) => throw new NotSupportedException();

        public ShellStart StartAtShellIntegrity(string exePath, IReadOnlyList<string> arguments, string workingDirectory) =>
            throw new System.ComponentModel.Win32Exception(5, "simulated");
    }
}
