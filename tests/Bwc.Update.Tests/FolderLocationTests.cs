using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Held-folder location check (docs/failure-modes/update-verifier.md, sections S6 and S23).
[TestClass]
public sealed class FolderLocationTests
{
    [TestMethod]
    public void Installer_In_Held_Folder_Accepted()
    {
        using var dir = new TempDir();
        var held = dir.Sub("held");
        var installer = Path.Combine(held, InstallerPaths.InstallerName);
        File.WriteAllText(installer, "x");
        using SafeFileHandle? folder = InstallerPaths.OpenFolder(held);
        Assert.IsNotNull(folder, "a folder opens with backup semantics, without elevation");
        using var file = File.Open(installer, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.IsTrue(InstallerPaths.InFolder(folder, InstallerPaths.FinalPathOf(file.SafeFileHandle)!));
    }

    [TestMethod]
    public void Installer_Outside_Held_Folder_Refused()
    {
        using var dir = new TempDir();
        var held = dir.Sub("held");
        var other = dir.Sub("other");
        File.WriteAllText(Path.Combine(other, InstallerPaths.InstallerName), "x");
        var swap = Path.Combine(dir.Root, "swap");
        if (!Links.TryMakeJunction(swap, other))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        using SafeFileHandle? folder = InstallerPaths.OpenFolder(held);
        Assert.IsNotNull(folder);
        // The name the helper checked leads, through the junction, to a file in another folder.
        using var file = File.Open(Path.Combine(swap, InstallerPaths.InstallerName), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.IsFalse(InstallerPaths.InFolder(folder, InstallerPaths.FinalPathOf(file.SafeFileHandle)!), "a swapped installer is refused");
    }
}
