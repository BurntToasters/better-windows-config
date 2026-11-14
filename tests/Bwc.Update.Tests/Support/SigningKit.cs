using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

/// <summary>Drives Support/minisign.mjs, a copy of the S5 spike signer. Needs `node` on PATH.</summary>
internal static class NodeSigner
{
    // Copied to the output folder by the test project. Source paths are not used: CI builds map them away.
    private static string ScriptPath() => Path.Combine(AppContext.BaseDirectory, "minisign.mjs");

    public static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(ScriptPath());
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start node");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"node minisign.mjs {args[0]} timed out");
        }

        _ = stdout.Result;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"node minisign.mjs {args[0]} exited {process.ExitCode}: {stderr.Result}");
        }
    }
}

/// <summary>A key pair made by the Node signer. The secret key stays in Dir and is never committed.</summary>
internal sealed record SigningKey(string Dir, string PublicKeyText)
{
    public static SigningKey Generate(string dir)
    {
        Directory.CreateDirectory(dir);
        NodeSigner.Run("keygen", dir);
        return new SigningKey(dir, File.ReadAllText(Path.Combine(dir, "minisign.pub")));
    }

    /// <summary>Key id bytes as stored in the public key packet.</summary>
    public byte[] KeyId => Sig.PublicKeyPacket(PublicKeyText)[2..10];
}

/// <summary>A file signed by the Node signer: raw .minisig text and the base64 envelope.</summary>
internal sealed record SignedFile(string FilePath, string Raw, string Envelope);

internal static class Signing
{
    public static SignedFile Sign(string file, SigningKey key, string trustedComment, bool legacy = false)
    {
        var args = new List<string> { "sign", file, key.Dir, "--trusted-comment", trustedComment };
        if (legacy)
        {
            args.Add("--legacy");
        }

        NodeSigner.Run([.. args]);
        return new SignedFile(file, File.ReadAllText(file + ".minisig"), File.ReadAllText(file + ".sig"));
    }

    /// <summary>Signs a manifest with the exact trusted comment the updater requires (version, channel, expires).</summary>
    public static SignedFile SignManifest(string file, SigningKey key, string version, string channel, string expires)
    {
        NodeSigner.Run("sign-manifest", file, key.Dir, "--version", version, "--channel", channel, "--expires", expires);
        return new SignedFile(file, File.ReadAllText(file + ".minisig"), File.ReadAllText(file + ".sig"));
    }
}

/// <summary>Byte-level helpers that build tampered signature and key text. LF input is assumed.</summary>
internal static class Sig
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public static string[] Lines(string text) => text.TrimEnd('\n').Split('\n');

    public static string Join(IEnumerable<string> lines) => string.Join('\n', lines) + "\n";

    public static string Envelope(string raw) => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

    public static byte[] PublicKeyPacket(string publicKeyText) => Convert.FromBase64String(Lines(publicKeyText)[1]);

    public static byte[] Packet(string raw) => Convert.FromBase64String(Lines(raw)[1]);

    public static string WithPacket(string raw, byte[] packet)
    {
        var lines = Lines(raw);
        lines[1] = Convert.ToBase64String(packet);
        return Join(lines);
    }

    public static string WithKeyId(string raw, byte[] keyId)
    {
        var packet = Packet(raw);
        keyId.CopyTo(packet, 2);
        return WithPacket(raw, packet);
    }

    public static byte[] WithAlgorithm(byte[] packet, string alg)
    {
        var copy = (byte[])packet.Clone();
        Encoding.ASCII.GetBytes(alg).CopyTo(copy, 0);
        return copy;
    }

    public static string WithPublicKeyKeyId(string publicKeyText, byte[] keyId)
    {
        var packet = PublicKeyPacket(publicKeyText);
        keyId.CopyTo(packet, 2);
        var lines = Lines(publicKeyText);
        lines[1] = Convert.ToBase64String(packet);
        return Join(lines);
    }

    public static string WithTrustedComment(string raw, string comment)
    {
        var lines = Lines(raw);
        lines[2] = "trusted comment: " + comment;
        return Join(lines);
    }

    public static string WithUntrustedComment(string raw, string comment)
    {
        var lines = Lines(raw);
        lines[0] = comment;
        return Join(lines);
    }

    /// <summary>Same decoded bytes as the packet line, with one unused padding bit set.</summary>
    public static string NonCanonicalPacketLine(string raw)
    {
        var lines = Lines(raw);
        var b64 = lines[1];
        var value = Alphabet.IndexOf(b64[^2]);
        lines[1] = b64[..^2] + Alphabet[value | 1] + b64[^1..];
        return Join(lines);
    }
}

/// <summary>A temp folder deleted on dispose. It lives under the process temp folder.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "bwc-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}

internal static class Payloads
{
    public static byte[] Pattern(int length = 4096, int seed = 0) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7 + seed)).ToArray();

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
