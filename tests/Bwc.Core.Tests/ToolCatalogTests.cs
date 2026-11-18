using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

/// <summary>
/// Pins the tool registry and the AppUserModelIDs the exe heads and the installer shortcuts share
/// (docs/failure-modes/gpedit-scaffold.md: Hub row order, Missing AUMID).
/// </summary>
[TestClass]
public sealed class ToolCatalogTests
{
    [TestMethod]
    public void Tools_ListRegeditThenGpeditInCatalogOrder()
    {
        string[] ids = ToolCatalog.Tools.Select(tool => tool.Id).ToArray();

        CollectionAssert.AreEqual(new[] { "regedit", "gpedit" }, ids);
    }

    [TestMethod]
    public void Gpedit_HasTheFixedIdentity()
    {
        ToolCatalog.Tool tool = ToolCatalog.Tools.Single(entry => entry.Id == "gpedit");

        Assert.AreEqual("Local Group Policy Editor", tool.DisplayName);
        Assert.AreEqual("BwcGpedit.exe", tool.ExeName);
        Assert.AreEqual("Browse and edit local group policy.", tool.Description);
        Assert.AreEqual("BetterWindowsConfig.Gpedit", AppIdentity.GpeditAppUserModelId);
    }

    [TestMethod]
    public void Tools_HaveDistinctIdsExeNamesAndAppUserModelIds()
    {
        Assert.AreEqual(ToolCatalog.Tools.Count, ToolCatalog.Tools.Select(tool => tool.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(
            ToolCatalog.Tools.Count,
            ToolCatalog.Tools.Select(tool => tool.ExeName).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        string[] aumids =
        {
            AppIdentity.HubAppUserModelId,
            AppIdentity.RegeditAppUserModelId,
            AppIdentity.GpeditAppUserModelId,
        };
        Assert.AreEqual(aumids.Length, aumids.Distinct(StringComparer.Ordinal).Count());
    }
}
