using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;

namespace Bwc.Gpo.Tests.Golden;

/// <summary>
/// The store alone against the six start files classic refused to open (group D, D9;
/// docs/failure-modes/gpo-store.md, section
/// L). Each start file is put into a temp sandbox; the store must report a malformed policy file on open, refuse
/// a save with
/// <see cref="GpoStoreFailure.Malformed"/> before any write, and leave the folder as it was.
/// </summary>
[TestClass]
public sealed class PolMalformedStartGoldenTests
{
    private const string Key = "Software\\Policies\\BWC-Oracle\\Basics";

    private static readonly string[] Refused =
    [
        "File_BadHeader", "File_Empty", "File_MissingCloseBracket", "File_OnlyGarbage", "File_TruncatedLastRecord", "File_VersionTwo",
    ];

    public static IEnumerable<string[]> Rows() => Refused.Select(c => new[] { c });

    private static string FlatPol => GoldenData.FlatName(GpoSide.Machine, GpoFileNames.RegistryPol);

    [TestMethod]
    public void L1_TheRefusedStartsOfGroupDAreExactlyTheSixMalformedFiles()
    {
        var refused = GoldenData.CasesOf("D").Where(c => GoldenData.StartRefused("D/" + c)).ToArray();
        CollectionAssert.AreEqual(Refused, refused, "The cases classic refused to open changed; update the six and the failure-mode rows.");
    }

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void L2_OpenReportsAMalformedPolFileAndNoDocument(string name)
    {
        using var fx = new SandboxGpoFixture();
        var start = GoldenData.StartBytes("D/" + name, FlatPol) ?? throw new AssertFailedException($"D/{name} has no machine policy file at start.");
        fx.PutRawFile(fx.PolPath(GpoSide.Machine), start);

        var snapshot = fx.NewStore().Open();

        Assert.IsTrue(snapshot.Machine.PolExists, $"D/{name}: the start file is not seen.");
        Assert.IsNull(snapshot.Machine.Pol, $"D/{name}: classic refused this file and the store opened it as a document.");
        Assert.IsInstanceOfType<PolFormatException>(snapshot.Machine.PolFailure, $"D/{name}: no format failure is reported.");
        Assert.IsNull(snapshot.User.PolFailure, $"D/{name}: the failure leaked to the user side.");
    }

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void L3_ASaveOverARefusedStartThrowsMalformedAndWritesNothing(string name)
    {
        using var fx = new SandboxGpoFixture();
        var start = GoldenData.StartBytes("D/" + name, FlatPol)!;
        fx.PutRawFile(fx.PolPath(GpoSide.Machine), start);

        var ex = Assert.ThrowsExactly<GpoStoreException>(
            () => fx.NewStore().Save(GpoSide.Machine, s => GpoStorePolHelper.SetDword(s.Pol, Key, "FlagValue", 1)),
            $"D/{name}: a save over a refused file must throw.");

        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason, $"D/{name}: the failure is not Malformed.");
        Assert.AreEqual(0, ex.WrittenFiles.Count, $"D/{name}: the refusal came after a write.");
        CollectionAssert.AreEqual(start, fx.Read(fx.PolPath(GpoSide.Machine)), $"D/{name}: the start file changed.");
        Assert.IsNull(fx.Read(fx.GptPath), $"D/{name}: a version file appeared.");
        Assert.IsNull(fx.Read(fx.PolPath(GpoSide.User)), $"D/{name}: a user policy file appeared.");
        Assert.IsNull(fx.Read(fx.CmtxPath(GpoSide.Machine)), $"D/{name}: a comment file appeared.");
        Assert.IsNull(fx.Read(fx.CmtxPath(GpoSide.User)), $"D/{name}: a user comment file appeared.");
        Assert.AreEqual(0, fx.Log.Count(l => l.StartsWith("write:", StringComparison.Ordinal)), $"D/{name}: the store wrote a file.");
        Assert.AreEqual(0, fx.Refresher.Calls.Count, $"D/{name}: the policy was refreshed.");
        Assert.AreEqual(0, fx.BackupFolders().Length, $"D/{name}: a backup was made for a refused save.");
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length, $"D/{name}: a temp file was left.");
    }
}
