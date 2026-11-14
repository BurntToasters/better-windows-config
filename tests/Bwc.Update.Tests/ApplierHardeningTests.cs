using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Apply-mode hardening from the 0.1.1 audit (docs/failure-modes/update-verifier.md, section S).
[TestClass]
public sealed class ApplierHardeningTests
{
    [TestMethod]
    public async Task Apply_Locked_Receipt_Relaunches_Hub()
    {
        using var h = new UpdateApplierTests.Harness();
        using var held = File.Open(h.Receipt, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.ReceiptInvalid, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.HubLaunched, "the user still gets the app back");
        Assert.IsFalse(outcome.InstallerStarted);
        Assert.AreEqual(0, h.Host.Runs.Count, "no installer runs");
        Assert.AreEqual(1, h.Host.Started.Count);
        CollectionAssert.AreEqual(new[] { "--update-result", "ReceiptInvalid" }, h.Host.Started[0].Args.ToArray());
    }

    [TestMethod]
    public async Task Unexpected_In_Apply_Relaunches_Hub()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Live = _ => throw new InvalidOperationException("simulated host failure");

        var outcome = await h.Applier.ApplyAsync(h.Request(pids: [4242]), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.Unexpected, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.HubLaunched);
        Assert.IsFalse(outcome.InstallerStarted);
        Assert.AreEqual(0, h.Host.Runs.Count, "the installer is not started");
        Assert.AreEqual(1, h.Host.Started.Count);
        CollectionAssert.AreEqual(new[] { "--update-result", "Unexpected" }, h.Host.Started[0].Args.ToArray());
    }

    [TestMethod]
    public async Task Staging_Path_8dot3_Applies()
    {
        using var h = new UpdateApplierTests.Harness(rootName: "long staging folder");
        var shortRoot = Links.ShortPath(h.Root);
        if (string.Equals(shortRoot, h.Root, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("8.3 short names are disabled on this volume, so the short form cannot be produced");
        }

        var folderName = Path.GetFileName(h.Folder);
        var request = h.Request() with
        {
            StagingRoot = shortRoot,
            InstallerPath = Path.Combine(shortRoot, folderName, "installer.exe"),
            ReceiptPath = Path.Combine(shortRoot, folderName, "receipt.json"),
        };

        var outcome = await h.Applier.ApplyAsync(request, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.InstallerStarted);
        Assert.AreEqual(1, h.Host.Runs.Count, "the verified installer is started");
    }

    [TestMethod]
    public async Task Staging_Via_Junction_Applies()
    {
        using var h = new UpdateApplierTests.Harness(rootName: "staging");
        using var via = new TempDir();
        var junction = Path.Combine(via.Root, "via");
        if (!Links.TryMakeJunction(junction, h.Dir.Root))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        // The staging root is reached through a junction in its parent, as when TEMP is relocated through one.
        var stagingRoot = Path.Combine(junction, "staging");
        var folderName = Path.GetFileName(h.Folder);
        var request = h.Request() with
        {
            StagingRoot = stagingRoot,
            InstallerPath = Path.Combine(stagingRoot, folderName, "installer.exe"),
            ReceiptPath = Path.Combine(stagingRoot, folderName, "receipt.json"),
        };

        var outcome = await h.Applier.ApplyAsync(request, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.InstallerStarted);
        Assert.AreEqual(1, h.Host.Runs.Count);
    }
}
