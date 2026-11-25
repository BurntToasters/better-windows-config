using System.Text;
using System.Text.Json.Nodes;
using Bwc.Gpo.Store;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The harness against itself (docs/failure-modes/policy-save.md, section R): it must accept the exact
/// golden files and fail on every kind of difference, so a green replay means something. None of these
/// calls <see cref="Bwc.Policy.Model.PolicySave"/>.
/// </summary>
[TestClass]
public sealed class GoldenHarnessTests
{
    private const string Saved = "A/Machine_Enabled";

    private static void SeedStep(GoldenSandbox sandbox, GoldenStep step)
    {
        foreach (var name in GoldenFiles.Names.Where(n => step.Files[n] is not null))
        {
            sandbox.SeedFrom(name, step.PathOf(name));
        }
    }

    [TestMethod]
    public void R4_Compare_AcceptsTheExactFilesOfAStep()
    {
        var golden = GoldenCase.Load(Saved);
        using var sandbox = new GoldenSandbox();
        SeedStep(sandbox, golden.Steps[1]);

        GoldenReplay.CompareStep(golden, sandbox, golden.Steps[1], "seeded");
    }

    [TestMethod]
    public void R4_Compare_FailsOnAChangedByte()
    {
        var golden = GoldenCase.Load(Saved);
        using var sandbox = new GoldenSandbox();
        SeedStep(sandbox, golden.Steps[1]);
        var bytes = sandbox.Read(GpoFileNames.GptIniFile)!;
        bytes[^3] ^= 0x01;
        sandbox.Overwrite(sandbox.GptPath, bytes);

        var ex = Assert.Throws<AssertFailedException>(() => GoldenReplay.CompareStep(golden, sandbox, golden.Steps[1], "tampered"));
        StringAssert.Contains(ex.Message, "differs");
    }

    [TestMethod]
    public void R4_Compare_FailsWhenAFileThatMustBeAbsentExists()
    {
        var golden = GoldenCase.Load(Saved);
        using var sandbox = new GoldenSandbox();
        SeedStep(sandbox, golden.Steps[1]);
        sandbox.Seed(sandbox.PolPath(GpoSide.User), [1, 2, 3]);

        var ex = Assert.Throws<AssertFailedException>(() => GoldenReplay.CompareStep(golden, sandbox, golden.Steps[1], "extra file"));
        StringAssert.Contains(ex.Message, "must be absent");
    }

    [TestMethod]
    public void R4_Compare_FailsWhenAFileThatMustExistIsMissing()
    {
        var golden = GoldenCase.Load(Saved);
        using var sandbox = new GoldenSandbox();
        SeedStep(sandbox, golden.Steps[1]);
        sandbox.Remove(sandbox.CmtxPath(GpoSide.Machine));

        var ex = Assert.Throws<AssertFailedException>(() => GoldenReplay.CompareStep(golden, sandbox, golden.Steps[1], "missing file"));
        StringAssert.Contains(ex.Message, "is missing");
    }

    [TestMethod]
    public void R5_Compare_FailsOnAnyOtherFileInTheGpoFolder()
    {
        var golden = GoldenCase.Load(Saved);
        using var sandbox = new GoldenSandbox();
        SeedStep(sandbox, golden.Steps[1]);
        sandbox.Seed(Path.Combine(sandbox.GpoRoot, "stray.tmp"), [1]);

        var ex = Assert.Throws<AssertFailedException>(() => GoldenReplay.CompareStep(golden, sandbox, golden.Steps[1], "stray"));
        StringAssert.Contains(ex.Message, "unexpected file");
    }

    [TestMethod]
    public void R1_Load_FailsOnAMissingCase()
    {
        var ex = Assert.Throws<AssertFailedException>(() => GoldenCase.Load("A/NoSuchCase"));
        StringAssert.Contains(ex.Message, "no folder");
    }

    [TestMethod]
    public void R1_Load_FailsOnAnIncompleteCase()
    {
        using var sandbox = new GoldenSandbox();
        var root = sandbox.CopyCase(GoldenCase.FolderOf(Saved), Saved);
        sandbox.Remove(Path.Combine(GoldenCase.FolderOf(Saved, root), "complete.json"));

        var ex = Assert.Throws<AssertFailedException>(() => GoldenCase.Load(Saved, root));
        StringAssert.Contains(ex.Message, "incomplete");
    }

    [TestMethod]
    public void R2_Load_FailsOnAGoldenFileThatDiffersFromCaseJson()
    {
        using var sandbox = new GoldenSandbox();
        var root = sandbox.CopyCase(GoldenCase.FolderOf(Saved), Saved);
        var step = Path.Combine(GoldenCase.FolderOf(Saved, root), "step-02-saved", GpoFileNames.GptIniFile);
        var bytes = File.ReadAllBytes(step);
        bytes[^3] ^= 0x01;
        sandbox.Overwrite(step, bytes);

        var ex = Assert.Throws<AssertFailedException>(() => GoldenCase.Load(Saved, root));
        StringAssert.Contains(ex.Message, "SHA-256");
    }

    [TestMethod]
    public void R3_Load_FailsOnAStrayFileInAStepFolder()
    {
        using var sandbox = new GoldenSandbox();
        var root = sandbox.CopyCase(GoldenCase.FolderOf(Saved), Saved);
        sandbox.Overwrite(Path.Combine(GoldenCase.FolderOf(Saved, root), "step-02-saved", "notes.txt"), [0]);

        var ex = Assert.Throws<AssertFailedException>(() => GoldenCase.Load(Saved, root));
        StringAssert.Contains(ex.Message, "not golden files");
    }

    [TestMethod]
    public void R9_Cap4Rule_FlagsADecimalCaseThatTheDialogDidNotNotice_AndNoGroupACase()
    {
        // The positive example is a copy of B/Pol_Decimal_MinMax with its alternate-value set changed to the CAP-4 pattern:
        // the state is Enabled before and after, the number is set with typed false and its text changes, Apply is
        // disabled after the set, and no message box or file change follows. The tracked golden is never edited.
        const string caseId = "B/Pol_Decimal_MinMax";
        using var sandbox = new GoldenSandbox();
        var root = sandbox.CopyCase(GoldenCase.FolderOf(caseId), caseId);
        var casePath = Path.Combine(GoldenCase.FolderOf(caseId, root), "case.json");
        var node = (JsonObject)JsonNode.Parse(File.ReadAllBytes(casePath))!;
        var alt1 = node["observed"]!.AsArray().OfType<JsonObject>().Single(o => o["index"]!.GetValue<int>() == 2);
        var data = (JsonObject)alt1["data"]!;
        var numberSet = (JsonObject)data["sets"]!.AsArray()[1]!;
        numberSet["typed"] = false;
        ((JsonObject)data["afterSet"]!["buttons"]!["Apply"]!)["enabled"] = false;
        var press = (JsonObject)data["presses"]!.AsArray()[0]!;
        press["messageBoxes"] = new JsonArray();
        press["files"]!["changed"] = false;
        alt1["changedFiles"] = new JsonArray();
        sandbox.Overwrite(casePath, Encoding.UTF8.GetBytes(node.ToJsonString()));

        Assert.IsNotNull(UnnoticedEditRule.FindInCase(GoldenCase.Load(caseId, root)));
        foreach (var id in PolicyGoldenReplayTests.GroupA.Select(c => "A/" + c).Concat(PolicyGoldenReplayTests.GroupK.Select(c => "K/" + c)))
        {
            Assert.IsNull(UnnoticedEditRule.FindInCase(GoldenCase.Load(id)), id);
        }

        // The ten recaptured cases (awaiting-recapture.txt no longer lists them) are not flagged.
        string[] recaptured =
        [
            "B/Pol_Both_WithElements.Computer", "B/Pol_Both_WithElements.User", "B/Pol_Decimal_MinMax", "B/Pol_Decimal_Soft",
            "B/Pol_Decimal_StoreAsText", "B/Pol_LongDecimal_Basic", "E/Decimal_EdgeValues", "E/Decimal_NotNumber",
            "E/Decimal_StoreAsText_OutOfRange", "E/LongDecimal_AboveMax",
        ];
        foreach (var id in recaptured)
        {
            Assert.IsNull(UnnoticedEditRule.FindInCase(GoldenCase.Load(id)), id);
        }
    }

    [TestMethod]
    public void R9_Cap4Rule_NeedsAllFourFacts()
    {
        Assert.IsTrue(UnnoticedEditRule.IsUnnoticed(true, false, true, false));
        Assert.IsFalse(UnnoticedEditRule.IsUnnoticed(false, false, true, false), "no text change");
        Assert.IsFalse(UnnoticedEditRule.IsUnnoticed(true, true, true, false), "the state changed");
        Assert.IsFalse(UnnoticedEditRule.IsUnnoticed(true, false, false, false), "Apply was enabled");
        Assert.IsFalse(UnnoticedEditRule.IsUnnoticed(true, false, true, true), "a message box refused it");
    }

    [TestMethod]
    public void G1_G2_Load_RejectsBrokenGroupGSnapshots_AndNoGroupGHasCap4()
    {
        const string caseId = "G/Machine_Ok";
        using (var sandbox = new GoldenSandbox())
        {
            var root = sandbox.CopyCase(GoldenCase.FolderOf(caseId), caseId);
            var casePath = Path.Combine(GoldenCase.FolderOf(caseId, root), "case.json");
            var node = (JsonObject)JsonNode.Parse(File.ReadAllBytes(casePath))!;
            var startObserved = node["observed"]!.AsArray().OfType<JsonObject>().Single(o => o["index"]!.GetValue<int>() == 0);
            startObserved.Remove("liveRegistry");
            sandbox.Overwrite(casePath, Encoding.UTF8.GetBytes(node.ToJsonString()));

            var ex = Assert.Throws<AssertFailedException>(() => GoldenCase.Load(caseId, root));
            StringAssert.Contains(ex.Message, "liveRegistry");
        }

        using (var sandbox = new GoldenSandbox())
        {
            var root = sandbox.CopyCase(GoldenCase.FolderOf(caseId), caseId);
            var casePath = Path.Combine(GoldenCase.FolderOf(caseId, root), "case.json");
            var node = (JsonObject)JsonNode.Parse(File.ReadAllBytes(casePath))!;
            var stepOp = node["ops"]!.AsArray().OfType<JsonObject>().First(o => o["kind"]!.GetValue<string>() == "snapshot" && o["label"]!.GetValue<string>().StartsWith("step ", StringComparison.Ordinal));
            stepOp["label"] = "step wrong-label";
            ((JsonObject)stepOp["op"]!)["label"] = "step wrong-label";
            sandbox.Overwrite(casePath, Encoding.UTF8.GetBytes(node.ToJsonString()));

            var ex = Assert.Throws<AssertFailedException>(() => GoldenCase.Load(caseId, root));
            StringAssert.Contains(ex.Message, "expected snapshot");
        }

        foreach (var id in PolicyGoldenReplayTests.GroupG.Select(c => "G/" + c))
        {
            Assert.IsNull(UnnoticedEditRule.FindInCase(GoldenCase.Load(id)), id);
        }
    }
}
