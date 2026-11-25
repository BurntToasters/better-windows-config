using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>
/// Refusals of the sandbox write policy, and the shape of the system one. This file names the real folder
/// (read-only string comparison), so it holds no write API.
/// </summary>
[TestClass]
public sealed class SandboxGpoStorePolicyTests
{
    private static void AssertDenied(Action action, string message = "")
    {
        var ex = Assert.ThrowsExactly<GpoStoreException>(action, message);
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason, message);
    }

    [TestMethod]
    public void Constructor_AcceptsARootStrictlyInsideTheRunDataRoot()
    {
        using var fx = new SandboxGpoFixture();
        var policy = new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot);
        Assert.AreEqual(fx.GpoRoot, policy.GpoRoot);
        Assert.AreEqual(fx.GpoRoot + ".backup", policy.BackupRoot);
        Assert.IsInstanceOfType<RecordingPolicyRefresher>(policy.Refresher);
    }

    [TestMethod]
    public void Constructor_RefusesARootOutsideTheRunDataRoot()
    {
        using var fx = new SandboxGpoFixture();
        AssertDenied(() => new SandboxGpoStorePolicy(Path.Combine(Path.GetTempPath(), "bwc-elsewhere-" + Guid.NewGuid().ToString("N")), fx.RunRoot), "outside");
        AssertDenied(() => new SandboxGpoStorePolicy(fx.RunRoot, fx.RunRoot), "the run data root itself");
        AssertDenied(() => new SandboxGpoStorePolicy(Path.GetDirectoryName(fx.RunRoot)!, fx.RunRoot), "its parent");
    }

    [TestMethod]
    public void Constructor_RefusesASiblingThatOnlySharesTheNamePrefix()
    {
        using var fx = new SandboxGpoFixture();
        AssertDenied(() => new SandboxGpoStorePolicy(fx.RunRoot + "2" + Path.DirectorySeparatorChar + "gpo", fx.RunRoot));
    }

    [TestMethod]
    public void Constructor_RefusesDotDotThatEscapesTheRunDataRoot()
    {
        using var fx = new SandboxGpoFixture();
        var escaping = Path.Combine(fx.RunRoot, "a", "..", "..", "escaped-" + Guid.NewGuid().ToString("N"));
        AssertDenied(() => new SandboxGpoStorePolicy(escaping, fx.RunRoot));
    }

    [TestMethod]
    public void Constructor_AcceptsDotDotThatStaysInside()
    {
        using var fx = new SandboxGpoFixture();
        var policy = new SandboxGpoStorePolicy(Path.Combine(fx.RunRoot, "a", "..", "b"), fx.RunRoot);
        Assert.AreEqual(Path.Combine(fx.RunRoot, "b"), policy.GpoRoot);
    }

    [TestMethod]
    public void Constructor_RefusesEmptyAndInvalidPaths()
    {
        using var fx = new SandboxGpoFixture();
        Assert.ThrowsExactly<ArgumentException>(() => new SandboxGpoStorePolicy("", fx.RunRoot));
        Assert.ThrowsExactly<ArgumentException>(() => new SandboxGpoStorePolicy(fx.GpoRoot, ""));
        AssertDenied(() => new SandboxGpoStorePolicy(fx.GpoRoot + "|bad", fx.RunRoot));
    }

    [TestMethod]
    public void Constructor_RefusesABackupRootInsideTheGpoRoot_OrOutsideTheRunDataRoot()
    {
        using var fx = new SandboxGpoFixture();
        AssertDenied(() => new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot, Path.Combine(fx.GpoRoot, "backup")));
        AssertDenied(() => new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot, Path.GetTempPath() + "bwc-elsewhere-backup"));
        AssertDenied(() => new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot, fx.GpoRoot));
    }

    [TestMethod]
    public void Constructor_RefusesAReparsePointAtTheRoot_OrOnTheChainToIt()
    {
        using var fx = new SandboxGpoFixture();
        var link = Path.Combine(fx.RunRoot, "link");
        if (!fx.TryMakeJunction(link, Path.Combine(fx.RunRoot, "real")))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        AssertDenied(() => new SandboxGpoStorePolicy(link, fx.RunRoot), "the root is a junction");
        AssertDenied(() => new SandboxGpoStorePolicy(Path.Combine(link, "gpo"), fx.RunRoot), "a junction on the chain");
    }

    [TestMethod]
    public void Constructor_RefusesAReparsePointBelowTheRoot()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.GpoRoot);
        var link = Path.Combine(fx.GpoRoot, GpoFileNames.MachineFolder);
        if (!fx.TryMakeJunction(link, Path.Combine(fx.RunRoot, "real")))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        AssertDenied(() => new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot));
    }

    [TestMethod]
    public void EnsureWriteAllowed_AcceptsPathsInsideTheGpoRootAndTheBackupRoot()
    {
        using var fx = new SandboxGpoFixture();
        fx.Policy.EnsureWriteAllowed(fx.PolPath(GpoSide.Machine));
        fx.Policy.EnsureWriteAllowed(fx.GptPath);
        fx.Policy.EnsureWriteAllowed(Path.Combine(fx.BackupRoot, "20000101-000000-000", "Machine", "Registry.pol.absent"));
    }

    [TestMethod]
    public void EnsureWriteAllowed_RefusesEveryEscape()
    {
        using var fx = new SandboxGpoFixture();
        var root = fx.GpoRoot;
        var cases = new Dictionary<string, string>
        {
            ["outside"] = Path.Combine(fx.RunRoot, "other", "x.bin"),
            ["dot dot"] = Path.Combine(root, "..", "x.bin"),
            ["deep dot dot"] = Path.Combine(root, "Machine", "..", "..", "x.bin"),
            ["sibling prefix"] = root + "2" + Path.DirectorySeparatorChar + "x.bin",
            ["the root itself"] = root,
            ["the backup root itself"] = fx.BackupRoot,
            ["stream"] = Path.Combine(root, "gpt.ini:stream"),
            ["drive relative"] = "x.bin",
            ["empty"] = "",
        };

        foreach (var (name, path) in cases)
        {
            AssertDenied(() => fx.Policy.EnsureWriteAllowed(path), name);
        }
    }

    [TestMethod]
    public void EnsureWriteAllowed_IsCaseInsensitiveAboutTheRoot()
    {
        using var fx = new SandboxGpoFixture();
        fx.Policy.EnsureWriteAllowed(Path.Combine(fx.GpoRoot.ToUpperInvariant(), "gpt.ini"));
    }

    [TestMethod]
    public void EnsureWriteAllowed_RefusesAJunctionCreatedAfterTheConstructor()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.GpoRoot);
        var link = Path.Combine(fx.GpoRoot, GpoFileNames.UserFolder);
        if (!fx.TryMakeJunction(link, Path.Combine(fx.RunRoot, "real")))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        AssertDenied(() => fx.Policy.EnsureWriteAllowed(fx.PolPath(GpoSide.User)));
        fx.Policy.EnsureRemoveAllowed(link);
        fx.Policy.EnsureWriteAllowed(fx.PolPath(GpoSide.Machine));
    }

    [TestMethod]
    public void WritingThroughAJunction_NeverReachesTheTarget()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.GpoRoot);
        var target = Path.Combine(fx.RunRoot, "real");
        var link = Path.Combine(fx.GpoRoot, GpoFileNames.MachineFolder);
        if (!fx.TryMakeJunction(link, target))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.WriteAtomic(fx.PolPath(GpoSide.Machine), [1]));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        Assert.AreEqual(0, Directory.GetFiles(target).Length);
    }

    [TestMethod]
    public void LockPath_IsAFileInTheSandboxBackupRoot_AndDiffersPerSandbox()
    {
        using var fx = new SandboxGpoFixture();
        var same = new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot);
        var other = new SandboxGpoStorePolicy(Path.Combine(fx.RunRoot, "other"), fx.RunRoot);

        Assert.AreEqual(Path.Combine(fx.Policy.BackupRoot, "write.lock"), fx.Policy.LockPath);
        Assert.AreEqual(fx.Policy.LockPath, same.LockPath);
        Assert.AreNotEqual(fx.Policy.LockPath, other.LockPath);
        fx.Policy.EnsureWriteAllowed(fx.Policy.LockPath);
        StringAssert.StartsWith(fx.Policy.LockPath, fx.RunRoot, "the lock file never leaves the run data root");
    }

    [TestMethod]
    public void SystemPolicy_HasTheShippingShape_AndRefusesPathsOutsideItsRoots()
    {
        var policy = new SystemGpoStorePolicy();

        Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "GroupPolicy"), policy.GpoRoot);
        Assert.IsTrue(
            policy.BackupRoot.EndsWith(Path.Combine("BetterWindowsConfig", "gpedit", "backup"), StringComparison.OrdinalIgnoreCase),
            policy.BackupRoot);
        Assert.AreEqual(Path.Combine(policy.BackupRoot, "write.lock"), policy.LockPath);
        Assert.IsInstanceOfType<UserenvPolicyRefresher>(policy.Refresher);

        AssertDenied(() => policy.EnsureWriteAllowed(Path.Combine(Environment.SystemDirectory, "drivers", "x.sys")));
        AssertDenied(() => policy.EnsureWriteAllowed(Path.Combine(policy.GpoRoot, "..", "x.bin")));
        AssertDenied(() => policy.EnsureWriteAllowed(policy.GpoRoot + "2" + Path.DirectorySeparatorChar + "x.bin"));
    }
}
