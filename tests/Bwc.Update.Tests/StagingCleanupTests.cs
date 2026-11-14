using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Staging folder cleanup (docs/failure-modes/update-verifier.md, sections M5, M6, M9).
[TestClass]
public sealed class StagingCleanupTests
{
    private const string KeepName = "fedcba9876543210fedcba9876543210";
    private const string OldName = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Prune_Leaves_Foreign_Entries()
    {
        using var dir = new TempDir();
        var root = dir.Sub("staging");
        var old = Path.Combine(root, OldName);
        var keep = Path.Combine(root, KeepName);
        var foreign = Path.Combine(root, "not-a-staging-folder");
        Directory.CreateDirectory(old);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "keep me");

        StagingCleanup.Prune(root, keep);

        Assert.IsFalse(Directory.Exists(old), "an older staging folder is removed");
        Assert.IsTrue(Directory.Exists(keep), "the kept folder stays");
        Assert.IsTrue(Directory.Exists(foreign), "a folder that is not a staging folder is left alone");
        Assert.IsTrue(File.Exists(Path.Combine(root, "notes.txt")), "a file in the root is left alone");
    }

    [TestMethod]
    public void Prune_Keeps_Folder_Younger_Than_24_Hours()
    {
        using var dir = new TempDir();
        var root = dir.Sub("staging");
        var young = Path.Combine(root, OldName);
        var keep = Path.Combine(root, KeepName);
        Directory.CreateDirectory(young);
        Directory.CreateDirectory(keep);

        StagingCleanup.Prune(root, keep);

        Assert.IsTrue(Directory.Exists(young), "a second install's staged update is not destroyed");
    }

    [TestMethod]
    public void Prune_Uses_Receipt_Write_Time_For_Age()
    {
        using var dir = new TempDir();
        var root = dir.Sub("staging");
        var folder = Path.Combine(root, OldName);
        var keep = Path.Combine(root, KeepName);
        Directory.CreateDirectory(folder);
        Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-3));
        var receipt = Path.Combine(folder, "receipt.json");
        File.WriteAllText(receipt, "{}");
        Directory.CreateDirectory(keep);

        StagingCleanup.Prune(root, keep);

        Assert.IsTrue(Directory.Exists(folder), "a receipt written recently keeps an old folder");
    }

    [TestMethod]
    public void Prune_Skips_Reparse_Point()
    {
        using var dir = new TempDir();
        var root = dir.Sub("staging");
        var target = dir.Sub("junction-target");
        File.WriteAllText(Path.Combine(target, "precious.txt"), "do not delete through the link");
        var link = Path.Combine(root, OldName);
        Directory.CreateDirectory(root);
        if (!Links.TryMakeJunction(link, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        StagingCleanup.Prune(root, Path.Combine(root, KeepName));

        Assert.IsTrue(Directory.Exists(link), "the link itself is not followed, so it is not removed either");
        Assert.IsTrue(File.Exists(Path.Combine(target, "precious.txt")), "the link target is never touched");
    }

    [TestMethod]
    public void Discard_Incomplete_Removes_Folders_Without_Receipt()
    {
        using var dir = new TempDir();
        var root = dir.Sub("staging");
        var incomplete = Path.Combine(root, OldName);
        var complete = Path.Combine(root, KeepName);
        var foreign = Path.Combine(root, "not-a-staging-folder");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "installer.exe"), "partial");
        Directory.CreateDirectory(complete);
        File.WriteAllText(Path.Combine(complete, "receipt.json"), "{}");
        Directory.CreateDirectory(foreign);

        var removed = StagingCleanup.DiscardIncomplete(root);

        Assert.AreEqual(1, removed);
        Assert.IsFalse(Directory.Exists(incomplete), "a killed download leaves no partial installer behind");
        Assert.IsTrue(Directory.Exists(complete), "a folder with a receipt is never discarded");
        Assert.IsTrue(Directory.Exists(foreign));
    }

    [TestMethod]
    public void Discard_Incomplete_Without_Root_Removes_Nothing()
    {
        using var dir = new TempDir();

        Assert.AreEqual(0, StagingCleanup.DiscardIncomplete(Path.Combine(dir.Root, "missing")));
    }

}
