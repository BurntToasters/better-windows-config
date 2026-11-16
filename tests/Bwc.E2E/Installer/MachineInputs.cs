using System.Security.Cryptography;
using System.Text.Json;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Installer;

/// <summary>
/// What <c>node scripts/test-installer-e2e.js --prepare --rid &lt;rid&gt;</c> wrote to artifacts/e2e/installer-inputs.json:
/// the two E2E setups, with their versions, sizes, and SHA-256. The elevated runner starts the tests with no arguments and
/// no environment, so this file is the only way the tests learn the setup paths. A missing, stale, or foreign file is
/// "not run" (docs/failure-modes/installer-e2e.md, 45 to 47), never a failure and never a build.
/// </summary>
internal sealed record MachineInputs(string Rid, MachineInputs.SetupFile A, MachineInputs.SetupFile B)
{
    public const string FileName = "installer-inputs.json";
    public const int Schema = 1;

    /// <summary>The inputs, or null with the reason (a sentence that names the prepare command).</summary>
    public static MachineInputs? TryRead(E2EConfig config, out string reason)
    {
        string prepare = $"Run: node scripts/test-installer-e2e.js --prepare --rid {config.Rid}";
        string file = Path.Combine(config.ArtifactsDir, FileName);
        if (!File.Exists(file))
        {
            reason = $"Not run: {file} is missing. {prepare}";
            return null;
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            reason = $"Not run: {file} cannot be read ({ex.GetType().Name}). {prepare}";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema", out JsonElement schema)
            || !schema.TryGetInt32(out int schemaValue)
            || schemaValue != Schema)
        {
            reason = $"Not run: {file} has no schema {Schema}. {prepare}";
            return null;
        }

        string? rid = Text(root, "rid");
        if (rid != config.Rid)
        {
            reason = $"Not run: the inputs are for rid '{rid}', this run is '{config.Rid}'. {prepare}";
            return null;
        }

        SetupFile? a = ReadSetup(root, "installerA", out string problemA);
        SetupFile? b = ReadSetup(root, "installerB", out string problemB);
        if (a is null || b is null)
        {
            reason = $"Not run: the inputs are stale or incomplete ({(a is null ? problemA : problemB)}). {prepare}";
            return null;
        }

        reason = string.Empty;
        return new MachineInputs(config.Rid, a, b);
    }

    private static SetupFile? ReadSetup(JsonElement root, string name, out string problem)
    {
        problem = name + " is missing";
        if (!root.TryGetProperty(name, out JsonElement setup) || setup.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? path = Text(setup, "path");
        string? version = Text(setup, "version");
        string? sha = Text(setup, "sha256");
        if (path is null || version is null || sha is null
            || !setup.TryGetProperty("size", out JsonElement sizeElement) || !sizeElement.TryGetInt64(out long size))
        {
            problem = name + " has a missing field";
            return null;
        }

        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            problem = name + " is gone: " + path;
            return null;
        }

        if (new FileInfo(path).Length != size)
        {
            problem = name + " has another size than recorded: " + path;
            return null;
        }

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
        {
            problem = name + " has another SHA-256 than recorded (it was rebuilt): " + path;
            return null;
        }

        return new SetupFile(path, version, size, sha);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>One setup: its path, the version it installs, and the size and SHA-256 recorded when it was built.</summary>
    internal sealed record SetupFile(string Path, string Version, long Size, string Sha256);
}
