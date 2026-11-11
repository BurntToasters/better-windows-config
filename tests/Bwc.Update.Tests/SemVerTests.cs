using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

[TestClass]
public sealed class SemVerTests
{
    [TestMethod]
    [DataRow("v0.1.0")]
    [DataRow("0.1")]
    [DataRow("01.0.0")]
    [DataRow("0.1.0-01")]
    [DataRow("0.1.0-alpha..1")]
    [DataRow("0.1.0+")]
    [DataRow(" 0.1.0")]
    [DataRow("0.1.0 ")]
    [DataRow("0.1.0-")]
    [DataRow("")]
    [DataRow("0.1.0-beta_1")]
    public void Invalid_Version_Rejected(string text)
    {
        Assert.IsFalse(SemVer.TryParse(text, out _), $"'{text}' must not parse");
    }

    [TestMethod]
    [DataRow("1.0.0-01")]
    [DataRow("1.0.0-alpha..1")]
    [DataRow("1.0.0-")]
    public void Invalid_Prerelease_Identifier_Rejected(string text)
    {
        Assert.IsFalse(SemVer.TryParse(text, out _));
    }

    [TestMethod]
    public void Prerelease_Precedence_Matches_Spec()
    {
        // The ordered example list from SemVer 2.0.0, section 11.
        string[] ordered =
        [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        ];

        for (var i = 0; i + 1 < ordered.Length; i++)
        {
            var lower = Parse(ordered[i]);
            var higher = Parse(ordered[i + 1]);
            Assert.IsTrue(lower.CompareTo(higher) < 0, $"{ordered[i]} must be below {ordered[i + 1]}");
            Assert.IsTrue(higher.CompareTo(lower) > 0, $"{ordered[i + 1]} must be above {ordered[i]}");
        }
    }

    [TestMethod]
    public void Numeric_Prerelease_Compares_Numerically()
    {
        Assert.IsTrue(Parse("0.1.0-beta.10").CompareTo(Parse("0.1.0-beta.2")) > 0);
    }

    [TestMethod]
    public void Build_Metadata_Ignored_For_Precedence()
    {
        Assert.AreEqual(0, Parse("0.1.0+a").CompareTo(Parse("0.1.0+b")));
        Assert.AreEqual(0, Parse("0.1.0+a").CompareTo(Parse("0.1.0")));
    }

    [TestMethod]
    public void Huge_Numeric_Identifiers_Compare()
    {
        Assert.IsTrue(Parse("18446744073709551616.0.0").CompareTo(Parse("18446744073709551615.0.0")) > 0);
        Assert.IsTrue(Parse("1.0.0-99999999999999999999999").CompareTo(Parse("1.0.0-9999999999999999")) > 0);
    }

    [TestMethod]
    public void Release_Upgrades_Prerelease()
    {
        Assert.IsTrue(Parse("0.2.0").CompareTo(Parse("0.2.0-beta.1")) > 0);
    }

    [TestMethod]
    public void Parsed_Version_Round_Trips_Text()
    {
        Assert.AreEqual("0.1.0-beta.1+build.5", Parse("0.1.0-beta.1+build.5").ToString());
    }

    private static SemVer Parse(string text)
    {
        Assert.IsTrue(SemVer.TryParse(text, out var version), $"'{text}' must parse");
        return version!;
    }
}
