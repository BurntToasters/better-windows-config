using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bwc.Gpo.Store;

namespace Bwc.Policy.Tests.Golden;

/// <summary>Where the repository and its captures are, found by walking up from the test assembly to the
/// folder with AGENTS.md.</summary>
internal static class GoldenRepo
{
    private static readonly Lazy<string> Root = new(FindRoot);

    internal static string RepoRoot => Root.Value;

    internal static string Goldens => Path.Combine(RepoRoot, "docs", "parity", "oracle", "gpedit", "goldens");

    internal static string AwaitingList => Path.Combine(RepoRoot, "tests", "Bwc.Policy.Tests", "Golden", "awaiting-recapture.txt");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("The repository root (the folder holding AGENTS.md) is not above " + AppContext.BaseDirectory);
    }
}

/// <summary>The five files of a GPO folder by the flat names a golden step folder uses.</summary>
internal static class GoldenFiles
{
    internal static readonly string[] Names =
    [
        GpoFileNames.FolderName(GpoSide.Machine) + "." + GpoFileNames.RegistryPol,
        GpoFileNames.GptIniFile,
        GpoFileNames.FolderName(GpoSide.Machine) + "." + GpoFileNames.CommentCmtx,
        GpoFileNames.FolderName(GpoSide.User) + "." + GpoFileNames.RegistryPol,
        GpoFileNames.FolderName(GpoSide.User) + "." + GpoFileNames.CommentCmtx,
    ];

    /// <summary>The flat name as classic's change list spells it: a backslash for the folder.</summary>
    internal static string FromChangeName(string changeName) => changeName.Replace('\\', '.');

    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>The size and SHA-256 of one golden file.</summary>
internal sealed record GoldenFile(long Size, string Sha256);

/// <summary>One recorded step: a folder and what each of the five files is in it (null: the file does not
/// exist).</summary>
internal sealed class GoldenStep
{
    internal required int Number { get; init; }

    internal required string Label { get; init; }

    internal required string Folder { get; init; }

    internal required bool IsCaseRoot { get; init; }

    internal required IReadOnlyDictionary<string, GoldenFile?> Files { get; init; }

    internal string PathOf(string flatName) => Path.Combine(Folder, flatName);
}

/// <summary>One operation of a case: its structured op and what classic's driver observed.</summary>
internal sealed class GoldenOp
{
    internal required int Index { get; init; }

    internal required string Kind { get; init; }

    internal required JsonObject Op { get; init; }

    internal JsonObject? Observed { get; init; }

    internal JsonObject? Data => Observed?["data"] as JsonObject;

    internal JsonObject? LiveRegistry => Observed?["liveRegistry"] as JsonObject;

    /// <summary>
    /// True for a step snapshot (<c>label</c> starts with <c>"step "</c>) that corresponds to a folder in
    /// <see cref="GoldenCase.Steps"/>; false for live-registry-only snapshots (such as Group G's start/4 s/8 s reads).
    /// </summary>
    internal bool IsStepSnapshot =>
        Kind == "snapshot" && (Text("label")?.StartsWith("step ", StringComparison.Ordinal) ?? false);

    internal string? Text(string name) => Op[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}

/// <summary>
/// A capture case read from <c>case.json</c> and verified: every golden file must equal the size and
/// SHA-256 recorded for it, so a wrong or damaged golden fails loudly before any replay. A missing or
/// incomplete folder always fails.
/// </summary>
internal sealed class GoldenCase
{
    private GoldenCase()
    {
    }

    internal required string Id { get; init; }

    internal required string Folder { get; init; }

    internal required bool StartRefused { get; init; }

    internal required IReadOnlyList<GoldenOp> Ops { get; init; }

    internal required IReadOnlyList<GoldenStep> Steps { get; init; }

    /// <summary>The crafted start files as found in the <c>start</c> folder, by flat name; empty when the case
    /// starts empty.</summary>
    internal required IReadOnlyDictionary<string, GoldenFile> StartFiles { get; init; }

    internal string StartFolder => Path.Combine(Folder, "start");

    internal static string FolderOf(string caseId, string? goldensRoot = null) =>
        Path.Combine(goldensRoot ?? GoldenRepo.Goldens, caseId.Replace('/', Path.DirectorySeparatorChar));

    internal static bool Exists(string caseId) => Directory.Exists(FolderOf(caseId));

    internal static GoldenCase Load(string caseId, string? goldensRoot = null)
    {
        var folder = FolderOf(caseId, goldensRoot);
        if (!Directory.Exists(folder))
        {
            throw new AssertFailedException($"Capture case {caseId} has no folder ({folder}). A missing case fails; it is never skipped.");
        }

        var caseJson = Path.Combine(folder, "case.json");
        var completeJson = Path.Combine(folder, "complete.json");
        if (!File.Exists(caseJson) || !File.Exists(completeJson))
        {
            throw new AssertFailedException($"Capture case {caseId} is incomplete: case.json or complete.json is missing in {folder}.");
        }

        var root = JsonNode.Parse(File.ReadAllText(caseJson)) as JsonObject
            ?? throw new AssertFailedException($"Capture case {caseId}: case.json is not a JSON object.");
        Assert.AreEqual(caseId, (string?)root["case"], $"case.json of {caseId} names another case.");

        var startRefused = root["startRefused"]?.GetValue<bool>() ?? false;
        var observed = (root["observed"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var ops = new List<GoldenOp>();
        foreach (var node in root["ops"] as JsonArray ?? new JsonArray())
        {
            var obj = (JsonObject)node!;
            var index = obj["index"]!.GetValue<int>();
            ops.Add(new GoldenOp
            {
                Index = index,
                Kind = obj["kind"]!.GetValue<string>(),
                Op = obj["op"] as JsonObject ?? new JsonObject(),
                Observed = observed.FirstOrDefault(o => o["index"]?.GetValue<int>() == index),
            });
        }

        var steps = new List<GoldenStep>();
        foreach (var node in root["steps"] as JsonArray ?? new JsonArray())
        {
            var step = (JsonObject)node!;
            var stepFolder = step["folder"]!.GetValue<string>();
            var files = new Dictionary<string, GoldenFile?>();
            foreach (var pair in (JsonObject)step["files"]!)
            {
                files[pair.Key] = pair.Value is JsonObject f ? new GoldenFile(f["size"]!.GetValue<long>(), f["sha256"]!.GetValue<string>()) : null;
            }

            steps.Add(new GoldenStep
            {
                Number = step["number"]!.GetValue<int>(),
                Label = step["label"]!.GetValue<string>(),
                Folder = stepFolder == "." ? folder : Path.Combine(folder, stepFolder),
                IsCaseRoot = stepFolder == ".",
                Files = files,
            });
        }

        var startFiles = new Dictionary<string, GoldenFile>();
        foreach (var node in root["startState"]?["files"] as JsonArray ?? new JsonArray())
        {
            var f = (JsonObject)node!;
            startFiles[f["name"]!.GetValue<string>()] = new GoldenFile(f["size"]!.GetValue<long>(), f["sha256"]!.GetValue<string>());
        }

        var golden = new GoldenCase
        {
            Id = caseId,
            Folder = folder,
            StartRefused = startRefused,
            Ops = ops,
            Steps = steps,
            StartFiles = startFiles,
        };
        golden.VerifyComplete();
        golden.VerifyBytes();
        return golden;
    }

    private void VerifyComplete()
    {
        Assert.IsTrue(Steps.Count > 0, $"Capture case {Id} records no step.");
        if (!StartRefused)
        {
            var stepSnapshots = Ops.Where(o => o.IsStepSnapshot).ToList();
            Assert.AreEqual(
                Steps.Count,
                stepSnapshots.Count,
                $"Capture case {Id} is incomplete: {stepSnapshots.Count} step snapshot operations but {Steps.Count} steps.");
            for (var i = 0; i < Steps.Count; i++)
            {
                var expectedLabel = "step " + Steps[i].Label;
                var actualLabel = stepSnapshots[i].Text("label");
                Assert.AreEqual(
                    expectedLabel,
                    actualLabel,
                    $"Capture case {Id} step {Steps[i].Number}: expected snapshot '{expectedLabel}', got '{actualLabel}'.");
            }

            foreach (var op in Ops.Where(o => o.Kind == "snapshot" && !o.IsStepSnapshot))
            {
                Assert.IsNotNull(
                    op.LiveRegistry,
                    $"Capture case {Id} non-step snapshot operation {op.Index} ('{op.Text("label")}') has no observed liveRegistry.");
            }
        }

        foreach (var step in Steps)
        {
            Assert.IsTrue(Directory.Exists(step.Folder), $"Capture case {Id}: step folder {step.Folder} is missing.");
            CollectionAssert.AreEquivalent(GoldenFiles.Names, step.Files.Keys.ToArray(), $"Capture case {Id} step {step.Number}: the file list is not the five files.");
        }

        if (StartRefused || StartFiles.Count > 0)
        {
            Assert.IsTrue(Directory.Exists(StartFolder), $"Capture case {Id} has crafted start files but no start folder.");
        }
    }

    private void VerifyBytes()
    {
        foreach (var step in Steps)
        {
            foreach (var name in GoldenFiles.Names)
            {
                VerifyFile($"{Id} step {step.Number} {name}", step.PathOf(name), step.Files[name]);
            }

            if (!step.IsCaseRoot)
            {
                var strays = Directory.GetFileSystemEntries(step.Folder).Select(Path.GetFileName).Where(n => !GoldenFiles.Names.Contains(n!)).ToArray();
                Assert.AreEqual(0, strays.Length, $"Capture case {Id} step {step.Number} holds files that are not golden files: {string.Join(", ", strays)}");
            }
        }

        foreach (var name in GoldenFiles.Names)
        {
            StartFiles.TryGetValue(name, out var expected);
            if (StartRefused || StartFiles.Count > 0)
            {
                VerifyFile($"{Id} start {name}", Path.Combine(StartFolder, name), expected);
            }
        }
    }

    private static void VerifyFile(string what, string path, GoldenFile? expected)
    {
        if (expected is null)
        {
            Assert.IsFalse(File.Exists(path), $"Golden {what}: case.json says the file does not exist, but {path} does.");
            return;
        }

        Assert.IsTrue(File.Exists(path), $"Golden {what}: case.json records a file but {path} is missing.");
        var bytes = File.ReadAllBytes(path);
        Assert.AreEqual(expected.Size, bytes.LongLength, $"Golden {what}: the size differs from case.json (a damaged or converted golden).");
        Assert.AreEqual(expected.Sha256, GoldenFiles.Sha256(bytes), true, $"Golden {what}: the SHA-256 differs from case.json (a damaged or converted golden).");
    }
}
