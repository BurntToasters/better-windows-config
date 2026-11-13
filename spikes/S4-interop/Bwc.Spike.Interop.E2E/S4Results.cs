using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bwc.Spike.Interop.E2E;

internal sealed record CheckResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("pass")] bool Pass,
    [property: JsonPropertyName("notes")] string Notes);

/// <summary>Collects every check across the run and writes spikes/S4-interop/out/s4-result.json at the end.</summary>
internal static class S4Results
{
    private static readonly object Gate = new();
    private static readonly List<CheckResult> Items = new();

    /// <summary>Records one check and returns its result, so tests can both record and assert.</summary>
    public static bool Record(string id, bool pass, string notes)
    {
        lock (Gate)
        {
            Items.Add(new CheckResult(id, pass, notes));
        }
        return pass;
    }

    public static IReadOnlyList<CheckResult> Snapshot()
    {
        lock (Gate)
        {
            return Items.ToList();
        }
    }

    /// <summary>spikes/S4-interop/out, found by walking up from the test binaries to the test project folder.</summary>
    public static string OutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bwc.Spike.Interop.E2E.csproj")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new DirectoryNotFoundException("Bwc.Spike.Interop.E2E.csproj not found above " + AppContext.BaseDirectory);
        }
        return Path.GetFullPath(Path.Combine(dir.Parent!.FullName, "out"));
    }

    public static void WriteArtifact()
    {
        var checks = Snapshot();
        var document = new
        {
            spike = "S4-interop",
            generatedUtc = DateTime.UtcNow.ToString("o"),
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            allPassed = checks.Count > 0 && checks.All(c => c.Pass),
            checks,
        };
        var dir = OutputDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "s4-result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}

/// <summary>Assembly-level setup and teardown: artifact write and removal of HKCU\Software\BWC-Spike-S4.</summary>
[TestClass]
public static class AssemblyHooks
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        SpikeRegistry.DeleteIfPresent();
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try
        {
            S4Results.WriteArtifact();
        }
        finally
        {
            SpikeRegistry.DeleteIfPresent();
        }
    }
}
