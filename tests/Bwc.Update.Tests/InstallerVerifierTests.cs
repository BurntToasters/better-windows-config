using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Installer-file cases from docs/failure-modes/update-verifier.md (section E, installer-level).
[TestClass]
public sealed class InstallerVerifierTests
{
    private const string GoodComment = "timestamp:1794102823\tfile:installer.exe\tversion:0.2.0\tarch:win-x64";

    private TempDir _dir = null!;
    private SigningKey _key = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = new TempDir();
        _key = SigningKey.Generate(_dir.Sub("key"));
    }

    [TestCleanup]
    public void Cleanup() => _dir.Dispose();

    private (string Path, byte[] Bytes, SignedFile Signed) SignInstaller(string comment = GoodComment, byte[]? bytes = null, string name = "installer.bin")
    {
        var payload = bytes ?? Payloads.Pattern(4096);
        var path = _dir.Write(name, payload);
        var signed = Signing.Sign(path, _key, comment);
        return (path, payload, signed);
    }

    private static InstallerExpectation ExpectationFor(
        byte[] bytes,
        SignedFile signed,
        string version = "0.2.0",
        string architecture = Architectures.X64) =>
        new(version, architecture, bytes.Length, Payloads.Sha256Hex(bytes), Sig.Envelope(signed.Raw));

    private Outcome<string> Verify(string path, InstallerExpectation expected) =>
        Verify(path, expected, [_key.PublicKeyText]);

    private static Outcome<string> Verify(string path, InstallerExpectation expected, IReadOnlyList<string> keys)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return InstallerVerifier.Verify(stream, expected, keys);
    }

    [TestMethod]
    public void Installer_Verifies_With_Matching_Expectation()
    {
        var (path, bytes, signed) = SignInstaller();
        var outcome = Verify(path, ExpectationFor(bytes, signed));

        Assert.IsTrue(outcome.Ok, outcome.Detail);
        Assert.AreEqual(GoodComment, outcome.Value);
    }

    [TestMethod]
    public void Installer_Hash_Mismatch_Rejected()
    {
        var (path, bytes, signed) = SignInstaller();
        var expected = ExpectationFor(bytes, signed);

        var tampered = (byte[])bytes.Clone();
        tampered[2048] ^= 0x01;
        File.WriteAllBytes(path, tampered);

        Expect.Fails(Verify(path, expected), UpdateFailure.InstallerHashMismatch);
    }

    [TestMethod]
    public void Installer_Signature_Of_Other_File_Rejected()
    {
        var (path, bytes, _) = SignInstaller(name: "served.bin");
        var (_, _, otherSigned) = SignInstaller(name: "other.bin", bytes: Payloads.Pattern(4096, seed: 9));
        var expected = new InstallerExpectation("0.2.0", Architectures.X64, bytes.Length, Payloads.Sha256Hex(bytes), Sig.Envelope(otherSigned.Raw));

        Expect.Fails(Verify(path, expected), UpdateFailure.InstallerSignatureInvalid);
    }

    [TestMethod]
    public void Installer_Arch_Mismatch_Rejected()
    {
        var (path, bytes, signed) = SignInstaller();
        var expected = ExpectationFor(bytes, signed, architecture: Architectures.Arm64);

        Expect.Fails(Verify(path, expected), UpdateFailure.InstallerCommentMismatch);
    }

    [TestMethod]
    public void Installer_Version_Mismatch_Rejected()
    {
        var (path, bytes, signed) = SignInstaller();
        var expected = ExpectationFor(bytes, signed, version: "0.3.0");

        Expect.Fails(Verify(path, expected), UpdateFailure.InstallerCommentMismatch);
    }

    [TestMethod]
    [DataRow("version:0.2.0")]
    [DataRow("arch:win-x64")]
    [DataRow("file:installer.exe")]
    public void Installer_Comment_Missing_Field_Rejected(string comment)
    {
        var (path, bytes, signed) = SignInstaller(comment: comment);

        Expect.Fails(Verify(path, ExpectationFor(bytes, signed)), UpdateFailure.InstallerCommentMismatch);
    }

    [TestMethod]
    [DataRow("file:..\\evil.exe")]
    [DataRow("file:../evil.exe")]
    [DataRow("file:C:evil.exe")]
    [DataRow("file:sub/evil.exe")]
    [DataRow("file:..")]
    public void Installer_Comment_File_Traversal_Rejected(string fileField)
    {
        var comment = $"{fileField}\tversion:0.2.0\tarch:win-x64";
        var (path, bytes, signed) = SignInstaller(comment: comment);

        Expect.Fails(Verify(path, ExpectationFor(bytes, signed)), UpdateFailure.InstallerCommentMismatch);
    }

    [TestMethod]
    public void Installer_Comment_Duplicate_Field_Rejected()
    {
        var (path, bytes, signed) = SignInstaller(comment: "version:0.2.0\tversion:0.3.0\tarch:win-x64");

        Expect.Fails(Verify(path, ExpectationFor(bytes, signed)), UpdateFailure.InstallerCommentMismatch);
    }

    [TestMethod]
    public void Installer_Size_Mismatch_Rejected()
    {
        var (path, bytes, signed) = SignInstaller();
        var expected = ExpectationFor(bytes, signed) with { Size = bytes.Length + 1 };

        Expect.Fails(Verify(path, expected), UpdateFailure.InstallerSizeMismatch);
    }

    [TestMethod]
    public void Installer_Accepted_Only_With_Trusted_Key()
    {
        var (path, bytes, signed) = SignInstaller();
        var other = SigningKey.Generate(_dir.Sub("other"));

        Expect.Fails(Verify(path, ExpectationFor(bytes, signed), [other.PublicKeyText]), UpdateFailure.InstallerSignatureInvalid);
    }
}
