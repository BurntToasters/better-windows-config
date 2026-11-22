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
        Assert.IsNotNull(UnnoticedEditRule.FindInCase(GoldenCase.Load("B/Pol_Decimal_MinMax")));
        foreach (var id in PolicyGoldenReplayTests.GroupA.Select(c => "A/" + c).Concat(PolicyGoldenReplayTests.GroupK.Select(c => "K/" + c)))
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
}
