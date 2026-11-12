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
}
