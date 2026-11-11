using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Command-line cases from docs/failure-modes/update-verifier.md (section I and HL3).
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
        StringAssert.Contains(UpdateArguments.UsageText, "--apply");
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
            "--relocated",
        ]);

        Assert.AreEqual(UpdateMode.Apply, parsed.Mode, parsed.Error);
        Assert.AreEqual(Installer, parsed.Single("--apply"));
        CollectionAssert.AreEqual(new[] { "12", "13" }, parsed.Multiple("--wait-pid").ToArray());
        CollectionAssert.AreEqual(new[] { "/ALLUSERS" }, parsed.Multiple("--installer-switch").ToArray());
        Assert.IsTrue(parsed.Has("--relocated"));
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

    private static string[] ApplyArgs(string name, string value) =>
    [
        "--apply", Installer,
        "--receipt", Receipt,
        "--current-version", "0.1.0",
        "--install-dir", @"C:\Program Files\BWC",
        name, value,
    ];
}
