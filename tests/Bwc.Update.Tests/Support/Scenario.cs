using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Update.Tests;

/// <summary>An installer file signed by the test key, with its manifest entry values.</summary>
internal sealed record InstallerPayload(string FilePath, byte[] Bytes, string Sha256, string Envelope, string Comment);

/// <summary>
/// A published update on the loopback server: a signed manifest, its signature, and one signed
/// installer per architecture. Tests change the manifest, the routes, or the bytes before a check.
/// </summary>
internal sealed class Scenario : IAsyncDisposable
{
    public const string ManifestPath = "/bwc/beta.json";
    public const string ManifestSignaturePath = "/bwc/beta.json.minisig";
    public const string DefaultVersion = "0.2.0";
    public const string DefaultPubDate = "2026-11-10T00:00:00Z";

    private readonly Dictionary<string, InstallerPayload> _installers = new(StringComparer.Ordinal);

    public Scenario(string version = DefaultVersion, string channel = "beta")
    {
        Version = version;
        Channel = channel;
        Dir = new TempDir();
        Server = new LoopbackServer();
        Key = SigningKey.Generate(Dir.Sub("key"));
        Manifest = new JsonObject
        {
            ["schema"] = 1,
            ["version"] = version,
            ["channel"] = channel,
            ["pubDate"] = DefaultPubDate,
            ["notesUrl"] = "https://example.invalid/notes",
            ["installers"] = new JsonObject(),
        };
    }

    public string Version { get; }

    public string Channel { get; }

    public TempDir Dir { get; }

    public LoopbackServer Server { get; }

    public SigningKey Key { get; }

    /// <summary>The manifest body. Tests edit it before calling <see cref="Publish"/>.</summary>
    public JsonObject Manifest { get; }

    public byte[] ManifestBytes { get; private set; } = [];

    public byte[] ManifestSignatureBytes { get; private set; } = [];

    public string StagingRoot => Path.Combine(Dir.Root, "staging");

    public InstallerPayload Installer(string architecture) => _installers[architecture];

    /// <summary>Signs an installer with the test key and adds its entry to the manifest.</summary>
    public InstallerPayload AddInstaller(
        string architecture,
        int size = 4096,
        string? path = null,
        byte[]? bytes = null,
        string? comment = null,
        SigningKey? signer = null)
    {
        var routePath = path ?? $"/bwc/{architecture}.exe";
        var payloadBytes = bytes ?? Payloads.Pattern(size, seed: architecture.Length);
        var file = Dir.Write($"{architecture}-{Guid.NewGuid():N}.bin", payloadBytes);
        var trusted = comment ?? $"timestamp:1794102823\tfile:installer.exe\tversion:{Version}\tarch:{architecture}";
        var signed = Signing.Sign(file, signer ?? Key, trusted);
        var envelope = Sig.Envelope(signed.Raw);
        var payload = new InstallerPayload(file, payloadBytes, Payloads.Sha256Hex(payloadBytes), envelope, trusted);
        _installers[architecture] = payload;

        Server.Map(routePath, new Route(Body: payloadBytes));
        ((JsonObject)Manifest["installers"]!)[architecture] = new JsonObject
        {
            ["url"] = Server.Url(routePath).AbsoluteUri,
            ["size"] = payloadBytes.Length,
            ["sha256"] = payload.Sha256,
            ["signature"] = envelope,
        };
        return payload;
    }

    /// <summary>Pads the manifest with an unknown property so its JSON is exactly <paramref name="totalBytes"/> long.</summary>
    public void PadManifestTo(int totalBytes)
    {
        Manifest["padding"] = string.Empty;
        var baseLength = Encoding.UTF8.GetByteCount(Manifest.ToJsonString());
        Manifest["padding"] = new string('x', totalBytes - baseLength);
    }

    /// <summary>
    /// The expiry the signed manifest carries by default: one day from now, in the form the trusted
    /// comment requires (yyyy-MM-ddTHH:mm:ssZ).
    /// </summary>
    public static string DefaultExpires(TimeSpan? ahead = null) =>
        DateTimeOffset.UtcNow.Add(ahead ?? TimeSpan.FromDays(1)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the manifest, signs it, and maps the manifest and signature routes. The comment is the
    /// required form (version, channel, expires). Pass <paramref name="commentVersion"/>,
    /// <paramref name="commentChannel"/>, or <paramref name="expires"/> to sign a comment that disagrees
    /// with the body or has another expiry, and <paramref name="rawComment"/> to sign any text at all.
    /// </summary>
    public void Publish(
        string? commentVersion = null,
        SigningKey? signer = null,
        bool publishSignature = true,
        string? commentChannel = null,
        string? expires = null,
        string? rawComment = null)
    {
        ManifestBytes = Encoding.UTF8.GetBytes(Manifest.ToJsonString());
        var file = Dir.Write("manifest-" + Guid.NewGuid().ToString("N") + ".json", ManifestBytes);
        var signed = rawComment is not null
            ? Signing.Sign(file, signer ?? Key, rawComment)
            : Signing.SignManifest(file, signer ?? Key, commentVersion ?? Version, commentChannel ?? Channel, expires ?? DefaultExpires());
        Server.Map(ManifestPath, new Route(Body: ManifestBytes));
        ManifestSignatureBytes = Encoding.UTF8.GetBytes(signed.Raw);
        if (publishSignature)
        {
            Server.Map(ManifestSignaturePath, new Route(Body: ManifestSignatureBytes));
        }
    }

    public Uri ManifestUrl => Server.Url(ManifestPath);

    public UpdateRequest Request(string current = "0.1.0", string architecture = Architectures.X64, UpdateChannel channel = UpdateChannel.Beta) =>
        new(Server.Url(ManifestPath), Server.Url(ManifestSignaturePath), channel, current, architecture, StagingRoot);

    /// <summary>Options for loopback tests: plain http allowed, short timeouts, the test key trusted.</summary>
    public UpdateOptions Options(TimeSpan? stall = null, TimeSpan? installerDownload = null) => new()
    {
        TrustedPublicKeys = [Key.PublicKeyText],
        RequireHttps = false,
        StallTimeout = stall ?? TimeSpan.FromSeconds(2),
        ManifestTimeout = TimeSpan.FromSeconds(5),
        InstallerDownloadTimeout = installerDownload ?? TimeSpan.FromSeconds(10),
    };

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Dir.Dispose();
    }
}
