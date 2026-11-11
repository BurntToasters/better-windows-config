using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Hub;

/// <summary>
/// A throwaway minisign key pair, signed release manifests, and signed installers for both
/// architectures. Everything is generated per run in a temporary folder by the signer that
/// Bwc.Update.Tests uses. The private key is never committed, and the folder is deleted on dispose.
/// </summary>
internal sealed class UpdateFixture : IDisposable
{
    public const string NewerVersion = "9.9.9";
    private const int InstallerSize = 4096;
    private const string ArchX64 = "win-x64";
    private const string ArchArm64 = "win-arm64";

    private readonly string _root;
    private readonly string _signer;
    private readonly string _keyDir;
    private readonly byte[] _installer;
    private readonly string _installerSha256;

    private UpdateFixture(string root, string signer, string keyDir, string publicKeyText)
    {
        _root = root;
        _signer = signer;
        _keyDir = keyDir;
        PublicKeyText = publicKeyText;
        _installer = Enumerable.Range(0, InstallerSize).Select(i => (byte)(i * 37 + 11)).ToArray();
        _installerSha256 = Convert.ToHexString(SHA256.HashData(_installer)).ToLowerInvariant();
    }

    /// <summary>The public key file text. The Hub and helper receive it base64 encoded.</summary>
    public string PublicKeyText { get; }

    /// <summary>The public key as the helper reads it from BWC_E2E_UPDATE_TRUSTED_KEY.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(PublicKeyText));

    /// <summary>Creates a key pair in a new temporary folder. Needs node on PATH.</summary>
    public static UpdateFixture Create(E2EConfig config)
    {
        string signer = Path.Combine(config.RepoRoot, "tests", "Bwc.Update.Tests", "Support", "minisign.mjs");
        if (!File.Exists(signer))
        {
            throw new FileNotFoundException("The update signer script is missing.", signer);
        }

        string root = Path.Combine(Path.GetTempPath(), "bwc-hub-e2e-" + Guid.NewGuid().ToString("N"));
        string keyDir = Path.Combine(root, "key");
        Directory.CreateDirectory(keyDir);
        RunNode(signer, "keygen", keyDir);
        string publicKey = File.ReadAllText(Path.Combine(keyDir, "minisign.pub"));
        return new UpdateFixture(root, signer, keyDir, publicKey);
    }

    /// <summary>The manifest and its signature for one version, exactly as the server should serve them.</summary>
    public (byte[] Manifest, byte[] Signature) BuildManifest(string version, string baseUrl)
    {
        string releaseDir = Path.Combine(_root, "release-" + version);
        Directory.CreateDirectory(releaseDir);

        string x64 = SignInstaller(releaseDir, ArchX64, version);
        string arm64 = SignInstaller(releaseDir, ArchArm64, version);
        string pubDate = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["version"] = version,
            ["channel"] = "stable",
            ["pubDate"] = pubDate,
            ["notesUrl"] = "https://example.invalid/notes/" + version,
            ["installers"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ArchX64] = InstallerEntry(baseUrl, ArchX64, x64),
                [ArchArm64] = InstallerEntry(baseUrl, ArchArm64, arm64),
            },
        };

        string manifestPath = Path.Combine(releaseDir, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RunNode(_signer, "sign", manifestPath, _keyDir, "--trusted-comment", "timestamp:1794102823 version:" + version);
        return (File.ReadAllBytes(manifestPath), File.ReadAllBytes(manifestPath + ".minisig"));
    }

    /// <summary>The installer bytes that the server serves for every architecture.</summary>
    public byte[] Installer => _installer;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by a process is left for the OS temp cleaner.
        }
    }

    private string SignInstaller(string folder, string architecture, string version)
    {
        string path = Path.Combine(folder, "installer-" + architecture + ".exe");
        File.WriteAllBytes(path, _installer);
        RunNode(
            _signer,
            "sign",
            path,
            _keyDir,
            "--trusted-comment",
            "timestamp:1794102823 version:" + version + " arch:" + architecture);

        // The manifest carries the base64 envelope of the installer's minisign text.
        return File.ReadAllText(path + ".sig").Trim();
    }

    private Dictionary<string, object?> InstallerEntry(string baseUrl, string architecture, string signatureEnvelope) =>
        new(StringComparer.Ordinal)
        {
            ["url"] = baseUrl + "/installer-" + architecture + ".exe",
            ["size"] = InstallerSize,
            ["sha256"] = _installerSha256,
            ["signature"] = signatureEnvelope,
        };

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
            ?? throw new InvalidOperationException("node did not start. The update fixture needs node on PATH.");
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
            throw new InvalidOperationException(
                $"The update signer failed with exit code {process.ExitCode}: {stderr.Result}");
        }
    }
}
