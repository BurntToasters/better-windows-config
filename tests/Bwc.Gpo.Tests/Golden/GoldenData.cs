using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bwc.Gpo.Store;

namespace Bwc.Gpo.Tests.Golden;

/// <summary>
/// Reads golden files of the classic capture (docs/parity/oracle/gpedit/goldens) for the file-rule tests.
/// Every file is checked against the size and SHA-256 in case.json first, so a damaged golden fails
/// loudly. A missing case or step fails; it never skips.
/// </summary>
internal static class GoldenData
{
    private static readonly Lazy<string> Root = new(FindGoldens);

    internal static string FlatName(GpoSide side, string file) => GpoFileNames.FolderName(side) + "." + file;

    /// <summary>The bytes of one file in one step, or null when classic had no such file in that step.</summary>
    /// <param name="caseId">The case, for example the group, a slash and the folder name.</param>
    /// <param name="step">The step folder name from case.json, for example a start or saved step.</param>
    /// <param name="flatName">The flat file name: a side, a dot and the file name, or the version file
    /// name.</param>
    /// <returns>The bytes, or null.</returns>
    internal static byte[]? Bytes(string caseId, string step, string flatName)
    {
        var caseFolder = Path.Combine(Root.Value, caseId.Replace('/', Path.DirectorySeparatorChar));
        var caseJson = Path.Combine(caseFolder, "case.json");
        Assert.IsTrue(File.Exists(caseJson), $"Capture case {caseId} has no case.json in {caseFolder}.");
        Assert.IsTrue(File.Exists(Path.Combine(caseFolder, "complete.json")), $"Capture case {caseId} is incomplete: complete.json is missing.");

        var steps = (JsonArray)JsonNode.Parse(File.ReadAllText(caseJson))!["steps"]!;
        var entry = steps.OfType<JsonObject>().FirstOrDefault(s => s["folder"]!.GetValue<string>() == step)
            ?? throw new AssertFailedException($"Capture case {caseId} has no step folder '{step}'.");
        var recorded = ((JsonObject)entry["files"]!)[flatName];
        var path = Path.Combine(step == "." ? caseFolder : Path.Combine(caseFolder, step), flatName);
        if (recorded is null)
        {
            Assert.IsFalse(File.Exists(path), $"Golden {caseId}/{step}/{flatName}: case.json says it does not exist, but it does.");
            return null;
        }

        Assert.IsTrue(File.Exists(path), $"Golden {caseId}/{step}/{flatName} is missing.");
        var bytes = File.ReadAllBytes(path);
        Assert.AreEqual(recorded["size"]!.GetValue<long>(), bytes.LongLength, $"Golden {caseId}/{step}/{flatName}: the size differs from case.json.");
        Assert.AreEqual(
            recorded["sha256"]!.GetValue<string>(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            true,
            $"Golden {caseId}/{step}/{flatName}: the SHA-256 differs from case.json.");
        return bytes;
    }

    /// <summary>The names of the case folders of one group, in name order.</summary>
    /// <param name="group">The group folder name, for example a single capital letter.</param>
    /// <returns>The case folder names.</returns>
    internal static string[] CasesOf(string group) =>
        Directory.GetDirectories(Path.Combine(Root.Value, group)).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Whether classic refused to open the start files of a case (startRefused in case.json).</summary>
    /// <param name="caseId">The case.</param>
    /// <returns>True when the capture says classic refused the start.</returns>
    internal static bool StartRefused(string caseId)
    {
        var caseJson = Path.Combine(Root.Value, caseId.Replace('/', Path.DirectorySeparatorChar), "case.json");
        Assert.IsTrue(File.Exists(caseJson), $"Capture case {caseId} has no case.json.");
        return JsonNode.Parse(File.ReadAllText(caseJson))!["startRefused"]?.GetValue<bool>() ?? false;
    }

    /// <summary>The bytes of one start file of a case (the startState of case.json, held in the start folder),
    /// checked against the
    /// size and SHA-256 recorded there.</summary>
    /// <param name="caseId">The case.</param>
    /// <param name="flatName">The flat file name: a side, a dot and the file name.</param>
    /// <returns>The bytes, or null when the case starts without that file.</returns>
    internal static byte[]? StartBytes(string caseId, string flatName)
    {
        var caseFolder = Path.Combine(Root.Value, caseId.Replace('/', Path.DirectorySeparatorChar));
        var files = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(caseFolder, "case.json")))!["startState"]!["files"]!;
        var entry = files.OfType<JsonObject>().FirstOrDefault(f => f["name"]!.GetValue<string>() == flatName);
        if (entry is null)
        {
            return null;
        }

        var path = Path.Combine(caseFolder, "start", flatName);
        Assert.IsTrue(File.Exists(path), $"Start file {caseId}/start/{flatName} is missing.");
        var bytes = File.ReadAllBytes(path);
        Assert.AreEqual(entry["size"]!.GetValue<long>(), bytes.LongLength, $"Start file {caseId}/start/{flatName}: the size differs from case.json.");
        Assert.AreEqual(
            entry["sha256"]!.GetValue<string>(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            true,
            $"Start file {caseId}/start/{flatName}: the SHA-256 differs from case.json.");
        return bytes;
    }

    internal static byte[]? Gpt(string caseId, string step) => Bytes(caseId, step, GpoFileNames.GptIniFile);

    internal static byte[]? Pol(string caseId, string step, GpoSide side) => Bytes(caseId, step, FlatName(side, GpoFileNames.RegistryPol));

    internal static byte[]? Cmtx(string caseId, string step, GpoSide side) => Bytes(caseId, step, FlatName(side, GpoFileNames.CommentCmtx));

    private static string FindGoldens()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return Path.Combine(dir.FullName, "docs", "parity", "oracle", "gpedit", "goldens");
            }
        }

        throw new AssertFailedException("The repository root (the folder holding AGENTS.md) is not above " + AppContext.BaseDirectory);
    }
}
