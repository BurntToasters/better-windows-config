using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

/// <summary>A TimeProvider frozen at one instant, so date rules are deterministic.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// Manifest content and version policy cases from docs/failure-modes/update-verifier.md (B, C, D).
// Parsing and policy never touch the network; the signature is checked earlier, in the pipeline.
[TestClass]
public sealed class ManifestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static UpdateOptions Options() => new() { RequireHttps = true, Clock = new FixedClock(Now) };

    private static JsonObject ValidManifest(string version = "0.2.0-beta.1", string channel = "beta")
    {
        return new JsonObject
        {
            ["schema"] = 1,
            ["version"] = version,
            ["channel"] = channel,
            ["pubDate"] = "2026-11-10T00:00:00Z",
            ["notesUrl"] = "https://example.invalid/notes",
            ["installers"] = new JsonObject
            {
                [Architectures.X64] = InstallerEntry(),
            },
        };
    }

    private static JsonObject InstallerEntry() => new()
    {
        ["url"] = "https://example.invalid/x64.exe",
        ["size"] = 4096,
        ["sha256"] = new string('a', 64),
        ["signature"] = Sig.Envelope("untrusted comment: test signature\n"),
    };

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static Outcome<UpdateManifest> Parse(JsonNode node) => UpdateManifestParser.Parse(Bytes(node), Options());

    private static UpdateManifest ParseOk(JsonNode node)
    {
        var outcome = Parse(node);
        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        return outcome.Value!;
    }

    [TestMethod]
    public void Manifest_Bom_Rejected()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(ValidManifest())).ToArray();
        Expect.Fails(UpdateManifestParser.Parse(bytes, Options()), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("{} x")]
    [DataRow("[]")]
    [DataRow("{\"a\":}")]
    [DataRow("")]
    public void Manifest_InvalidJson_Rejected(string text)
    {
        Expect.Fails(UpdateManifestParser.Parse(Encoding.UTF8.GetBytes(text), Options()), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Manifest_DuplicateKey_Rejected()
    {
        const string text = "{\"schema\":1,\"version\":\"0.2.0\",\"version\":\"0.3.0\",\"channel\":\"beta\"}";
        Expect.Fails(UpdateManifestParser.Parse(Encoding.UTF8.GetBytes(text), Options()), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Manifest_JsonComment_Rejected()
    {
        const string text = "{ /* comment */ \"schema\": 1 }";
        Expect.Fails(UpdateManifestParser.Parse(Encoding.UTF8.GetBytes(text), Options()), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Manifest_DeepNesting_Rejected()
    {
        var text = "{\"x\":" + new string('[', 40) + new string(']', 40) + "}";
        Expect.Fails(UpdateManifestParser.Parse(Encoding.UTF8.GetBytes(text), Options()), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Unsupported_Schema_Rejected()
    {
        var manifest = ValidManifest();
        manifest["schema"] = 2;
        Expect.Fails(Parse(manifest), UpdateFailure.UnsupportedSchema);
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("version")]
    [DataRow("channel")]
    [DataRow("pubDate")]
    [DataRow("installers")]
    public void Missing_Field_Rejected(string field)
    {
        var manifest = ValidManifest();
        manifest.Remove(field);
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    [DataRow("v0.1.0")]
    [DataRow("0.1")]
    [DataRow("01.0.0")]
    [DataRow("0.1.0-01")]
    [DataRow("0.1.0+")]
    [DataRow(" 0.1.0")]
    public void Invalid_Version_Rejected(string version)
    {
        var manifest = ValidManifest();
        manifest["version"] = version;
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Unknown_Channel_Rejected()
    {
        var manifest = ValidManifest(channel: "nightly");
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Channel_Mismatch_Rejected()
    {
        var manifest = ParseOk(ValidManifest(channel: "beta"));
        Expect.Fails(
            UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Stable, SemVer.Parse("0.1.0"), Architectures.X64),
            UpdateFailure.ChannelMismatch);
    }

    [TestMethod]
    public void Prerelease_On_Stable_Rejected()
    {
        var manifest = ParseOk(ValidManifest(version: "0.2.0-beta.1", channel: "stable"));
        Expect.Fails(
            UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Stable, SemVer.Parse("0.1.0"), Architectures.X64),
            UpdateFailure.PrereleaseOnStable);
    }

    [TestMethod]
    public void Release_On_Beta_Accepted()
    {
        var manifest = ParseOk(ValidManifest(version: "0.2.0", channel: "beta"));
        Assert.IsTrue(UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Beta, SemVer.Parse("0.1.0"), Architectures.X64).Ok);
    }

    [TestMethod]
    [DataRow("2026-11-10")]
    [DataRow("2026-11-10T00:00:00+01:00")]
    [DataRow("2026-11-10 00:00:00Z")]
    [DataRow("2026-11-10T00:00:00")]
    [DataRow("2026-13-10T00:00:00Z")]
    public void PubDate_Format_Rejected(string pubDate)
    {
        var manifest = ValidManifest();
        manifest["pubDate"] = pubDate;
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void PubDate_Far_Future_Rejected()
    {
        var manifest = ValidManifest();
        manifest["pubDate"] = Now.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestDatedInFuture);
    }

    [TestMethod]
    public void PubDate_Small_Skew_Accepted()
    {
        var manifest = ValidManifest();
        manifest["pubDate"] = Now.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(Parse(manifest).Ok);
    }

    [TestMethod]
    public void PubDate_Old_Accepted_By_Design()
    {
        var manifest = ValidManifest();
        manifest["pubDate"] = "2019-01-01T00:00:00Z";
        Assert.IsTrue(Parse(manifest).Ok, "freshness is not enforced; the version rule stops replay");
    }

    [TestMethod]
    public void Notes_Url_Http_Rejected()
    {
        var manifest = ValidManifest();
        manifest["notesUrl"] = "http://example.invalid/notes";
        Expect.Fails(Parse(manifest), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Unknown_Fields_Ignored()
    {
        var manifest = ValidManifest();
        manifest["futureRoot"] = new JsonObject { ["anything"] = true };
        ((JsonObject)manifest["installers"]![Architectures.X64]!)["futureEntry"] = "ignored";
        Assert.IsTrue(Parse(manifest).Ok);
    }

    [TestMethod]
    public void Missing_Architecture_Rejected()
    {
        var manifest = ValidManifest();
        var installers = (JsonObject)manifest["installers"]!;
        installers.Remove(Architectures.X64);
        installers[Architectures.Arm64] = InstallerEntry();
        var parsed = ParseOk(manifest);

        Expect.Fails(
            UpdatePolicy.SelectInstaller(parsed, UpdateChannel.Beta, SemVer.Parse("0.1.0"), Architectures.X64),
            UpdateFailure.NoInstallerForArchitecture);
    }

    [TestMethod]
    [DataRow("size-string")]
    [DataRow("sha-missing")]
    [DataRow("url-number")]
    [DataRow("signature-number")]
    public void Installer_Field_Type_Rejected(string mutation)
    {
        var manifest = ValidManifest();
        var entry = (JsonObject)((JsonObject)manifest["installers"]!)[Architectures.X64]!;
        switch (mutation)
        {
            case "size-string":
                entry["size"] = "4096";
                break;
            case "sha-missing":
                entry.Remove("sha256");
                break;
            case "url-number":
                entry["url"] = 7;
                break;
            case "signature-number":
                entry["signature"] = 7;
                break;
        }

        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(1073741825L)]
    public void Installer_Size_Out_Of_Range_Rejected(long size)
    {
        var manifest = ValidManifest();
        ((JsonObject)((JsonObject)manifest["installers"]!)[Architectures.X64]!)["size"] = size;
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Installer_Size_Fraction_Rejected()
    {
        var manifest = ValidManifest();
        ((JsonObject)((JsonObject)manifest["installers"]!)[Architectures.X64]!)["size"] = 1.5;
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    [DataRow("uppercase")]
    [DataRow("short")]
    [DataRow("non-hex")]
    public void Sha256_Format_Rejected(string kind)
    {
        var sha = kind switch
        {
            "uppercase" => new string('A', 64),
            "short" => new string('a', 63),
            _ => new string('g', 64),
        };
        var manifest = ValidManifest();
        ((JsonObject)((JsonObject)manifest["installers"]!)[Architectures.X64]!)["sha256"] = sha;
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Installer_Signature_Not_Envelope_Rejected()
    {
        var manifest = ValidManifest();
        ((JsonObject)((JsonObject)manifest["installers"]!)[Architectures.X64]!)["signature"] = Convert.ToBase64String("hello"u8);
        Expect.Fails(Parse(manifest), UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public void Unknown_Architecture_Key_Ignored()
    {
        var manifest = ValidManifest();
        ((JsonObject)manifest["installers"]!)["win-x86"] = new JsonObject { ["url"] = 1 };
        var parsed = ParseOk(manifest);

        Assert.IsTrue(UpdatePolicy.SelectInstaller(parsed, UpdateChannel.Beta, SemVer.Parse("0.1.0"), Architectures.X64).Ok);
        Assert.IsFalse(parsed.Installers.ContainsKey("win-x86"));
    }

    [TestMethod]
    public void Same_Version_Rejected()
    {
        var manifest = ParseOk(ValidManifest(version: "0.2.0-beta.1"));
        Expect.Fails(
            UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Beta, SemVer.Parse("0.2.0-beta.1"), Architectures.X64),
            UpdateFailure.SameVersion);
    }

    [TestMethod]
    public void Same_Precedence_Build_Metadata_Rejected()
    {
        var manifest = ParseOk(ValidManifest(version: "0.2.0-beta.1"));
        Expect.Fails(
            UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Beta, SemVer.Parse("0.2.0-beta.1+build.5"), Architectures.X64),
            UpdateFailure.SameVersion);
    }

    [TestMethod]
    public void Downgrade_Rejected()
    {
        var manifest = ParseOk(ValidManifest(version: "0.2.0-beta.1"));
        Expect.Fails(
            UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Beta, SemVer.Parse("0.3.0-beta.1"), Architectures.X64),
            UpdateFailure.Downgrade);
    }

    [TestMethod]
    public void Release_Upgrades_Prerelease_Installed()
    {
        var manifest = ParseOk(ValidManifest(version: "0.2.0", channel: "beta"));
        Assert.IsTrue(UpdatePolicy.SelectInstaller(manifest, UpdateChannel.Beta, SemVer.Parse("0.2.0-beta.1"), Architectures.X64).Ok);
    }
}
