using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

/// <summary>The pure decisions behind the elevated data folder rules (failure-modes/core.md, CORE-1).</summary>
[TestClass]
public sealed class DataPathPolicyTests
{
    private const string Configured = @"C:\sandbox\data";
    private const string UserDefault = @"C:\Users\someone\AppData\Local\BetterWindowsConfig";

    [TestMethod]
    public void ShipElevatedIgnoresConfiguredRoot()
    {
        Assert.AreEqual(UserDefault, DataPathPolicy.ChooseRoot(elevated: true, e2eFlavor: false, Configured, () => UserDefault));
    }

    [TestMethod]
    public void ShipUnelevatedHonorsConfiguredRoot()
    {
        Assert.AreEqual(Configured, DataPathPolicy.ChooseRoot(elevated: false, e2eFlavor: false, Configured, () => UserDefault));
    }

    [TestMethod]
    public void E2EElevatedHonorsConfiguredRoot()
    {
        Assert.AreEqual(Configured, DataPathPolicy.ChooseRoot(elevated: true, e2eFlavor: true, Configured, () => UserDefault));
    }

    [TestMethod]
    public void E2EUnelevatedHonorsConfiguredRoot()
    {
        Assert.AreEqual(Configured, DataPathPolicy.ChooseRoot(elevated: false, e2eFlavor: true, Configured, () => UserDefault));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void NoConfiguredRootUsesDefaultInEveryCase(bool elevated, bool e2e)
    {
        Assert.AreEqual(UserDefault, DataPathPolicy.ChooseRoot(elevated, e2e, null, () => UserDefault));
    }

    [TestMethod]
    public void UnelevatedAllowsEverythingEvenWithLinkSignals()
    {
        Assert.IsTrue(DataPathPolicy.AllowFile(elevated: false, reparsePoint: true, finalPathMatches: false, linkCount: 5));
    }

    [TestMethod]
    public void ElevatedAllowsPlainFileWithMatchingFinalPath()
    {
        Assert.IsTrue(DataPathPolicy.AllowFile(elevated: true, reparsePoint: false, finalPathMatches: true, linkCount: 1));
    }

    [TestMethod]
    public void ElevatedRefusesReparsePoint()
    {
        Assert.IsFalse(DataPathPolicy.AllowFile(elevated: true, reparsePoint: true, finalPathMatches: true, linkCount: 1));
    }

    [TestMethod]
    public void ElevatedRefusesFinalPathMismatch()
    {
        Assert.IsFalse(DataPathPolicy.AllowFile(elevated: true, reparsePoint: false, finalPathMatches: false, linkCount: 1));
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(2u)]
    [DataRow(9u)]
    public void ElevatedRefusesUnexpectedLinkCount(uint linkCount)
    {
        Assert.IsFalse(DataPathPolicy.AllowFile(elevated: true, reparsePoint: false, finalPathMatches: true, linkCount));
    }

    [TestMethod]
    public void UnknownElevationIsTreatedAsElevated()
    {
        Assert.IsTrue(ElevationProbe.Classify(tokenOpened: false, elevated: null, highIntegrity: null));
        Assert.IsTrue(ElevationProbe.Classify(tokenOpened: true, elevated: null, highIntegrity: false));
        Assert.IsTrue(ElevationProbe.Classify(tokenOpened: true, elevated: false, highIntegrity: null));
    }

    [TestMethod]
    public void ElevatedOrHighIntegrityIsElevated()
    {
        Assert.IsTrue(ElevationProbe.Classify(tokenOpened: true, elevated: true, highIntegrity: false));
        Assert.IsTrue(ElevationProbe.Classify(tokenOpened: true, elevated: false, highIntegrity: true));
    }

    [TestMethod]
    public void KnownMediumIntegrityIsNotElevated()
    {
        Assert.IsFalse(ElevationProbe.Classify(tokenOpened: true, elevated: false, highIntegrity: false));
    }
}
