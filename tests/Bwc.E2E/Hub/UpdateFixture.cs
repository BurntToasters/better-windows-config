using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Hub;

/// <summary>
/// A throwaway minisign key pair, signed release manifests, and signed installers for both architectures.
/// The installers are the real stand-in exe (tests/Bwc.E2E.FakeInstaller), one per architecture, published by
/// the E2E build into artifacts/e2e/fake-installer. The private key is generated per run in a temporary folder
/// by the signer that Bwc.Update.Tests uses, and the folder is deleted on dispose. Nothing is committed.
/// </summary>
internal sealed class UpdateFixture : IDisposable
{
    public const string NewerVersion = "9.9.9";
    public const string ArchX64 = "win-x64";
    public const string ArchArm64 = "win-arm64";

    private const string StandInFileName = "FakeInstaller.exe";

    private readonly string _root;
    private readonly string _signer;
    private readonly string _keyDir;
    private readonly IReadOnlyDictionary<string, byte[]> _installers;

    private UpdateFixture(string root, string signer, string keyDir, string publicKeyText, IReadOnlyDictionary<string, byte[]> installers)
    {
        _root = root;
        _signer = signer;
        _keyDir = keyDir;
        PublicKeyText = publicKeyText;
        _installers = installers;
    }

    /// <summary>The public key file text. The Hub and helper receive it base64 encoded.</summary>
    public string PublicKeyText { get; }

    /// <summary>The public key as the helper reads it from BWC_E2E_UPDATE_TRUSTED_KEY.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(PublicKeyText));

    /// <summary>
    /// Creates a key pair in a new temporary folder and loads the stand-in installer for each architecture.
    /// Needs node on PATH and the stand-ins published (the Bwc.E2E build publishes them).
    /// </summary>
    public static UpdateFixture Create(E2EConfig config)
    {
        string signer = Path.Combine(config.RepoRoot, "tests", "Bwc.Update.Tests", "Support", "minisign.mjs");
        if (!File.Exists(signer))
        {
            throw new FileNotFoundException("The update signer script is missing.", signer);
        }

        var installers = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ArchX64] = File.ReadAllBytes(StandInPath(config, ArchX64)),
            [ArchArm64] = File.ReadAllBytes(StandInPath(config, ArchArm64)),
        };

        string root = Path.Combine(Path.GetTempPath(), "bwc-hub-e2e-" + Guid.NewGuid().ToString("N"));
        string keyDir = Path.Combine(root, "key");
        Directory.CreateDirectory(keyDir);
        RunNode(signer, "keygen", keyDir);
        string publicKey = File.ReadAllText(Path.Combine(keyDir, "minisign.pub"));
        return new UpdateFixture(root, signer, keyDir, publicKey, installers);
    }

    /// <summary>The stand-in installer bytes for one architecture, exactly as the signed manifest lists them.</summary>
    public byte[] InstallerFor(string architecture) => _installers[architecture];

    /// <summary>The SHA-256 (lower case hex) of the signed stand-in for one architecture.</summary>
    public string Sha256For(string architecture) =>
        Convert.ToHexString(SHA256.HashData(_installers[architecture])).ToLowerInvariant();

    /// <summary>
    /// The stand-in bytes with one byte changed. The size is the same, so only the SHA-256 and the signature
    /// can refuse it. The manifest still signs the untouched bytes.
    /// </summary>
    public byte[] TamperedInstallerFor(string architecture)
    {
        byte[] copy = (byte[])_installers[architecture].Clone();
        copy[copy.Length / 2] ^= 0x01;
        return copy;
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

        // The trusted comment binds version, channel, and expiry, in exactly this form.
        string expires = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        RunNode(_signer, "sign-manifest", manifestPath, _keyDir, "--version", version, "--channel", "stable", "--expires", expires);
        return (File.ReadAllBytes(manifestPath), File.ReadAllBytes(manifestPath + ".minisig"));
    }

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

    /// <summary>The published stand-in for one architecture. The test fails, naming the build, when absent.</summary>
    public static string StandInPath(E2EConfig config, string architecture)
    {
        string path = Path.Combine(config.ArtifactsDir, "fake-installer", architecture, StandInFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The stand-in installer is missing at '{path}'. Build the E2E test project, which publishes it.",
                path);
        }

        return path;
    }

    private string SignInstaller(string folder, string architecture, string version)
    {
        string path = Path.Combine(folder, "installer-" + architecture + ".exe");
        File.WriteAllBytes(path, _installers[architecture]);
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
            ["size"] = _installers[architecture].Length,
            ["sha256"] = Sha256For(architecture),
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
