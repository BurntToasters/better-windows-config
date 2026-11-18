using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

[TestClass]
public sealed class GpoAccessTests
{
    private static GpoAccessProbes Probes(bool elevated, string? edition, bool canWrite) =>
        new() { IsElevated = () => elevated, EditionId = () => edition, CanWriteFolder = _ => canWrite };

    [TestMethod]
    public void Check_AllClear_IsNotReadOnly()
    {
        using var fx = new SandboxGpoFixture();
        var result = GpoAccess.Check(fx.Policy, Probes(true, "Professional", true));
        Assert.IsFalse(result.IsReadOnly);
        Assert.AreEqual(0, result.Reasons.Count);
    }

    [TestMethod]
    public void Check_EachReason_IsReportedAlone()
    {
        using var fx = new SandboxGpoFixture();
        CollectionAssert.AreEqual(new[] { GpoReadOnlyReason.NotElevated }, GpoAccess.Check(fx.Policy, Probes(false, "Enterprise", true)).Reasons.ToArray());
        CollectionAssert.AreEqual(new[] { GpoReadOnlyReason.NoWriteAccess }, GpoAccess.Check(fx.Policy, Probes(true, "Enterprise", false)).Reasons.ToArray());
        CollectionAssert.AreEqual(new[] { GpoReadOnlyReason.HomeEdition }, GpoAccess.Check(fx.Policy, Probes(true, "Core", true)).Reasons.ToArray());
    }

    [TestMethod]
    public void Check_AllThatApply_AreReportedInAFixedOrder()
    {
        using var fx = new SandboxGpoFixture();
        var result = GpoAccess.Check(fx.Policy, Probes(false, "CoreN", false));
        Assert.IsTrue(result.IsReadOnly);
        CollectionAssert.AreEqual(
            new[] { GpoReadOnlyReason.NotElevated, GpoReadOnlyReason.NoWriteAccess, GpoReadOnlyReason.HomeEdition },
            result.Reasons.ToArray());
    }

    [TestMethod]
    public void Check_HandsTheProbeTheGpoRoot()
    {
        using var fx = new SandboxGpoFixture();
        string? asked = null;
        GpoAccess.Check(fx.Policy, new GpoAccessProbes { IsElevated = () => true, EditionId = () => null, CanWriteFolder = p => { asked = p; return true; } });
        Assert.AreEqual(fx.GpoRoot, asked);
    }

    [TestMethod]
    public void Check_AProbeThatThrows_IsTheSafeSide()
    {
        using var fx = new SandboxGpoFixture();
        var result = GpoAccess.Check(fx.Policy, new GpoAccessProbes
        {
            IsElevated = () => throw new InvalidOperationException("x"),
            EditionId = () => throw new InvalidOperationException("x"),
            CanWriteFolder = _ => throw new InvalidOperationException("x"),
        });
        CollectionAssert.AreEqual(new[] { GpoReadOnlyReason.NotElevated, GpoReadOnlyReason.NoWriteAccess }, result.Reasons.ToArray(), "unknown edition is not Home");
    }

    [TestMethod]
    [DataRow("Core", true)]
    [DataRow("CoreN", true)]
    [DataRow("CoreSingleLanguage", true)]
    [DataRow("CoreCountrySpecific", true)]
    [DataRow("core", true)]
    [DataRow("Professional", false)]
    [DataRow("Enterprise", false)]
    [DataRow("Education", false)]
    [DataRow("ServerStandard", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsHomeEdition_MatchesTheCoreFamily(string? edition, bool expected)
    {
        Assert.AreEqual(expected, GpoAccess.IsHomeEdition(edition));
    }

    [TestMethod]
    public void ThisTestProcess_IsNotElevated_SoNotElevatedIsReported()
    {
        if (GpoAccess.IsElevated())
        {
            Assert.Inconclusive("The test process is elevated; this case needs an unelevated one.");
        }

        using var fx = new SandboxGpoFixture();
        var result = GpoAccess.Check(fx.Policy);
        CollectionAssert.Contains(result.Reasons.ToArray(), GpoReadOnlyReason.NotElevated);
        Assert.IsTrue(result.IsReadOnly);
    }

    [TestMethod]
    public void CanWriteFolder_IsTrueForOwnTempFolder_AndForAFolderThatDoesNotExistYet()
    {
        using var fx = new SandboxGpoFixture();
        Assert.IsTrue(GpoAccess.CanWriteFolder(fx.RunRoot));
        Assert.IsTrue(GpoAccess.CanWriteFolder(fx.GpoRoot), "missing: the nearest existing ancestor is checked");
        Assert.IsTrue(GpoAccess.CanWriteFolder(Path.Combine(fx.GpoRoot, "a", "b")));
        Assert.IsFalse(Directory.Exists(fx.GpoRoot), "the check creates nothing");
    }

    [TestMethod]
    public void CanWriteFolder_IsFalseForAFolderTheUnelevatedUserCannotWrite()
    {
        if (GpoAccess.IsElevated())
        {
            Assert.Inconclusive("The test process is elevated; administrators can write the system folder.");
        }

        Assert.IsFalse(GpoAccess.CanWriteFolder(Environment.SystemDirectory));
        Assert.IsFalse(GpoAccess.CanWriteFolder(Path.Combine(Environment.SystemDirectory, "a-folder-that-does-not-exist-bwc")));
    }

    [TestMethod]
    public void CanWriteFolder_WithAFolderUnderAFile_IsFalseNotAThrow()
    {
        using var fx = new SandboxGpoFixture();
        fx.PutRawFile(fx.GptPath, [1]);
        Assert.IsFalse(GpoAccess.CanWriteFolder(Path.Combine(fx.GptPath, "child")));
        Assert.IsFalse(GpoAccess.CanWriteFolder(""));
    }

    [TestMethod]
    public void CanWriteFolder_ChecksTheExistingMachineAndUserFolders()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.GpoRoot);
        fx.MakeFolder(Path.Combine(fx.GpoRoot, GpoFileNames.MachineFolder));
        Assert.IsTrue(GpoAccess.CanWriteFolder(fx.GpoRoot));
    }

    [TestMethod]
    public void TheAccessCheck_ChangesNothingOnDisk()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        var before = Directory.GetFileSystemEntries(fx.RunRoot, "*", SearchOption.AllDirectories).Order().ToArray();
        var writeTime = File.GetLastWriteTimeUtc(fx.GptPath);

        GpoAccess.Check(fx.Policy);

        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(fx.RunRoot, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.AreEqual(writeTime, File.GetLastWriteTimeUtc(fx.GptPath));
        Assert.AreEqual(0, fx.Events.Count);
    }
}
