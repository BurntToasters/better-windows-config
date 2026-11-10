using System.Security.Cryptography;
using Bwc.Spike.Minisign;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Spike.Minisign.Tests;

// Every case here is listed in ../../FAILURE-MODES.md. Keys and signatures come from the
// Node CLI (node/minisign.mjs) so the tests prove Node signs and .NET verifies.
[TestClass]
public sealed class VerifierTests
{
    private const string Comment = "timestamp:1794102823\tfile:payload.bin\tversion:1.2.3";

    private TempDir _dir = null!;

    [TestInitialize]
    public void Init() => _dir = new TempDir();

    [TestCleanup]
    public void Cleanup() => _dir.Dispose();

    private SigningKey NewKey(string name) => SigningKey.Generate(_dir.Sub(name));

    private string Payload(string name = "payload.bin", int length = 4096) =>
        _dir.Write(name, Payloads.Pattern(length));

    // ---- Happy paths ------------------------------------------------------

    [TestMethod]
    public void Node_Signed_File_Verifies_Via_Envelope()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        Expect.Accepted(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    public void Node_Signed_File_Verifies_Via_RawSigText()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        Expect.Accepted(MinisignVerifier.VerifyFile(file, signed.Raw, [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    public void Stream_Api_Verifies_Same_As_File_Api()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        using var stream = File.OpenRead(file);
        Expect.Accepted(MinisignVerifier.Verify(stream, signed.Envelope, [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Trusted_Key_Raw_And_Base64_Both_Accepted(bool raw)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);
        var trusted = raw ? key.PublicKeyText : key.TauriPublicKey;

        Expect.Accepted(MinisignVerifier.VerifyFile(file, signed.Envelope, [trusted]), Comment);
    }

    // ---- Payload (A) ------------------------------------------------------

    [TestMethod]
    [DataRow(0)]
    [DataRow(2047)]
    [DataRow(4095)]
    public void FileTamperedByte_Rejected(int index)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var tampered = Payloads.Pattern();
        tampered[index] ^= 0x01;
        File.WriteAllBytes(file, tampered);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), "file");
    }

    [TestMethod]
    public void Truncated_File_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        File.WriteAllBytes(file, Payloads.Pattern()[..^1]);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), "file");
    }

    [TestMethod]
    public void Appended_Byte_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        File.WriteAllBytes(file, [.. Payloads.Pattern(), 0x00]);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), "file");
    }

    [TestMethod]
    public void EmptyFile_SignedEmpty_Accepted()
    {
        var key = NewKey("key");
        var file = _dir.Write("empty.bin", []);
        var signed = Signing.Sign(file, key, Comment);

        Expect.Accepted(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    public void EmptyFile_WrongSignature_Rejected()
    {
        var key = NewKey("key");
        var signed = Signing.Sign(Payload(), key, Comment);
        var empty = _dir.Write("empty.bin", []);

        Expect.Rejected(MinisignVerifier.VerifyFile(empty, signed.Envelope, [key.TauriPublicKey]), "file");
    }

    [TestMethod]
    public void Huge_300MB_Verifies_With_Bounded_Allocation()
    {
        const int chunkSize = 1 << 20;
        const int chunkCount = 300;
        var key = NewKey("key");
        var file = Path.Combine(_dir.Root, "huge.bin");
        var chunk = new byte[chunkSize];
        using (var output = File.Create(file))
        {
            for (var i = 0; i < chunkCount; i++)
            {
                RandomNumberGenerator.Fill(chunk);
                output.Write(chunk);
            }
        }

        var signed = Signing.Sign(file, key, Comment);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Expect.Accepted(result, Comment);
        Assert.IsTrue(
            allocated < 16L * 1024 * 1024,
            $"verifying a {chunkSize * (long)chunkCount}-byte file allocated {allocated} bytes; the file must be streamed");

        using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(-1, SeekOrigin.End);
            var last = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.End);
            stream.WriteByte((byte)(last ^ 0xFF));
        }

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), "file");
    }

    [TestMethod]
    public void Missing_File_Returns_Failure()
    {
        var key = NewKey("key");
        var signed = Signing.Sign(Payload(), key, Comment);
        var missing = Path.Combine(_dir.Root, "does-not-exist.bin");

        Expect.Rejected(MinisignVerifier.VerifyFile(missing, signed.Envelope, [key.TauriPublicKey]), "cannot read");
    }

    [TestMethod]
    public void Unreadable_Stream_Returns_Failure()
    {
        var key = NewKey("key");
        var signed = Signing.Sign(Payload(), key, Comment);
        using var writeOnly = new FileStream(Path.Combine(_dir.Root, "write-only.bin"), FileMode.Create, FileAccess.Write);

        Expect.Rejected(MinisignVerifier.Verify(writeOnly, signed.Envelope, [key.TauriPublicKey]), "readable");
    }

    // ---- Signature text and envelope (B) ----------------------------------

    [TestMethod]
    public void Sig_From_Other_File_Rejected()
    {
        var key = NewKey("key");
        var fileA = Payload("a.bin", 4096);
        var fileB = Payload("b.bin", 4097);
        var signedA = Signing.Sign(fileA, key, Comment);

        Expect.Accepted(MinisignVerifier.VerifyFile(fileA, signedA.Envelope, [key.TauriPublicKey]), Comment);
        Expect.Rejected(MinisignVerifier.VerifyFile(fileB, signedA.Envelope, [key.TauriPublicKey]), "file");
    }

    [TestMethod]
    public void Signature_Bytes_Tampered_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var packet = Sig.Packet(signed.Raw);
        packet[10] ^= 0x01; // first byte of the 64-byte Ed25519 signature
        var tampered = Sig.WithPacket(signed.Raw, packet);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(tampered), [key.TauriPublicKey]), "global");
    }

    [TestMethod]
    public void Global_Signature_Tampered_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var global = Sig.GlobalOf(signed.Raw);
        global[0] ^= 0x01;
        var tampered = Sig.WithGlobal(signed.Raw, global);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(tampered), [key.TauriPublicKey]), "global");
    }

    [TestMethod]
    public void TrustedComment_Tampered_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var tampered = Sig.WithTrustedComment(signed.Raw, "timestamp:1794102824\tfile:payload.bin\tversion:9.9.9");

        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(tampered), [key.TauriPublicKey]), "global");
        Expect.Rejected(MinisignVerifier.VerifyFile(file, tampered, [key.TauriPublicKey]), "global");
    }

    [TestMethod]
    public void TrustedComment_Tabs_Unicode_RoundTrips()
    {
        const string unicodeComment = "timestamp:1794102823\tfile:Zinnia_0.6.6_x64-setup.exe\tversion:0.6.6 ünïcode ✓ trailing ";
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, unicodeComment);

        Expect.Accepted(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), unicodeComment);
    }

    [TestMethod]
    public void UntrustedComment_Changed_StillVerifies()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var changed = Sig.WithUntrustedComment(signed.Raw, "untrusted comment: attacker-chosen text");

        Expect.Accepted(MinisignVerifier.VerifyFile(file, Sig.Tauri(changed), [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    public void Malformed_Base64_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);
        var envelope = signed.Envelope;

        var cases = new[]
        {
            ReplaceAt(envelope, 20, '!'),
            envelope + "A",
            ReplaceAt(envelope, 20, '='),
            ReplaceAt(envelope, 20, '*'),
            Sig.WithPacketLine(signed.Raw, "!" + Sig.Lines(signed.Raw)[1][1..]),
        };

        foreach (var input in cases)
        {
            Expect.Rejected(MinisignVerifier.VerifyFile(file, input, [key.TauriPublicKey]));
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NonCanonical_Base64_Rejected(bool envelope)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var raw = Sig.NonCanonicalPacketLine(signed.Raw);
        // Sanity: the mutation must decode to the same bytes, so only the canonical-form check can reject it.
        CollectionAssert.AreEqual(Sig.Packet(signed.Raw), Sig.Packet(raw));
        Assert.AreNotEqual(Sig.Lines(signed.Raw)[1], Sig.Lines(raw)[1]);

        var input = envelope ? Sig.Tauri(raw) : raw;
        Expect.Rejected(MinisignVerifier.VerifyFile(file, input, [key.TauriPublicKey]));
    }

    [TestMethod]
    public void Wrapped_Base64_Envelope_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var wrapped = Sig.Tauri(signed.Raw).Insert(76, "\n");

        Expect.Rejected(MinisignVerifier.VerifyFile(file, wrapped, [key.TauriPublicKey]));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Wrong_Line_Count_Rejected(int kind)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);
        var lines = Sig.Lines(signed.Raw);

        var raw = kind switch
        {
            0 => Sig.Join(lines[..3]),              // global signature line dropped
            1 => Sig.Join([.. lines, "extra line"]), // five lines
            2 => signed.Raw + "\n",                 // extra blank line at the end
            3 => Sig.Join(lines[..1]),              // header only
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Expect.Rejected(MinisignVerifier.VerifyFile(file, raw, [key.TauriPublicKey]));
        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(raw), [key.TauriPublicKey]));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Missing_Final_Newline_Accepted(bool envelope)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var raw = signed.Raw.TrimEnd('\n');
        var input = envelope ? Sig.Tauri(raw) : raw;

        Expect.Accepted(MinisignVerifier.VerifyFile(file, input, [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    public void CRLF_Raw_Sig_Accepted()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var crlf = signed.Raw.Replace("\n", "\r\n");

        Expect.Accepted(MinisignVerifier.VerifyFile(file, crlf, [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    public void CRLF_Envelope_Accepted()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var crlf = signed.Raw.Replace("\n", "\r\n");

        Expect.Accepted(MinisignVerifier.VerifyFile(file, Sig.Tauri(crlf), [key.TauriPublicKey]), Comment);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void Extra_Whitespace_Rejected(int kind)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);
        var packetLine = Sig.Lines(signed.Raw)[1];

        var input = kind switch
        {
            0 => " " + signed.Raw,                                                  // space before header
            1 => "\n" + signed.Raw,                                                 // blank line before header
            2 => Sig.WithPacketLine(signed.Raw, packetLine + " "),                  // trailing space on a base64 line
            3 => Sig.WithPacketLine(signed.Raw, packetLine[..10] + "\r" + packetLine[10..]), // stray CR inside a line
            4 => signed.Raw + " ",                                                  // extra line of spaces at the end
            5 => Sig.Tauri(signed.Raw).Insert(20, "\t"),                            // tab inside the envelope
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Expect.Rejected(MinisignVerifier.VerifyFile(file, input, [key.TauriPublicKey]));
    }

    [TestMethod]
    public void Bom_Prefixed_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, "﻿" + signed.Raw, [key.TauriPublicKey]));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\n")]
    public void Empty_Signature_Text_Rejected(string input)
    {
        var key = NewKey("key");
        var file = Payload();

        Expect.Rejected(MinisignVerifier.VerifyFile(file, input, [key.TauriPublicKey]));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Signature_Length_Wrong_Rejected(int kind)
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);
        var packet = Sig.Packet(signed.Raw);
        var global = Sig.GlobalOf(signed.Raw);

        var raw = kind switch
        {
            0 => Sig.WithPacket(signed.Raw, packet[..73]),
            1 => Sig.WithPacket(signed.Raw, [.. packet, 0x00]),
            2 => Sig.WithGlobal(signed.Raw, global[..63]),
            3 => Sig.WithGlobal(signed.Raw, [.. global, 0x00]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(raw), [key.TauriPublicKey]));
    }

    [TestMethod]
    public void Unknown_Algorithm_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var raw = Sig.WithPacket(signed.Raw, Sig.WithAlgorithm(Sig.Packet(signed.Raw), "XX"));

        Expect.Rejected(MinisignVerifier.VerifyFile(file, raw, [key.TauriPublicKey]), "algorithm");
    }

    [TestMethod]
    public void Legacy_Ed_Rejected_By_Default()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment, legacy: true);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey]), "legacy");
    }

    [TestMethod]
    public void Legacy_Ed_Accepted_When_Allowed()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment, legacy: true);

        Expect.Accepted(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey], allowLegacy: true), Comment);
    }

    [TestMethod]
    public void Legacy_Ed_Tampered_Rejected_When_Allowed()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment, legacy: true);

        var tampered = Payloads.Pattern();
        tampered[100] ^= 0x01;
        File.WriteAllBytes(file, tampered);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [key.TauriPublicKey], allowLegacy: true), "file");
    }

    [TestMethod]
    public void Garbage_Input_Never_Throws()
    {
        var key = NewKey("key");
        var file = Payload();
        const string alphabet = "untrusted comment: ED\r\n\t=+/0123456789ABCDEFabcdefxyz!";
        var random = new Random(20260401);

        for (var i = 0; i < 500; i++)
        {
            string input;
            if (i % 2 == 0)
            {
                var length = random.Next(0, 300);
                input = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            }
            else
            {
                var bytes = new byte[random.Next(0, 300)];
                random.NextBytes(bytes);
                input = Convert.ToBase64String(bytes);
            }

            var result = MinisignVerifier.VerifyFile(file, input, [key.TauriPublicKey]);
            Assert.IsFalse(result.Ok, $"garbage input #{i} was accepted");
        }
    }

    // ---- Key identity and trust (C) ---------------------------------------

    [TestMethod]
    public void Wrong_Key_Rejected()
    {
        var signer = NewKey("signer");
        var other = NewKey("other");
        var file = Payload();
        var signed = Signing.Sign(file, signer, Comment);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [other.TauriPublicKey]), "key id");
    }

    [TestMethod]
    public void SameKeyId_WrongKey_Rejected()
    {
        var trusted = NewKey("trusted");
        var attacker = NewKey("attacker");
        var file = Payload();
        var signed = Signing.Sign(file, attacker, Comment);

        // The attacker copies the trusted key id into the packet. Only the signature check can catch this.
        var forged = Sig.WithKeyId(signed.Raw, trusted.KeyId);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(forged), [trusted.TauriPublicKey]), "global");
    }

    [TestMethod]
    public void WrongKeyId_WithRightKey_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        var otherId = key.KeyId.Select(b => (byte)~b).ToArray();
        var forged = Sig.WithKeyId(signed.Raw, otherId);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, Sig.Tauri(forged), [key.TauriPublicKey]), "key id");
    }

    [TestMethod]
    public void KeyId_Collision_Between_Trusted_Keys_Still_Accepts()
    {
        var first = NewKey("first");
        var second = NewKey("second");
        var file = Payload();
        var signed = Signing.Sign(file, second, Comment);

        // Trusted list has two keys with the same key id. The second key signed.
        var secondPacket = Sig.PublicKeyPacket(second.PublicKeyText);
        first.KeyId.CopyTo(secondPacket, 2);
        var secondWithFirstId = "untrusted comment: minisign public key: COLLISION\n" + Convert.ToBase64String(secondPacket) + "\n";
        var collided = Sig.WithKeyId(signed.Raw, first.KeyId);

        Expect.Accepted(
            MinisignVerifier.VerifyFile(file, Sig.Tauri(collided), [first.PublicKeyText, secondWithFirstId]),
            Comment);
    }

    [TestMethod]
    public void Rotation_Second_Key_Signed_Accepted()
    {
        var oldKey = NewKey("old");
        var newKey = NewKey("new");
        var file = Payload();
        var signed = Signing.Sign(file, newKey, Comment);

        Expect.Accepted(
            MinisignVerifier.VerifyFile(file, signed.Envelope, [oldKey.TauriPublicKey, newKey.TauriPublicKey]),
            Comment);
    }

    [TestMethod]
    public void Rotation_Unlisted_Key_Rejected()
    {
        var oldKey = NewKey("old");
        var newKey = NewKey("new");
        var third = NewKey("third");
        var file = Payload();
        var signed = Signing.Sign(file, third, Comment);

        Expect.Rejected(
            MinisignVerifier.VerifyFile(file, signed.Envelope, [oldKey.TauriPublicKey, newKey.TauriPublicKey]),
            "key id");
    }

    [TestMethod]
    public void Empty_Trusted_Key_List_Rejected()
    {
        var key = NewKey("key");
        var file = Payload();
        var signed = Signing.Sign(file, key, Comment);

        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, []), "trusted");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Malformed_Trusted_Key_Fails_Closed(int kind)
    {
        var good = NewKey("good");
        var file = Payload();
        var signed = Signing.Sign(file, good, Comment);
        var bad = MalformedTrustedKey(kind, good);

        // The bad key is refused whether it is listed first or last. A valid key beside it does not save the call.
        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [bad, good.TauriPublicKey]));
        Expect.Rejected(MinisignVerifier.VerifyFile(file, signed.Envelope, [good.TauriPublicKey, bad]));
    }

    private static string MalformedTrustedKey(int kind, SigningKey valid)
    {
        var packet = Sig.PublicKeyPacket(valid.PublicKeyText);
        return kind switch
        {
            0 => "not base64 at all!!",
            1 => Sig.Tauri("untrusted comment: truncated\nAAAA\n"),
            2 => Sig.Tauri("untrusted comment: wrong algorithm\n" + Convert.ToBase64String(Sig.WithAlgorithm(packet, "ED")) + "\n"),
            3 => Sig.Tauri("untrusted comment: point not on the curve\n" + Convert.ToBase64String(OffCurvePacket(valid.KeyId)) + "\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // "Ed" + key id + 32 bytes of 0xFF. That y-value is >= the field prime, so it is not a valid encoding.
    private static byte[] OffCurvePacket(byte[] keyId)
    {
        var packet = new byte[42];
        "Ed"u8.CopyTo(packet);
        keyId.CopyTo(packet, 2);
        Array.Fill(packet, (byte)0xFF, 10, 32);
        return packet;
    }

    private static string ReplaceAt(string text, int index, char value) =>
        text[..index] + value + text[(index + 1)..];
}
