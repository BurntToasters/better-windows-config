using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// --set-aumid (docs/failure-modes/update-elevation.md, section SA; installer.md M45). The argument shape, the path
// rules, and the file rules are pure and are proved here. The COM write is proved by the installer E2E (the user
// sandbox reads both shortcuts back), because a test that fakes COM would not show that a real .lnk carries the id.
[TestClass]
public sealed class SetAumidTests
{
    private const string Programs = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
    private const string HubLnk = Programs + @"\Better Windows Config.lnk";
    private const string RegeditLnk = Programs + @"\Registry Editor (BWC).lnk";
    private const string HubId = "BetterWindowsConfig.Hub";
    private const string RegeditId = "BetterWindowsConfig.Regedit";

    // SA1, SA3
    [TestMethod]
    [DataRow(new[] { "--set-aumid", HubLnk, HubId }, true)]
    [DataRow(new[] { "--set-aumid", RegeditLnk, RegeditId }, true)]
    [DataRow(new[] { "--set-aumid" }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, HubId, "extra" }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, "--x" }, false)]
    [DataRow(new[] { "--set-aumid", "--x", HubId }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, "BetterWindowsConfig.Other" }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, "betterwindowsconfig.hub" }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, "" }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, HubId + " " }, false)]
    [DataRow(new[] { "--set-aumid", HubLnk, HubId, "--set-aumid", HubLnk }, false)]
    public void SetAumid_Arguments_Parse(string[] args, bool accepted)
    {
        var parsed = UpdateArguments.Parse(args);

        Assert.AreEqual(accepted ? UpdateMode.SetAumid : UpdateMode.Invalid, parsed.Mode, parsed.Error);
        if (accepted)
        {
            Assert.AreEqual(args[1], parsed.Single("--set-aumid"));
            Assert.AreEqual(args[2], parsed.Single("--app-id"));
        }
    }

    // SA2, SA7
    [TestMethod]
    [DataRow(@"relative\Better Windows Config.lnk", false)]
    [DataRow(@"Better Windows Config.lnk", false)]
    [DataRow(@"\\server\share\Better Windows Config.lnk", false)]
    [DataRow(@"\\?\C:\ProgramData\Better Windows Config.lnk", false)]
    [DataRow(Programs + @"\..\Programs\Better Windows Config.lnk", false)]
    [DataRow(Programs + @"\.\Better Windows Config.lnk", false)]
    [DataRow(Programs + @"\\Better Windows Config.lnk", false)]
    [DataRow(@"C:/ProgramData/Better Windows Config.lnk", false)]
    [DataRow(Programs + @"\Better Windows Config.lnk ", false)]
    [DataRow(Programs + @"\Better Windows Config.lnk.", false)]
    [DataRow(Programs + "\\\"Better Windows Config.lnk", false)]
    [DataRow(Programs + "\\Better\u0001Windows Config.lnk", false)]
    [DataRow(Programs + @"\Better Windows Config.lnk:stream", false)]
    [DataRow(Programs + @"\Better Windows Config.exe", false)]
    [DataRow(Programs + @"\Better Windows Config", false)]
    [DataRow(HubLnk, true)]
    [DataRow(Programs + @"\Better Windows Config.LNK", true)]
    public void SetAumid_Path_Shape(string shortcut, bool accepted)
    {
        var problem = ShortcutAumidRules.ArgumentProblem(shortcut, HubId);

        Assert.AreEqual(accepted, problem is null, problem);
        Assert.AreEqual(accepted ? UpdateMode.SetAumid : UpdateMode.Invalid, UpdateArguments.Parse(["--set-aumid", shortcut, HubId]).Mode);
    }

    // SA4
    [TestMethod]
    [DataRow(HubLnk, HubId, true)]
    [DataRow(RegeditLnk, RegeditId, true)]
    [DataRow(Programs + @"\better windows config.LNK", HubId, true)]
    [DataRow(HubLnk, RegeditId, false)]
    [DataRow(RegeditLnk, HubId, false)]
    [DataRow(Programs + @"\Notepad.lnk", HubId, false)]
    [DataRow(Programs + @"\Better Windows Config (2).lnk", HubId, false)]
    [DataRow(Programs + @"\Some Other Tool.lnk", RegeditId, false)]
    public void SetAumid_Name_And_Id_Rule(string shortcut, string appId, bool accepted)
    {
        var problem = ShortcutAumidRules.ScopeProblem(shortcut, appId, folder => PathRules.SameDirectory(folder, Programs));

        Assert.AreEqual(accepted, problem is null, problem);
    }

    // SA5
    [TestMethod]
    public void SetAumid_Ship_Scope_Is_Common_Programs()
    {
        var scope = ShortcutAumidRules.ShipScope(Programs);

        Assert.IsTrue(scope(Programs));
        Assert.IsTrue(scope(Programs + @"\"), "a trailing separator is the same folder");
        Assert.IsTrue(scope(Programs.ToUpperInvariant()), "case is ignored");
        Assert.IsFalse(scope(Programs + @"\BWC-E2E-run"), "no subfolder");
        Assert.IsFalse(scope(Path.GetDirectoryName(Programs)!), "no parent");
        Assert.IsFalse(scope(@"C:\Users\someone\AppData\Roaming\Microsoft\Windows\Start Menu\Programs"), "never the per-user Start menu");
        Assert.IsFalse(scope(Path.GetTempPath()), "never the temp folder in a ship build");

        foreach (var folder in new[] { @"D:\ProgramData\Microsoft\Windows\Start Menu\Programs", string.Empty })
        {
            Assert.IsFalse(scope(folder), folder);
        }

        var problem = ShortcutAumidRules.ScopeProblem(@"C:\Users\someone\Better Windows Config.lnk", HubId, scope);
        Assert.IsNotNull(problem, "a shortcut outside the all-users folder is refused");
    }

    // SA6
    [TestMethod]
    public void SetAumid_Files_Refused()
    {
        using var dir = new TempDir();
        var folder = dir.Sub("programs");
        var good = Path.Combine(folder, "Better Windows Config.lnk");
        File.WriteAllText(good, "not a real shortcut, only a plain file");
        Assert.IsNull(ShortcutAumidRules.FileProblem(good), "a plain file in a plain folder passes the file rules");

        Assert.IsNotNull(ShortcutAumidRules.FileProblem(Path.Combine(folder, "Registry Editor (BWC).lnk")), "a missing file");

        var asFolder = Path.Combine(folder, "Registry Editor (BWC).lnk");
        Directory.CreateDirectory(asFolder);
        Assert.IsNotNull(ShortcutAumidRules.FileProblem(asFolder), "a folder named like the shortcut");

        var target = dir.Sub("elsewhere");
        var linkedFolder = Path.Combine(dir.Root, "linked-programs");
        if (!Links.TryMakeJunction(linkedFolder, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        File.WriteAllText(Path.Combine(target, "Better Windows Config.lnk"), "x");
        Assert.IsNotNull(
            ShortcutAumidRules.FileProblem(Path.Combine(linkedFolder, "Better Windows Config.lnk")),
            "a shortcut reached through a junction folder");

        var fileLink = Path.Combine(folder, "linked.lnk");
        try
        {
            File.CreateSymbolicLink(fileLink, good);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        Assert.IsNotNull(ShortcutAumidRules.FileProblem(fileLink), "a symbolic link to a shortcut");
    }

    // SA11
    [TestMethod]
    public void SetAumid_Mode_Is_Neither_Gated_Nor_Starts_Anything()
    {
        Assert.IsFalse(ElevationPolicy.RefusesElevated(UpdateMode.SetAumid), "it is meant to run elevated, inside the installer");
        StringAssert.Contains(UpdateArguments.UsageText, "--set-aumid");
        Assert.AreEqual(UpdateMode.Invalid, UpdateArguments.Parse(["--set-aumid", "--install-dir", @"C:\x"]).Mode);
    }
}
