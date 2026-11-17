using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The stdout lines of BwcUpdate.exe are a contract the Hub and the installer parse (update-elevation.md HN1 to HN7).
// The expected text here is written out by hand and was first run against the reflection-based code of 0.1.2 (anonymous
// types and JsonSerializer.Serialize), where all of it passed. The NativeAOT build writes the same bytes through a
// source-generated context.
[TestClass]
public sealed class StdoutJsonTests
{
    private const string AllFailureNames =
        "None,InvalidOptions,TrustedKeysInvalid,TrustedKeysNotConfigured,UrlRejected,DownloadFailed,Timeout,Canceled,Io,"
        + "ManifestDownload,ManifestTooLarge,ManifestSignatureDownload,ManifestSignatureInvalid,ManifestInvalid,UnsupportedSchema,"
        + "ChannelMismatch,PrereleaseOnStable,ManifestDatedInFuture,ManifestExpired,SameVersion,Downgrade,"
        + "NoInstallerForArchitecture,InstallerDownload,InstallerDownloadInterrupted,InstallerTooLarge,InstallerSizeMismatch,"
        + "InstallerHashMismatch,InstallerSignatureInvalid,InstallerCommentMismatch,ReceiptInvalid,PathRejected,"
        + "ArchitectureMismatch,ProcessesStillRunning,InstallDirUnsafe,InstallerArgumentRejected,InstallerFailed,HubMissing,"
        + "RunningElevated,ElevationDeclined,NotElevated,RunningInOtherSession,DeElevationFailed,Unexpected";

    // The text of every special character in one detail: quote, backslash, slash, < > & ' +, a tab, a control
    // character, an accented letter, and a character outside the BMP.
    private const string SpecialDetail = "a\"b\\c/d<e>f&g'h+i\tj\u0001k\u00e9l\U0001F600m";
    private const string SpecialDetailJson =
        "a\\u0022b\\\\c/d\\u003Ce\\u003Ef\\u0026g\\u0027h\\u002Bi\\tj\\u0001k\\u00E9l\\uD83D\\uDE00m";

    // HN1, HN2
    [TestMethod]
    public void Stdout_Check_Json_Is_Pinned()
    {
        Assert.AreEqual(
            "{\"ok\":true,\"failure\":\"None\",\"detail\":\"\",\"version\":\"0.2.0\",\"notesUrl\":\"https://example.com/notes\"}",
            Check(UpdateFailure.None, string.Empty, "0.2.0", "https://example.com/notes"));
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"UrlRejected\",\"detail\":\"not https\",\"version\":null,\"notesUrl\":null}",
            Check(UpdateFailure.UrlRejected, "not https", null, null));
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"RunningElevated\",\"detail\":\"d\",\"version\":\"1.0.0-beta.1\",\"notesUrl\":null}",
            Check(UpdateFailure.RunningElevated, "d", "1.0.0-beta.1", null));
    }

    // HN1, HN2
    [TestMethod]
    public void Stdout_Download_Json_Is_Pinned()
    {
        Assert.AreEqual(
            "{\"ok\":true,\"failure\":\"None\",\"detail\":\"\",\"version\":\"0.2.0\",\"notesUrl\":\"https://example.com/n\","
            + "\"stagingFolder\":\"C:\\\\Users\\\\x\\\\AppData\\\\Local\\\\Temp\\\\0123\"}",
            Download(UpdateFailure.None, string.Empty, "0.2.0", "https://example.com/n", @"C:\Users\x\AppData\Local\Temp\0123"));
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"InstallerHashMismatch\",\"detail\":\"x\",\"version\":null,\"notesUrl\":null,\"stagingFolder\":null}",
            Download(UpdateFailure.InstallerHashMismatch, "x", null, null, null));
    }

    // HN1
    [TestMethod]
    public void Stdout_Apply_Json_Is_Pinned()
    {
        Assert.AreEqual(
            "{\"ok\":true,\"failure\":\"None\",\"detail\":\"\",\"installerStarted\":true,\"elevatedHelperStarted\":false,\"hubLaunched\":false}",
            Apply(new ApplyOutcome(UpdateFailure.None, string.Empty, InstallerStarted: true, HubLaunched: false)));
        Assert.AreEqual(
            "{\"ok\":true,\"failure\":\"None\",\"detail\":\"\",\"installerStarted\":false,\"elevatedHelperStarted\":true,\"hubLaunched\":false}",
            Apply(new ApplyOutcome(UpdateFailure.None, string.Empty, InstallerStarted: false, HubLaunched: false, ElevatedHelperStarted: true)));
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"ProcessesStillRunning\",\"detail\":\"two\",\"installerStarted\":false,\"elevatedHelperStarted\":false,\"hubLaunched\":true}",
            Apply(new ApplyOutcome(UpdateFailure.ProcessesStillRunning, "two", InstallerStarted: false, HubLaunched: true)));
    }

    // HN1, HN6
    [TestMethod]
    public void Stdout_Simple_Json_Is_Pinned()
    {
        Assert.AreEqual("{\"ok\":true,\"failure\":\"None\",\"detail\":\"Hub started\"}", Simple(UpdateFailure.None, "Hub started"));
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"DeElevationFailed\",\"detail\":\"nothing was started\"}",
            Simple(UpdateFailure.DeElevationFailed, "nothing was started"));
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"Unexpected\",\"detail\":\"unexpected IOException; see the log\"}",
            Simple(UpdateFailure.Unexpected, "unexpected IOException; see the log"));
        Assert.AreEqual(
            "{\"ok\":true,\"failure\":\"None\",\"detail\":\"AppUserModelID set and read back\"}",
            Simple(UpdateFailure.None, "AppUserModelID set and read back"));
    }

    // HN5
    [TestMethod]
    public void Stdout_Progress_Json_Is_Pinned()
    {
        Assert.AreEqual("{\"progress\":{\"received\":4194304,\"total\":104857600}}", Progress(4194304, 104857600));
        Assert.AreEqual("{\"progress\":{\"received\":0,\"total\":-1}}", Progress(0, -1));
        Assert.AreEqual(
            "{\"progress\":{\"received\":9223372036854775807,\"total\":9223372036854775807}}",
            Progress(long.MaxValue, long.MaxValue));
    }

    // HN3
    [TestMethod]
    public void Stdout_Json_Escaping_Is_Pinned()
    {
        string expectedDetail = "\"detail\":\"" + SpecialDetailJson + "\"";

        StringAssert.Contains(Check(UpdateFailure.Io, SpecialDetail, null, null), expectedDetail, StringComparison.Ordinal);
        StringAssert.Contains(Download(UpdateFailure.Io, SpecialDetail, null, null, null), expectedDetail, StringComparison.Ordinal);
        StringAssert.Contains(Apply(new ApplyOutcome(UpdateFailure.Io, SpecialDetail, false, false)), expectedDetail, StringComparison.Ordinal);
        StringAssert.Contains(Simple(UpdateFailure.Io, SpecialDetail), expectedDetail, StringComparison.Ordinal);
        Assert.IsTrue(Check(UpdateFailure.Io, SpecialDetail, SpecialDetail, SpecialDetail).All(c => c is >= ' ' and <= '~'), "the line is ASCII");
        Assert.AreEqual(
            "{\"ok\":false,\"failure\":\"Io\",\"detail\":\"" + SpecialDetailJson + "\",\"version\":null,\"notesUrl\":\"https://x/?a=1\\u0026b=2\"}",
            Check(UpdateFailure.Io, SpecialDetail, null, "https://x/?a=1&b=2"));
        using var parsed = JsonDocument.Parse(Simple(UpdateFailure.Io, SpecialDetail));
        Assert.AreEqual(SpecialDetail, parsed.RootElement.GetProperty("detail").GetString(), "the line parses back to the text");
    }

    // HN4
    [TestMethod]
    public void Stdout_Json_Failure_Names_Are_Stable()
    {
        UpdateFailure[] all = Enum.GetValues<UpdateFailure>();

        Assert.AreEqual(AllFailureNames, string.Join(",", all.Select(failure => failure.ToString())));
        foreach (UpdateFailure failure in all)
        {
            string name = failure.ToString();
            string ok = failure == UpdateFailure.None ? "true" : "false";
            Assert.AreEqual("{\"ok\":" + ok + ",\"failure\":\"" + name + "\",\"detail\":\"d\"}", Simple(failure, "d"));
            Assert.AreEqual(
                "{\"ok\":" + ok + ",\"failure\":\"" + name + "\",\"detail\":\"d\",\"version\":null,\"notesUrl\":null}",
                Check(failure, "d", null, null));
        }
    }

    private static string Check(UpdateFailure failure, string detail, string? version, string? notesUrl) =>
        StdoutJson.Check(failure, detail, version, notesUrl);

    private static string Download(UpdateFailure failure, string detail, string? version, string? notesUrl, string? stagingFolder) =>
        StdoutJson.Download(failure, detail, version, notesUrl, stagingFolder);

    private static string Apply(ApplyOutcome outcome) => StdoutJson.Apply(outcome);

    private static string Simple(UpdateFailure failure, string detail) => StdoutJson.Simple(failure, detail);

    private static string Progress(long received, long total) => StdoutJson.Progress(received, total);
}