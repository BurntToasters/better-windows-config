using Bwc.Gpo.Store;
using Bwc.Policy.Filter;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.FilterTestSupport;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class FilterTests
{
    [TestMethod]
    public void Options_NoneValidatesAndPassesEverything()
    {
        Assert.AreEqual(0, FilterOptions.None.Validate().Count);
        var filter = Create(FilterOptions.None);
        Assert.IsTrue(Set.Policies.All(p => filter.Matches(new FilterSubject(p, null, null))));
    }

    [TestMethod]
    public void Validate_KeywordOnWithNoWordIsRefused()
    {
        foreach (var text in new[] { string.Empty, "   ", "\t\r\n", null! })
        {
            var problems = Options(keyword: Words(text)).Validate();
            CollectionAssert.AreEqual(new[] { FilterProblem.KeywordTextMissing }, problems.ToArray(), "text: " + (text ?? "null"));
        }
    }

    [TestMethod]
    public void Validate_KeywordOffIgnoresItsFields()
    {
        var off = new KeywordFilter(false, string.Empty, KeywordMatchMode.Exact, false, false, false);
        Assert.AreEqual(0, Options(keyword: off).Validate().Count);
    }

    [TestMethod]
    public void Validate_KeywordOnWithNoWithinBoxIsRefused()
    {
        var problems = Options(keyword: Words("word", title: false, help: false, comment: false)).Validate();
        CollectionAssert.AreEqual(new[] { FilterProblem.KeywordWithinMissing }, problems.ToArray());
    }

    [TestMethod]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var options = Options(keyword: Words(" ", title: false, help: false, comment: false), requirements: new RequirementsFilter(true, [], PlatformMatchMode.Any));
        CollectionAssert.AreEqual(
            new[] { FilterProblem.KeywordTextMissing, FilterProblem.KeywordWithinMissing, FilterProblem.PlatformsMissing },
            options.Validate().ToArray());
    }

    [TestMethod]
    public void Validate_ARidiculouslyLongKeywordIsRefused()
    {
        var options = Options(keyword: Words(new string('a', FilterRules.MaxKeywordLength + 1)));
        CollectionAssert.AreEqual(new[] { FilterProblem.KeywordTooLong }, options.Validate().ToArray());
        Assert.AreEqual(0, Options(keyword: Words(new string('a', FilterRules.MaxKeywordLength))).Validate().Count);
    }

    [TestMethod]
    public void TryCreate_RefusesInvalidOptionsAndReturnsTheProblems()
    {
        var created = PolicyFilter.TryCreate(Options(keyword: Words(" ")), out var filter, out var problems);
        Assert.IsFalse(created);
        Assert.IsNull(filter);
        Assert.AreEqual(1, problems.Count);
    }

    [TestMethod]
    public void Managed_FollowsTheKeyRoots()
    {
        var yes = Create(Options(managed: FilterTriState.Yes));
        var no = Create(Options(managed: FilterTriState.No));
        Assert.IsTrue(yes.Matches(Subject("P1")), "Software\\Policies");
        Assert.IsTrue(yes.Matches(Subject("P3")), "CurrentVersion\\Policies");
        Assert.IsFalse(yes.Matches(Subject("P2")), "a vendor key is not managed");
        Assert.IsFalse(yes.Matches(Subject("P4")), "Software\\PoliciesX is not below Software\\Policies");
        Assert.IsFalse(no.Matches(Subject("P1")));
        Assert.IsTrue(no.Matches(Subject("P2")));
        Assert.IsTrue(no.Matches(Subject("P4")));
    }

    [TestMethod]
    public void Managed_KeyRulesIgnoreCaseAndOuterBackslashes()
    {
        Assert.IsTrue(FilterRules.IsManagedKey(@"software\POLICIES\x"));
        Assert.IsTrue(FilterRules.IsManagedKey(@"\Software\Policies\"));
        Assert.IsTrue(FilterRules.IsManagedKey(@"Software\Policies"));
        Assert.IsFalse(FilterRules.IsManagedKey(@"Software\\Policies\x"));
        Assert.IsFalse(FilterRules.IsManagedKey(@"Software\Policie"));
        Assert.IsFalse(FilterRules.IsManagedKey(string.Empty));
        Assert.IsFalse(FilterRules.IsManagedKey(null));
    }

    [TestMethod]
    public void Configured_YesIsEnabledOrDisabledNoIsNotConfigured()
    {
        var yes = Create(Options(configured: FilterTriState.Yes));
        var no = Create(Options(configured: FilterTriState.No));
        Assert.IsTrue(yes.Matches(Subject("P1", PolicyState.Enabled)));
        Assert.IsTrue(yes.Matches(Subject("P1", PolicyState.Disabled)));
        Assert.IsFalse(yes.Matches(Subject("P1", PolicyState.NotConfigured)));
        Assert.IsFalse(no.Matches(Subject("P1", PolicyState.Enabled)));
        Assert.IsFalse(no.Matches(Subject("P1", PolicyState.Disabled)));
        Assert.IsTrue(no.Matches(Subject("P1", PolicyState.NotConfigured)));
    }

    [TestMethod]
    public void Configured_AnUnreadableStateMatchesOnlyAny()
    {
        var unreadable = Subject("P1", state: null);
        Assert.IsFalse(Matches(Options(configured: FilterTriState.Yes), unreadable));
        Assert.IsFalse(Matches(Options(configured: FilterTriState.No), unreadable));
        Assert.IsTrue(Matches(Options(configured: FilterTriState.Any), unreadable));
        Assert.IsTrue(Matches(FilterOptions.None, unreadable));
    }

    [TestMethod]
    public void Commented_BlankAndNullCommentsAreNoComment()
    {
        var yes = Create(Options(commented: FilterTriState.Yes));
        var no = Create(Options(commented: FilterTriState.No));
        foreach (var blank in new[] { null, string.Empty, "  ", "\r\n\t" })
        {
            Assert.IsFalse(yes.Matches(Subject("P1", comment: blank)), "blank: '" + blank + "'");
            Assert.IsTrue(no.Matches(Subject("P1", comment: blank)));
        }

        Assert.IsTrue(yes.Matches(Subject("P1", comment: "why")));
        Assert.IsFalse(no.Matches(Subject("P1", comment: "why")));
    }

    [TestMethod]
    public void Parts_CombineWithAnd()
    {
        var options = Options(FilterTriState.Yes, FilterTriState.Yes, FilterTriState.Yes, Words("firewall", help: false, comment: false));
        var filter = Create(options);
        Assert.IsTrue(filter.Matches(Subject("P1", PolicyState.Enabled, "c")));
        Assert.IsFalse(filter.Matches(Subject("P1", PolicyState.NotConfigured, "c")));
        Assert.IsFalse(filter.Matches(Subject("P1", PolicyState.Enabled, null)));
        Assert.IsFalse(filter.Matches(Subject("P4", PolicyState.Enabled, "c")));
    }

    [TestMethod]
    public void Apply_ReturnsTheMatchesInOrderAndCountsAll()
    {
        var subjects = Set.Policies.Select(p => new FilterSubject(p, State(PolicyState.NotConfigured), null)).ToArray();
        var result = Create(Options(managed: FilterTriState.Yes)).Apply(subjects);
        CollectionAssert.AreEqual(new[] { "P1", "P3" }, result.Shown.Select(s => s.Policy.Name).ToArray());
        Assert.AreEqual(4, result.Total);
    }

    [TestMethod]
    public void Count_ACategoryWithNoMatchGivesZeroOfTotal()
    {
        var inCategory = Set.FindCategory("T.Filter", "Root")!.Policies.Select(p => new FilterSubject(p, State(PolicyState.NotConfigured), null)).ToArray();
        var count = Create(Options(configured: FilterTriState.Yes)).Count(inCategory);
        Assert.AreEqual(new FilterCount(0, inCategory.Length), count);
        Assert.IsTrue(FilterRules.EmptyCategoryStaysVisible);
    }

    [TestMethod]
    public void Apply_CancellationThrows()
    {
        var subjects = Enumerable.Repeat(Subject("P1"), 1000);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Create(FilterOptions.None).Apply(subjects, source.Token));
    }

    [TestMethod]
    public void Apply_ChecksTheTokenAgainWhileItRuns()
    {
        using var source = new CancellationTokenSource();
        IEnumerable<FilterSubject> Stream()
        {
            for (var i = 0; i < 2000; i++)
            {
                if (i == 300)
                {
                    source.Cancel();
                }

                yield return Subject("P1");
            }
        }

        Assert.ThrowsExactly<OperationCanceledException>(() => Create(FilterOptions.None).Apply(Stream(), source.Token));
    }

    [TestMethod]
    public void TryCreate_CopiesThePlatformListSoLaterEditsDoNotChangeTheFilter()
    {
        var keys = new List<string> { "T.Filter:SUP1" };
        var filter = Create(Options(requirements: new RequirementsFilter(true, keys, PlatformMatchMode.Any)));
        keys.Clear();
        keys.Add("T.Filter:SUP2");
        Assert.IsTrue(filter.Matches(Subject("P1")));
        Assert.IsFalse(filter.Matches(Subject("P2")));
    }

    [TestMethod]
    public void Matches_ANullPolicyIsAnArgumentError()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Create(FilterOptions.None).Matches(default));
    }

    [TestMethod]
    public void AllSettings_ListsEachPolicyOncePerSideInSourceOrder()
    {
        CollectionAssert.AreEqual(new[] { "P1", "P3", "P4" }, AllSettings.List(Set, GpoSide.Machine).Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "P2", "P3" }, AllSettings.List(Set, GpoSide.User).Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void AllSettings_IncludesAPolicyWithNoCategory()
    {
        Assert.IsNull(P("P3").Category);
        Assert.IsTrue(AllSettings.List(Set, GpoSide.Machine).Any(p => p.Name == "P3"));
    }

    [TestMethod]
    public void AllSettings_CancellationThrows()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => AllSettings.List(Set, GpoSide.Machine, source.Token));
    }

    [TestMethod]
    public void AllSettings_OfAnEmptySetIsEmpty()
    {
        Assert.AreEqual(0, AllSettings.List(Bwc.Policy.Admx.TemplateSet.Empty, GpoSide.User).Count);
    }
}
