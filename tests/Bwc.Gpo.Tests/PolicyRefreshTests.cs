using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>Only the recorder runs here. The real refresher is never called by a test.</summary>
[TestClass]
public sealed class PolicyRefreshTests
{
    [TestMethod]
    public void Recorder_AppendsOneLinePerCall_InsideTheSandboxRoot()
    {
        using var fx = new SandboxGpoFixture();
        var policy = new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot);
        var recordFile = Path.Combine(fx.GpoRoot, SandboxGpoStorePolicy.RefreshRecordFileName);

        policy.Refresher.Refresh(GpoSide.Machine);
        policy.Refresher.Refresh(GpoSide.User);
        policy.Refresher.Refresh(GpoSide.Machine);

        Assert.AreEqual("Machine\r\nUser\r\nMachine\r\n", System.Text.Encoding.UTF8.GetString(fx.Read(recordFile)!));
    }

    [TestMethod]
    public void Recorder_IsAskedNothingNative_AndCreatesNoFileWhenNotCalled()
    {
        using var fx = new SandboxGpoFixture();
        var policy = new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot);
        Assert.IsNotNull(policy.Refresher);
        Assert.IsFalse(File.Exists(Path.Combine(fx.GpoRoot, SandboxGpoStorePolicy.RefreshRecordFileName)));
    }

    [TestMethod]
    public void Recorder_WritesGoThroughTheWritePolicy()
    {
        using var fx = new SandboxGpoFixture();
        var outside = Path.Combine(fx.RunRoot, "elsewhere", "refresh.log");
        var recorder = new RecordingPolicyRefresher(outside, fx.Seeder);

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => recorder.Refresh(GpoSide.Machine));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        Assert.IsFalse(File.Exists(outside));
    }

    [TestMethod]
    public void SaveThroughTheSandboxPolicy_RecordsOneRefreshLinePerPolicyWrite()
    {
        using var fx = new SandboxGpoFixture();
        var policy = new SandboxGpoStorePolicy(fx.GpoRoot, fx.RunRoot);
        var store = new GpoStore(policy);

        store.Save(GpoSide.User, s => GpoStorePolHelper.SetDword(s.Pol, "Software\\Policies\\BWC-Test", "A", 1u));
        store.Save(GpoSide.User, _ => { });
        store.Save(GpoSide.Machine, s => GpoStorePolHelper.SetDword(s.Pol, "Software\\Policies\\BWC-Test", "A", 1u));

        var recordFile = Path.Combine(fx.GpoRoot, SandboxGpoStorePolicy.RefreshRecordFileName);
        Assert.AreEqual("User\r\nMachine\r\n", System.Text.Encoding.UTF8.GetString(fx.Read(recordFile)!));
    }
}
