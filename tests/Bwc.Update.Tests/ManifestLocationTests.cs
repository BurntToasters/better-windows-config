using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The signature URL is the manifest URL with ".minisig" inserted before any query (section R).
[TestClass]
public sealed class ManifestLocationTests
{
    [TestMethod]
    public void Signature_Url_Without_Query()
    {
        Assert.AreEqual(
            "https://example.invalid/bwc/beta.json.minisig",
            ManifestLocation.SignatureUrlFor("https://example.invalid/bwc/beta.json"));
    }

    [TestMethod]
    public void Signature_Url_Inserts_Before_Query()
    {
        Assert.AreEqual(
            "https://example.invalid/bwc/beta.json.minisig?token=abc&v=2",
            ManifestLocation.SignatureUrlFor("https://example.invalid/bwc/beta.json?token=abc&v=2"));
    }

    // The Hub opens the release notes link from the manifest only when this check passes (UPD-1 in hub.md).
    [TestMethod]
    [DataRow("https://github.com/BurntToasters/better-windows-config/releases/tag/v0.2.0")]
    [DataRow("https://example.invalid/notes?tag=v0.2.0&lang=en")]
    public void Notes_Url_Https_Accepted(string url)
    {
        Assert.IsTrue(ManifestLocation.IsOpenableNotesUrl(url), url);
    }

    [TestMethod]
    [DataRow("http://example.invalid/notes")]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///C:/Windows/notes.html")]
    [DataRow("\\\\server\\share\\notes.html")]
    [DataRow("ms-settings:windowsupdate")]
    [DataRow("/notes")]
    [DataRow("notes.html")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("https://")]
    [DataRow("https://user:secret@example.invalid/notes")]
    [DataRow("https://example.invalid/no tes")]
    [DataRow(" https://example.invalid/notes")]
    [DataRow("https://example.invalid/notes\r\nhttps://evil.invalid/")]
    public void Notes_Url_Not_Https_Or_Not_Absolute_Refused(string url)
    {
        Assert.IsFalse(ManifestLocation.IsOpenableNotesUrl(url), url);
    }

    [TestMethod]
    public void Notes_Url_Null_Refused()
    {
        Assert.IsFalse(ManifestLocation.IsOpenableNotesUrl(null));
    }
}
