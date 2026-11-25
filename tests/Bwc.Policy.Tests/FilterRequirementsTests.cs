using Bwc.Policy.Filter;
using static Bwc.Policy.Tests.FilterTestSupport;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class FilterRequirementsTests
{
    private const string Sup1 = "T.Filter:SUP1";
    private const string Sup2 = "T.Filter:SUP2";

    private static FilterOptions Requirements(PlatformMatchMode mode, params string[] keys) =>
        Options(requirements: new RequirementsFilter(true, keys, mode));

    [TestMethod]
    public void PlatformList_HasOneEntryPerDefinitionInSourceOrder()
    {
        var platforms = PlatformList.From(Set);
        CollectionAssert.AreEqual(new[] { Sup1, Sup2 }, platforms.Select(p => p.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "At least Windows 10", "At least Windows 11" }, platforms.Select(p => p.DisplayName).ToArray());
    }

    [TestMethod]
    public void PlatformList_SelectAllAndClearAll()
    {
        CollectionAssert.AreEqual(new[] { Sup1, Sup2 }, PlatformList.All(PlatformList.From(Set)).ToArray());
        Assert.AreEqual(Sup1, PlatformList.KeyOf(P("P1").SupportedOn!));
    }

    [TestMethod]
    public void PlatformList_PruneDropsKeysTheSetNoLongerDefines()
    {
        var pruned = PlatformList.Prune([Sup1, "T.Gone:SUP", "t.filter:sup2"], Set);
        CollectionAssert.AreEqual(new[] { Sup1, "t.filter:sup2" }, pruned.ToArray());
    }

    [TestMethod]
    public void Any_AMatchingPlatformIsEnough()
    {
        Assert.IsTrue(Matches(Requirements(PlatformMatchMode.Any, Sup1), Subject("P1")));
        Assert.IsFalse(Matches(Requirements(PlatformMatchMode.Any, Sup1), Subject("P2")));
        Assert.IsTrue(Matches(Requirements(PlatformMatchMode.Any, Sup1, Sup2), Subject("P2")));
    }

    [TestMethod]
    public void All_NeedsEverySelectedPlatformSoTwoDifferentOnesMatchNothing()
    {
        Assert.IsTrue(Matches(Requirements(PlatformMatchMode.All, Sup1), Subject("P1")));
        Assert.IsFalse(Matches(Requirements(PlatformMatchMode.All, Sup1, Sup2), Subject("P1")));
        Assert.IsFalse(Matches(Requirements(PlatformMatchMode.All, Sup1, Sup2), Subject("P2")));
    }

    [TestMethod]
    public void ThePlatformKeyIsComparedIgnoringCase()
    {
        Assert.IsTrue(Matches(Requirements(PlatformMatchMode.Any, "t.FILTER:sup1"), Subject("P1")));
    }

    [TestMethod]
    public void APolicyWithNoSupportedOnFailsAnEnabledRequirementsFilter()
    {
        Assert.IsNull(P("P3").SupportedOn);
        Assert.IsFalse(Matches(Requirements(PlatformMatchMode.Any, Sup1, Sup2), Subject("P3")));
        Assert.IsTrue(Matches(FilterOptions.None, Subject("P3")));
    }

    [TestMethod]
    public void AStaleSelectedPlatformMatchesNothing()
    {
        Assert.IsFalse(Matches(Requirements(PlatformMatchMode.Any, "T.Gone:SUP"), Subject("P1")));
    }

    [TestMethod]
    public void NoPlatformSelectedIsRefusedWhenEnabled()
    {
        var problems = Requirements(PlatformMatchMode.Any).Validate();
        CollectionAssert.AreEqual(new[] { FilterProblem.PlatformsMissing }, problems.ToArray());
        Assert.AreEqual(0, Options(requirements: new RequirementsFilter(false, [], PlatformMatchMode.All)).Validate().Count);
    }

    [TestMethod]
    public void DisabledRequirementsPassEverythingWhateverTheyHold()
    {
        Assert.IsTrue(Matches(Options(requirements: new RequirementsFilter(false, ["T.Gone:SUP"], PlatformMatchMode.All)), Subject("P3")));
    }
}
