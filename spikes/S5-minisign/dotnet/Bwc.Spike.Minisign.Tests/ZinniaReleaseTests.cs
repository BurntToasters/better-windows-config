using System.Text.Json;
using Bwc.Spike.Minisign;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Spike.Minisign.Tests;

// Real-world vector: Zinnia's shipped Windows release, checked with Zinnia's own updater public key
// (src-tauri/tauri.conf.json). Needs internet. If GitHub cannot be reached the test is Inconclusive
// with a skip message instead of failing.
[TestClass]
public sealed class ZinniaReleaseTests
{
    private const string ZinniaPublicKey =
        "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IEY5RTAwQ0YyRjU4NENCNkMKUldSc3k0VDE4Z3pnK2NQaXdzYXkzWklabm9tTTZvSTErcmNQSkZFL2d3VnpXNnlHL2R0WjAwMHcK";

    private const string ManifestUrl =
        "https://github.com/BurntToasters/zinnia/releases/latest/download/latest-windows-x86_64.json";

    [TestMethod]
    public async Task Zinnia_Release_Artifact_Verifies()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var scratch = new TempDir();

        string manifestJson;
        try
        {
            manifestJson = await http.GetStringAsync(ManifestUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Assert.Inconclusive($"SKIPPED: could not download the Zinnia manifest ({e.Message}). Re-run online to check the real vector.");
            return;
        }

        using var manifest = JsonDocument.Parse(manifestJson);
        var platform = manifest.RootElement.GetProperty("platforms").GetProperty("windows-x86_64");
        var url = platform.GetProperty("url").GetString()!;
        var signature = platform.GetProperty("signature").GetString()!;

        var assetDir = scratch.Sub("artifact");
        var assetPath = Path.Combine(assetDir, Path.GetFileName(new Uri(url).LocalPath));
        try
        {
            await using var source = await http.GetStreamAsync(url);
            await using var target = File.Create(assetPath);
            await source.CopyToAsync(target);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Assert.Inconclusive($"SKIPPED: could not download the Zinnia artifact ({e.Message}). Re-run online to check the real vector.");
            return;
        }

        var result = MinisignVerifier.VerifyFile(assetPath, signature, [ZinniaPublicKey]);
        Console.WriteLine(
            $"Zinnia real vector: url={url} bytes={new FileInfo(assetPath).Length} ok={result.Ok} " +
            $"trustedComment={result.TrustedComment?.Replace('\t', ' ')} failure={result.Failure}");
        Assert.IsTrue(result.Ok, $"real Zinnia artifact did not verify: {result.Failure}");

        // Same artifact with one byte flipped in place must be rejected.
        using (var stream = new FileStream(assetPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(stream.Length / 2, SeekOrigin.Begin);
            var original = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.Current);
            stream.WriteByte((byte)(original ^ 0x01));
        }

        Expect.Rejected(MinisignVerifier.VerifyFile(assetPath, signature, [ZinniaPublicKey]), "file");
    }
}
