using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Command-line cases from docs/failure-modes/update-verifier.md (sections I, P and HL3) and
// docs/contracts/update-cli.md.
[TestClass]
public sealed class UpdateArgumentsTests
{
    private const string Receipt = @"C:\Users\test\AppData\Local\Temp\BWC-Update\0123456789abcdef0123456789abcdef\receipt.json";
    private const string Installer = @"C:\Users\test\AppData\Local\Temp\BWC-Update\0123456789abcdef0123456789abcdef\installer.exe";

    [TestMethod]
    public void Help_Lists_Modes()
    {
        Assert.AreEqual(UpdateMode.Help, UpdateArguments.Parse([]).Mode);
        Assert.AreEqual(UpdateMode.Help, UpdateArguments.Parse(["--help"]).Mode);
        Assert.AreEqual(UpdateMode.Help, UpdateArguments.Parse(["-h"]).Mode);
        StringAssert.Contains(UpdateArguments.UsageText, "--check");
        StringAssert.Contains(UpdateArguments.UsageText, "--download");
        StringAssert.Contains(UpdateArguments.UsageText, "--apply");
        StringAssert.Contains(UpdateArguments.UsageText, "--close-running");
        StringAssert.Contains(UpdateArguments.UsageText, "--wait-pid");
    }

    [TestMethod]
    public void Unknown_Option_Usage()
    {
        var parsed = UpdateArguments.Parse(["--check", "--bogus", "x"]);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode);
        StringAssert.Contains(parsed.Error, "--bogus");
    }

    [TestMethod]
    public void Duplicate_Option_Usage()
    {
        var parsed = UpdateArguments.Parse(
            ["--check", "--manifest-url", "https://a/x", "--manifest-url", "https://b/x", "--signature-url", "https://a/x.minisig", "--channel", "beta", "--current-version", "0.1.0"]);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode);
        StringAssert.Contains(parsed.Error, "--manifest-url");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-5")]
    [DataRow("abc")]
    [DataRow("99999999999")]
    public void Wait_Pid_Invalid_Usage(string pid)
    {
        var parsed = UpdateArguments.Parse(ApplyArgs("--wait-pid", pid));

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode, parsed.Error);
    }

    [TestMethod]
    public void Check_Channel_Usage()
    {
        var missing = UpdateArguments.Parse(["--check", "--manifest-url", "https://a/x", "--signature-url", "https://a/x.minisig", "--current-version", "0.1.0"]);
        var unknown = UpdateArguments.Parse(["--check", "--manifest-url", "https://a/x", "--signature-url", "https://a/x.minisig", "--channel", "nightly", "--current-version", "0.1.0"]);

        Assert.AreEqual(UpdateMode.Invalid, missing.Mode);
        Assert.AreEqual(UpdateMode.Invalid, unknown.Mode);
    }

    [TestMethod]
    public void Check_Http_Manifest_Refused()
    {
        var parsed = UpdateArguments.Parse(
            ["--check", "--manifest-url", "http://example.invalid/beta.json", "--signature-url", "http://example.invalid/beta.json.minisig", "--channel", "beta", "--current-version", "0.1.0"]);

        Assert.AreEqual(UpdateMode.Check, parsed.Mode, parsed.Error);
        Expect.Fails(UrlPolicy.Validate(parsed.Single("--manifest-url")!, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Download_Arguments_Parse()
    {
        var parsed = UpdateArguments.Parse(
            ["--download", "--manifest-url", "https://a/beta.json", "--signature-url", "https://a/beta.json.minisig", "--channel", "beta", "--current-version", "0.1.0"]);

        Assert.AreEqual(UpdateMode.Download, parsed.Mode, parsed.Error);
        Assert.AreEqual("https://a/beta.json", parsed.Single("--manifest-url"));
        Assert.AreEqual("beta", parsed.Single("--channel"));
    }

    [TestMethod]
    [DataRow("--download", "--manifest-url", "https://a/beta.json", "--channel", "beta", "--current-version", "0.1.0")]
    [DataRow("--download", "--manifest-url", "https://a/beta.json", "--signature-url", "https://a/beta.json.minisig", "--channel", "nightly", "--current-version", "0.1.0")]
    public void Download_Usage_Errors(params string[] args)
    {
        var parsed = UpdateArguments.Parse(args);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode, parsed.Error);
    }

    [TestMethod]
    public void Apply_Arguments_Parse()
    {
        var parsed = UpdateArguments.Parse(
        [
            "--apply", Installer,
            "--receipt", Receipt,
            "--current-version", "0.1.0",
            "--install-dir", @"C:\Program Files\Better Windows Config",
            "--wait-pid", "12",
            "--wait-pid", "13",
            "--installer-switch", "/ALLUSERS",
            "--installer-switch", "/RUNID=abc",
        ]);

        Assert.AreEqual(UpdateMode.Apply, parsed.Mode, parsed.Error);
        Assert.AreEqual(Installer, parsed.Single("--apply"));
        CollectionAssert.AreEqual(new[] { "12", "13" }, parsed.Multiple("--wait-pid").ToArray());
        CollectionAssert.AreEqual(new[] { "/ALLUSERS", "/RUNID=abc" }, parsed.Multiple("--installer-switch").ToArray());
    }

    [TestMethod]
    public void Apply_Relocated_Flag_Is_Gone()
    {
        var parsed = UpdateArguments.Parse(ApplyArgs("--relocated", null));

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode, "the helper no longer copies itself, so the flag is unknown");
    }

    [TestMethod]
    public void Apply_Without_Receipt_Is_Usage_Error()
    {
        var parsed = UpdateArguments.Parse(
            ["--apply", Installer, "--current-version", "0.1.0", "--install-dir", @"C:\Program Files\BWC"]);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode);
        StringAssert.Contains(parsed.Error, "--receipt");
    }

    [TestMethod]
    public void Apply_Value_Cannot_Be_Another_Option()
    {
        var parsed = UpdateArguments.Parse(["--apply", "--receipt", Receipt]);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode);
    }

    [TestMethod]
    public void Close_Running_Arguments_Parse()
    {
        var parsed = UpdateArguments.Parse(
            ["--close-running", "--install-dir", @"C:\Program Files\BWC", "--exclude-pid", "41", "--exclude-pid", "42", "--timeout-seconds", "10"]);

        Assert.AreEqual(UpdateMode.CloseRunning, parsed.Mode, parsed.Error);
        Assert.AreEqual(@"C:\Program Files\BWC", parsed.Single("--install-dir"));
        CollectionAssert.AreEqual(new[] { "41", "42" }, parsed.Multiple("--exclude-pid").ToArray());
        Assert.AreEqual("10", parsed.Single("--timeout-seconds"));
    }

    [TestMethod]
    [DataRow("missing-install-dir")]
    [DataRow("pid-zero")]
    [DataRow("pid-text")]
    [DataRow("timeout-zero")]
    [DataRow("timeout-too-large")]
    [DataRow("unknown-option")]
    [DataRow("install-dir-twice")]
    [DataRow("wait-pid-not-allowed")]
    public void CloseRunning_Usage_Errors(string kind)
    {
        var args = kind switch
        {
            "missing-install-dir" => new[] { "--close-running" },
            "pid-zero" => new[] { "--close-running", "--install-dir", @"C:\BWC", "--exclude-pid", "0" },
            "pid-text" => new[] { "--close-running", "--install-dir", @"C:\BWC", "--exclude-pid", "abc" },
            "timeout-zero" => new[] { "--close-running", "--install-dir", @"C:\BWC", "--timeout-seconds", "0" },
            "timeout-too-large" => new[] { "--close-running", "--install-dir", @"C:\BWC", "--timeout-seconds", "3601" },
            "unknown-option" => new[] { "--close-running", "--install-dir", @"C:\BWC", "--force" },
            "install-dir-twice" => new[] { "--close-running", "--install-dir", @"C:\A", "--install-dir", @"C:\B" },
            _ => new[] { "--close-running", "--install-dir", @"C:\BWC", "--wait-pid", "5" },
        };

        var parsed = UpdateArguments.Parse(args);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode, parsed.Error);
    }

    [TestMethod]
    public void WaitPid_Arguments_Parse()
    {
        var parsed = UpdateArguments.Parse(["--wait-pid", "4242", "--timeout-seconds", "30"]);

        Assert.AreEqual(UpdateMode.WaitPid, parsed.Mode, parsed.Error);
        Assert.AreEqual("4242", parsed.Single("--wait-pid"));
        Assert.AreEqual("30", parsed.Single("--timeout-seconds"));
    }

    [TestMethod]
    [DataRow("missing-pid")]
    [DataRow("pid-zero")]
    [DataRow("pid-text")]
    [DataRow("timeout-zero")]
    [DataRow("timeout-too-large")]
    [DataRow("two-pids")]
    [DataRow("install-dir-not-allowed")]
    public void WaitPid_Usage_Errors(string kind)
    {
        var args = kind switch
        {
            "missing-pid" => new[] { "--wait-pid" },
            "pid-zero" => new[] { "--wait-pid", "0" },
            "pid-text" => new[] { "--wait-pid", "abc" },
            "timeout-zero" => new[] { "--wait-pid", "5", "--timeout-seconds", "0" },
            "timeout-too-large" => new[] { "--wait-pid", "5", "--timeout-seconds", "3601" },
            "two-pids" => new[] { "--wait-pid", "5", "--wait-pid", "6" },
            _ => new[] { "--wait-pid", "5", "--install-dir", @"C:\BWC" },
        };

        var parsed = UpdateArguments.Parse(args);

        Assert.AreEqual(UpdateMode.Invalid, parsed.Mode, parsed.Error);
    }

    private static string[] ApplyArgs(string name, string? value) =>
        value is null
            ?
            [
                "--apply", Installer,
                "--receipt", Receipt,
                "--current-version", "0.1.0",
                "--install-dir", @"C:\Program Files\BWC",
                name,
            ]
            :
            [
                "--apply", Installer,
                "--receipt", Receipt,
                "--current-version", "0.1.0",
                "--install-dir", @"C:\Program Files\BWC",
                name, value,
            ];
}
