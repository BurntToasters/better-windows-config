using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Manifest-byte cases from docs/failure-modes/update-verifier.md (section A). Keys and signatures
// come from the Node signer, so every accepted case is a real minisign signature.
[TestClass]
public sealed class ManifestSignatureTests
{
    private const string Comment = "timestamp:1794102823\tversion:0.2.0";
    private const string Body = "{\"schema\":1,\"version\":\"0.2.0\",\"channel\":\"beta\"}";

    private TempDir _dir = null!;

    [TestInitialize]
    public void Init() => _dir = new TempDir();

    [TestCleanup]
    public void Cleanup() => _dir.Dispose();

    private SigningKey NewKey(string name) => SigningKey.Generate(_dir.Sub(name));

    private SignedFile SignManifest(SigningKey key, string body = Body, string comment = Comment)
    {
        var path = _dir.Write("beta-" + Guid.NewGuid().ToString("N") + ".json", Encoding.UTF8.GetBytes(body));
        return Signing.Sign(path, key, comment);
    }

    private static VerificationResult Verify(string body, string signature, IReadOnlyList<string> keys)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return MinisignVerifier.Verify(stream, signature, keys);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(20)]
    [DataRow(-1)]
    public void Manifest_TamperedByte_Rejected(int index)
    {
        var key = NewKey("key");
        var signed = SignManifest(key);
        var bytes = Encoding.UTF8.GetBytes(Body);
        bytes[index < 0 ? bytes.Length - 1 : index] ^= 0x01;

        using var stream = new MemoryStream(bytes);
        Expect.Rejected(MinisignVerifier.Verify(stream, signed.Envelope, [key.PublicKeyText]), "file");
    }

    [TestMethod]
    public void Manifest_UnlistedKey_Rejected()
    {
        var signer = NewKey("signer");
        var other = NewKey("other");
        var signed = SignManifest(signer);

        Expect.Rejected(Verify(Body, signed.Envelope, [other.PublicKeyText]));
    }

    [TestMethod]
    public void Manifest_WrongKeyId_Rejected()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);
        var wrongId = key.KeyId.Select(b => (byte)(b ^ 0xFF)).ToArray();
        var raw = Sig.WithKeyId(signed.Raw, wrongId);

        Expect.Rejected(Verify(Body, raw, [key.PublicKeyText]), "key id");
    }

    [TestMethod]
    public void Manifest_SignatureOfOtherManifest_Rejected()
    {
        var key = NewKey("key");
        var signedOther = SignManifest(key, body: Body.Replace("0.2.0", "0.3.0"));

        Expect.Rejected(Verify(Body, signedOther.Envelope, [key.PublicKeyText]));
    }

    [TestMethod]
    [DataRow(12)]
    [DataRow(40)]
    public void Manifest_SignatureTruncated_Rejected(int cut)
    {
        var key = NewKey("key");
        var signed = SignManifest(key);

        Expect.Rejected(Verify(Body, signed.Raw[..^cut], [key.PublicKeyText]));
        Expect.Rejected(Verify(Body, signed.Envelope[..^cut], [key.PublicKeyText]));
    }

    [TestMethod]
    public void Manifest_UntrustedComment_Edit_Still_Accepted()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);
        var edited = Sig.WithUntrustedComment(signed.Raw, "untrusted comment: forged by someone else");

        Expect.Accepted(Verify(Body, edited, [key.PublicKeyText]), Comment);
    }

    [TestMethod]
    public void Manifest_TrustedComment_Edit_Rejected()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);
        var edited = Sig.WithTrustedComment(signed.Raw, "timestamp:1794102823\tversion:9.9.9");

        Expect.Rejected(Verify(Body, edited, [key.PublicKeyText]), "global signature");
    }

    [TestMethod]
    public void Manifest_LegacyEd_Rejected()
    {
        var key = NewKey("key");
        var path = _dir.Write("legacy.json", Encoding.UTF8.GetBytes(Body));
        var signed = Signing.Sign(path, key, Comment, legacy: true);

        Expect.Rejected(Verify(Body, signed.Raw, [key.PublicKeyText]), "legacy");
    }

    [TestMethod]
    public void Manifest_UnknownAlgorithm_Rejected()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);
        var packet = Sig.WithAlgorithm(Sig.Packet(signed.Raw), "XX");
        var raw = Sig.WithPacket(signed.Raw, packet);

        Expect.Rejected(Verify(Body, raw, [key.PublicKeyText]), "algorithm");
    }

    [TestMethod]
    public void Manifest_RawAndEnvelope_Both_Accepted()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);

        Expect.Accepted(Verify(Body, signed.Raw, [key.PublicKeyText]), Comment);
        Expect.Accepted(Verify(Body, signed.Envelope, [key.PublicKeyText]), Comment);
    }

    [TestMethod]
    [DataRow("bom")]
    [DataRow("trailing-space")]
    [DataRow("extra-blank-line")]
    [DataRow("leading-space")]
    public void Manifest_Signature_Format_Rejected(string mutation)
    {
        var key = NewKey("key");
        var signed = SignManifest(key);
        var lines = Sig.Lines(signed.Raw);
        var text = mutation switch
        {
            "bom" => "﻿" + signed.Raw,
            "trailing-space" => Sig.Join([lines[0], lines[1] + " ", lines[2], lines[3]]),
            "extra-blank-line" => signed.Raw + "\n",
            "leading-space" => " " + signed.Raw,
            _ => throw new InvalidOperationException(mutation),
        };

        Expect.Rejected(Verify(Body, text, [key.PublicKeyText]));
    }

    [TestMethod]
    public void Empty_Key_List_Rejected()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);

        Expect.Rejected(Verify(Body, signed.Envelope, []), "no trusted public keys");
    }

    [TestMethod]
    public void Rotation_Second_Key_Accepted()
    {
        var oldKey = NewKey("old");
        var newKey = NewKey("new");
        var signed = SignManifest(newKey);

        Expect.Accepted(Verify(Body, signed.Envelope, [oldKey.PublicKeyText, newKey.PublicKeyText]), Comment);
    }

    [TestMethod]
    public void Rotation_Unlisted_Key_Rejected()
    {
        var listed1 = NewKey("listed1");
        var listed2 = NewKey("listed2");
        var unlisted = NewKey("unlisted");
        var signed = SignManifest(unlisted);

        Expect.Rejected(Verify(Body, signed.Envelope, [listed1.PublicKeyText, listed2.PublicKeyText]));
    }

    [TestMethod]
    public void Malformed_Key_Fails_Closed()
    {
        var key = NewKey("key");
        var signed = SignManifest(key);

        Expect.Rejected(Verify(Body, signed.Envelope, ["not a key at all", key.PublicKeyText]), "malformed");
    }

    [TestMethod]
    public void Key_Id_Collision_Still_Accepts()
    {
        var first = NewKey("first");
        var second = NewKey("second");
        var sharedId = first.KeyId;

        // Give both public keys the same key id. The signature must still verify under the key that signed it.
        var firstWithSharedId = first.PublicKeyText;
        var secondWithSharedId = Sig.WithPublicKeyKeyId(second.PublicKeyText, sharedId);
        var signed = SignManifest(second);
        var raw = Sig.WithKeyId(signed.Raw, sharedId);

        Expect.Accepted(Verify(Body, raw, [firstWithSharedId, secondWithSharedId]), Comment);
    }
}
