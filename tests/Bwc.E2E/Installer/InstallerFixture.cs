using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Installer;

/// <summary>
/// The signed update feed for one run. A throwaway minisign key pair lives in a temp folder, the real B installer
/// is signed with it, and the manifest for B is signed the way the release signer signs it (minisign.mjs, the same
/// signer the Bwc.Update tests use). The folder is deleted on dispose. Nothing is committed.
/// </summary>
internal sealed class InstallerFixture : IDisposable
{
    private const string SignerRelativePath = @"tests\Bwc.Update.Tests\Support\minisign.mjs";
    private const string TrustedCommentTimestamp = "1794102823";
    private readonly string _root;
    private readonly string _signer;
    private readonly string _keyDir;
    private readonly string _version;
    private readonly string _rid;
    private readonly string _installerEnvelope;
    private readonly long _installerSize;
    private readonly string _installerSha256;

    private InstallerFixture(
        string root,
        string signer,
        string keyDir,
        string version,
        string rid,
        string installerEnvelope,
        long installerSize,
        string installerSha256,
        string publicKeyBase64)
    {
        _root = root;
        _signer = signer;
        _keyDir = keyDir;
        _version = version;
        _rid = rid;
        _installerEnvelope = installerEnvelope;
        _installerSize = installerSize;
        _installerSha256 = installerSha256;
        PublicKeyBase64 = publicKeyBase64;
    }

    /// <summary>The trusted public key, base64 encoded, as the Hub and the helper read it from the environment.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>Creates the key pair and signs the installer bytes for one architecture.</summary>
    public static InstallerFixture Create(E2EConfig config, byte[] installer, string fileName, string version, string rid)
    {
        string signer = Path.Combine(config.RepoRoot, SignerRelativePath);
        if (!File.Exists(signer))
        {
            throw new FileNotFoundException("The update signer script is missing.", signer);
        }

        string root = Path.Combine(Path.GetTempPath(), "bwc-installer-feed-" + Guid.NewGuid().ToString("N")[..12]);
        string keyDir = Path.Combine(root, "key");
        Directory.CreateDirectory(keyDir);
        try
        {
            RunNode(signer, "keygen", keyDir);
            string publicKey = File.ReadAllText(Path.Combine(keyDir, "minisign.pub"));

            string installerPath = Path.Combine(root, fileName);
            File.WriteAllBytes(installerPath, installer);
            RunNode(
                signer,
                "sign",
                installerPath,
                keyDir,
                "--trusted-comment",
                "timestamp:" + TrustedCommentTimestamp + " version:" + version + " arch:" + rid);
            string envelope = File.ReadAllText(installerPath + ".sig").Trim();

            return new InstallerFixture(
                root,
                signer,
                keyDir,
                version,
                rid,
                envelope,
                installer.LongLength,
                Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey)));
        }
        catch
        {
            DeleteFolder(root);
            throw;
        }
    }

    /// <summary>The manifest and its signature for this version, with the installer served from installerUrl.</summary>
    public (byte[] Manifest, byte[] Signature) BuildManifest(string installerUrl)
    {
        string releaseDir = Path.Combine(_root, "release");
        Directory.CreateDirectory(releaseDir);

        var installer = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = installerUrl,
            ["size"] = _installerSize,
            ["sha256"] = _installerSha256,
            ["signature"] = _installerEnvelope,
        };
        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["version"] = _version,
            ["channel"] = "stable",
            ["pubDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["notesUrl"] = "https://example.invalid/notes/" + _version,
            ["installers"] = new Dictionary<string, object?>(StringComparer.Ordinal) { [_rid] = installer },
        };

        string manifestPath = Path.Combine(releaseDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string expires = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        RunNode(_signer, "sign-manifest", manifestPath, _keyDir, "--version", _version, "--channel", "stable", "--expires", expires);
        return (File.ReadAllBytes(manifestPath), File.ReadAllBytes(manifestPath + ".minisig"));
    }

    public void Dispose() => DeleteFolder(_root);

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by a process is left for the OS temp cleaner. The run reports it at cleanup.
        }
    }

    private static void RunNode(string script, params string[] args)
    {
        var startInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(script);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("node did not start. The installer E2E needs node on PATH.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The update signer did not finish within 2 minutes.");
        }

        _ = stdout.Result;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The update signer failed with exit code {process.ExitCode}: {stderr.Result}");
        }
    }
}
