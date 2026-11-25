using System.Diagnostics;
using Bwc.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The shared "is this BWC process running from the install folder" rule
// (docs/failure-modes/update-verifier.md, section O).
[TestClass]
public sealed class RunningImageRuleTests
{
    private const string InstallDir = @"C:\BWC";

    [TestMethod]
    public void Image_Inside_Install_Dir_Is_Running()
    {
        Assert.IsTrue(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWC\BwcRegedit.exe", InstallDir));
    }

    // HC5: every tool of the catalog is judged by the same rule, so a new tool needs no new rule.
    [TestMethod]
    public void Image_Rule_Covers_Every_Catalog_Tool()
    {
        Assert.IsTrue(ToolCatalog.Tools.Any(tool => tool.ExeName == "BwcGpedit.exe"), "the catalog lists the Local Group Policy Editor");
        foreach (var tool in ToolCatalog.Tools)
        {
            Assert.IsTrue(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWC\" + tool.ExeName, InstallDir), tool.ExeName + " in the folder");
            Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWC2\" + tool.ExeName, InstallDir), tool.ExeName + " in a sibling folder");
            Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"D:\Tools\" + tool.ExeName, InstallDir), tool.ExeName + " elsewhere");
        }
    }

    [TestMethod]
    public void Sibling_Folder_Prefix_Is_Not_Running()
    {
        Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWC2\BwcRegedit.exe", InstallDir));
        Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWCOld\BwcRegedit.exe", InstallDir));
    }

    [TestMethod]
    public void Install_Dir_Compare_Ignores_Case()
    {
        Assert.IsTrue(RunningImageRule.Decide(ImageStatus.Readable, @"c:\bwc\BwcRegedit.exe", InstallDir));
    }

    [TestMethod]
    public void Install_Dir_Trailing_Separator_Ignored()
    {
        Assert.IsTrue(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWC\BwcRegedit.exe", @"C:\BWC\"));
        Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"C:\BWC2\BwcRegedit.exe", @"C:\BWC\"));
    }

    [TestMethod]
    public void Unreadable_Image_Counts_As_Running()
    {
        Assert.IsTrue(RunningImageRule.Decide(ImageStatus.Unreadable, null, InstallDir), "fail closed: an image that cannot be read may be in the folder");
    }

    [TestMethod]
    public void Image_Outside_Install_Dir_Is_Not_Running()
    {
        Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"D:\Tools\BwcRegedit.exe", InstallDir));
    }

    [TestMethod]
    public void Exited_Status_Is_Not_Running()
    {
        Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Exited, null, InstallDir));
    }

    [TestMethod]
    public void Own_Process_Is_Found_Under_Its_Folder()
    {
        var image = Environment.ProcessPath!;
        var folder = Path.GetDirectoryName(image)!;

        Assert.IsTrue(RunningImageRule.IsRunningFrom(Environment.ProcessId, folder));
        Assert.IsFalse(RunningImageRule.IsRunningFrom(Environment.ProcessId, Path.Combine(folder, "not-this-folder")));
    }

    [TestMethod]
    public void Exited_Process_Is_Not_Running()
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start cmd");
        process.WaitForExit();

        Assert.IsFalse(RunningImageRule.IsRunningFrom(process.Id, Path.GetDirectoryName(Environment.ProcessPath!)!));
    }
}
