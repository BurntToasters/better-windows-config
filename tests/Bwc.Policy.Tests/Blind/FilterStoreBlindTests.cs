using System.Globalization;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Filter;
using Bwc.Policy.Model;
using Bwc.Policy.Store;
using Bwc.Update;
using static Bwc.Policy.Tests.Blind.BlindKit;

namespace Bwc.Policy.Tests.Blind;

/// <summary>
/// Blind tests of the filter (docs/failure-modes/policy-filter.md). The comment on each test names the case.
/// </summary>
[TestClass]
public sealed class FilterBlindTests
{
    private const string FilterNs = "Blind.F";

    private sealed record Spec(
        string Name,
        string Key = @"Software\Policies\Blind",
        string Title = "Title",
        string Help = "Help",
        string? Sup = "SUP",
        string Cls = "Machine",
        string? Cat = "Root");

    private static TemplateSet Build(params Spec[] specs)
    {
        using var t = new TempTemplates();
        var policies = string.Concat(specs.Select(s =>
            P(s.Name, key: s.Key, cls: s.Cls, sup: s.Sup, cat: s.Cat, attrs: $"explainText=\"$(string.H_{s.Name})\"")));
        var strings = string.Concat(specs.Select(s =>
            $"<string id=\"{s.Name}\">{SecurityElement.Escape(s.Title)}</string><string id=\"H_{s.Name}\">{SecurityElement.Escape(s.Help)}</string>"));
        var body =
            "<supportedOn><definitions><definition name=\"SUP\" displayName=\"$(string.SUP)\" />" +
            "<definition name=\"SUP2\" displayName=\"$(string.SUP2)\" /></definitions></supportedOn>\n" +
            "<categories><category name=\"Root\" displayName=\"$(string.Root)\" /></categories>\n" +
            $"<policies>{policies}</policies>";
        t.Write("f.admx", AdmxText(FilterNs, "t", body));
        t.Write(Path.Combine("en-US", "f.adml"), AdmlText(Strings("Root", "SUP", "SUP2") + strings));
        var set = t.Load();
        Assert.IsTrue(set.Failures.All(f => f.Code == TemplateFailureCode.MissingCategory), FailureAssert.Describe(set));
        return set;
    }

    private static PolicyStateSnapshot Snap(PolicyDefinition policy, PolicyState state)
    {
        var doc = PolDocument.Create();
        if (state != PolicyState.NotConfigured)
        {
            doc.SetValue(policy.Key, "V", PolValueType.DWord, PolData.FromDWord(state == PolicyState.Enabled ? 1u : 0u));
        }

        var snapshot = PolicyStateReader.Read(policy, doc);
        Assert.AreEqual(state, snapshot.State);
        return snapshot;
    }

    private static FilterSubject Subject(PolicyDefinition p, PolicyState? state = PolicyState.NotConfigured, string? comment = null) =>
        new(p, state is null ? null : Snap(p, state.Value), comment);

    private static FilterOptions Kw(string text, KeywordMatchMode mode = KeywordMatchMode.Any, bool title = true, bool help = true, bool comment = true) =>
        FilterOptions.None with { Keyword = new KeywordFilter(true, text, mode, title, help, comment) };

    private static PolicyFilter Make(FilterOptions options)
    {
        Assert.IsTrue(PolicyFilter.TryCreate(options, out var filter, out var problems), string.Join(",", problems));
        Assert.IsNotNull(filter);
        return filter;
    }

    private static bool Hit(FilterOptions options, string title = "Title", string help = "Help", string? comment = null)
    {
        var set = Build(new Spec("P", Title: title, Help: help));
        return Make(options).Matches(Subject(set.Policies[0], PolicyState.NotConfigured, comment));
    }

    // ---------------------------------------------------------------- A. Keyword filter

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\t \r\n")]
    public void A1_A3_EnabledKeywordWithBlankText_IsKeywordTextMissing(string text)
    {
        var options = Kw(text);
        CollectionAssert.Contains(options.Validate().ToList(), FilterProblem.KeywordTextMissing);
        Assert.IsFalse(PolicyFilter.TryCreate(options, out var filter, out var problems));
        Assert.IsNull(filter);
        CollectionAssert.Contains(problems.ToList(), FilterProblem.KeywordTextMissing);
    }

    [TestMethod]
    public void A1_DisabledKeywordWithBlankText_IsValid()
    {
        var options = FilterOptions.None with { Keyword = new KeywordFilter(false, "  ", KeywordMatchMode.All, false, false, false) };
        Assert.AreEqual(0, options.Validate().Count);
        Assert.IsTrue(PolicyFilter.TryCreate(options, out var filter, out var problems));
        Assert.IsNotNull(filter);
        Assert.AreEqual(0, problems.Count);
    }

    [TestMethod]
    public void A2_NoWithinBoxTicked_IsKeywordWithinMissing()
    {
        var options = Kw("word", title: false, help: false, comment: false);
        CollectionAssert.Contains(options.Validate().ToList(), FilterProblem.KeywordWithinMissing);
        Assert.IsFalse(PolicyFilter.TryCreate(options, out _, out _));
    }

    [TestMethod]
    public void E1_EveryProblemIsListedTogether()
    {
        var options = Kw(" ", title: false, help: false, comment: false) with
        {
            Requirements = new RequirementsFilter(true, [], PlatformMatchMode.Any),
        };
        var problems = options.Validate().ToList();
        CollectionAssert.Contains(problems, FilterProblem.KeywordTextMissing);
        CollectionAssert.Contains(problems, FilterProblem.KeywordWithinMissing);
        CollectionAssert.Contains(problems, FilterProblem.PlatformsMissing);
        Assert.AreEqual(3, problems.Count);
        Assert.IsFalse(PolicyFilter.TryCreate(options, out var filter, out var fromTry));
        Assert.IsNull(filter);
        CollectionAssert.AreEquivalent(problems, fromTry.ToList());
    }

    [TestMethod]
    public void A4_ACommentOnlyHit_DoesNotMatchWhenCommentIsOff_AndMatchesWhenOn()
    {
        Assert.IsFalse(Hit(Kw("zebra", comment: false), comment: "zebra crossing"));
        Assert.IsTrue(Hit(Kw("zebra", comment: true), comment: "zebra crossing"));
    }

    [TestMethod]
    public void A4_TitleOnlyAndHelpOnlyHits_FollowTheirOwnBoxes()
    {
        Assert.IsTrue(Hit(Kw("zebra", title: true, help: false, comment: false), title: "zebra"));
        Assert.IsFalse(Hit(Kw("zebra", title: false, help: true, comment: false), title: "zebra", help: "none"));
        Assert.IsTrue(Hit(Kw("zebra", title: false, help: true, comment: false), title: "none", help: "a zebra"));
        Assert.IsFalse(Hit(Kw("zebra", title: true, help: false, comment: false), title: "none", help: "a zebra"));
    }

    [TestMethod]
    public void A5_ANullOrEmptyComment_NeverMatchesAWord_AndNeverCrashes()
    {
        Assert.IsFalse(Hit(Kw("zebra", title: false, help: false, comment: true), comment: null));
        Assert.IsFalse(Hit(Kw("zebra", title: false, help: false, comment: true), comment: ""));
    }

    [TestMethod]
    public void A6_NoHelpText_StillSearchesTheTitleAndComment_AndAnEmptyTextMatchesNothing()
    {
        Assert.IsTrue(Hit(Kw("zebra"), title: "zebra", help: ""));
        Assert.IsTrue(Hit(Kw("zebra"), title: "none", help: "", comment: "zebra"));
        Assert.IsFalse(Hit(Kw("zebra", title: false, help: true, comment: false), title: "zebra", help: ""));
    }

    [TestMethod]
    public void A7_CaseInsensitive_AndTheSameUnderTurkishCulture()
    {
        Assert.IsTrue(Hit(Kw("FIREWALL"), title: "Windows firewall"));
        Assert.IsTrue(Hit(Kw("firewall"), title: "Windows FIREWALL"));
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.IsTrue(Hit(Kw("TITLE INFO"), title: "title info"));
            Assert.IsTrue(Hit(Kw("info"), title: "INFO"));
            Assert.IsTrue(Hit(Kw("INFO"), title: "info"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [TestMethod]
    public void A8_WordsAreSplitOnWhiteSpace()
    {
        Assert.IsTrue(Hit(Kw("alpha\t\n  beta", KeywordMatchMode.All), title: "beta alpha"));
        Assert.IsTrue(Hit(Kw("   zzz   alpha", KeywordMatchMode.Any), title: "alpha"));
        Assert.IsFalse(Hit(Kw("alpha  gamma", KeywordMatchMode.All), title: "alpha beta"));
    }

    [TestMethod]
    public void A9_AWordInsideALongerWord_Matches()
    {
        Assert.IsTrue(Hit(Kw("work"), title: "network settings"));
    }

    [TestMethod]
    public void A10_Any_OneWordIsEnough()
    {
        Assert.IsTrue(Hit(Kw("nothere beta"), title: "alpha beta"));
        Assert.IsFalse(Hit(Kw("nothere neither"), title: "alpha beta"));
    }

    [TestMethod]
    public void A11_All_EveryWordMustBeFound_InOneText()
    {
        // Changed from "possibly in different texts": capture F/Filter_Options op 54 (words "policy sort", title and help ticked)
        // shows classic listing no policy when the two words are in different texts.
        Assert.IsFalse(Hit(Kw("alpha beta", KeywordMatchMode.All), title: "alpha", help: "beta"));
        Assert.IsFalse(Hit(Kw("alpha beta", KeywordMatchMode.All), title: "alpha", help: "x", comment: "beta"));
        Assert.IsTrue(Hit(Kw("alpha beta", KeywordMatchMode.All), title: "beta alpha", help: "x"));
        Assert.IsFalse(Hit(Kw("alpha beta", KeywordMatchMode.All), title: "alpha", help: "x", comment: "y"));
    }

    [TestMethod]
    public void E4_All_AWordInAnUntickedTextDoesNotCount()
    {
        Assert.IsFalse(Hit(Kw("alpha beta", KeywordMatchMode.All, title: true, help: false, comment: false), title: "alpha", help: "beta"));
        Assert.IsFalse(Hit(Kw("alpha beta", KeywordMatchMode.Any, title: false, help: false, comment: true), title: "alpha", help: "beta", comment: "zzz"));
    }

    [TestMethod]
    public void A12_Exact_TrimsAndCollapsesWhiteSpace_InThePhraseAndTheText()
    {
        Assert.IsTrue(Hit(Kw("  hello   world ", KeywordMatchMode.Exact), title: "say hello \n  world now"));
        Assert.IsFalse(Hit(Kw("hello world", KeywordMatchMode.Exact), title: "world hello"));
        Assert.IsFalse(Hit(Kw("hello world", KeywordMatchMode.Exact), title: "hello big world"));
        Assert.IsTrue(Hit(Kw("hello world", KeywordMatchMode.Exact), title: "hello world"));
    }

    [TestMethod]
    public void A12_Exact_KeepsPunctuation()
    {
        Assert.IsFalse(Hit(Kw("a-b", KeywordMatchMode.Exact), title: "a b"));
        Assert.IsTrue(Hit(Kw("a-b, c.", KeywordMatchMode.Exact), title: "x a-b, c. y"));
        Assert.IsFalse(Hit(Kw("a-b c", KeywordMatchMode.Exact), title: "a-b, c"));
    }

    [TestMethod]
    public void E3_Exact_ThePhraseMustLieInsideOneText()
    {
        Assert.IsFalse(Hit(Kw("end start", KeywordMatchMode.Exact), title: "the end", help: "start of help"));
    }

    [TestMethod]
    public void A13_KeywordOver4096_IsTooLong_At4096_IsAccepted()
    {
        CollectionAssert.Contains(Kw(new string('a', 4097)).Validate().ToList(), FilterProblem.KeywordTooLong);
        Assert.AreEqual(0, Kw(new string('a', 4096)).Validate().Count);
        Assert.AreEqual(4096, FilterRules.MaxKeywordLength);
    }

    [TestMethod]
    public void A13_ALongHelpText_IsSearchedWithoutTrouble()
    {
        var help = new string('x', 900_000) + " needle";
        Assert.IsTrue(Hit(Kw("needle"), help: help));
        Assert.IsFalse(Hit(Kw("absent"), help: help));
    }

    [TestMethod]
    public void A14_ALineBreakInsideAWord_SplitsIt_ExceptThatExactCollapsesOnlyWhiteSpaceRuns()
    {
        Assert.IsFalse(Hit(Kw("network"), title: "net\nwork"));
        Assert.IsFalse(Hit(Kw("network", KeywordMatchMode.Exact), title: "net\nwork"));
    }

    [TestMethod]
    public void A15_NoUnicodeNormalization_SurrogatePairsStayUnits()
    {
        Assert.IsTrue(Hit(Kw("\U0001F600"), title: "smile \U0001F600 now"));
        Assert.IsFalse(Hit(Kw("\u00E9"), title: "e\u0301"));
        Assert.IsTrue(Hit(Kw("e\u0301"), title: "caf" + "e\u0301"));
    }

    [TestMethod]
    public void E2_WildcardAndPatternCharactersAreLiteral()
    {
        Assert.IsFalse(Hit(Kw("a.c"), title: "abc"));
        Assert.IsTrue(Hit(Kw("a.c"), title: "a.c"));
        Assert.IsFalse(Hit(Kw("a*"), title: "abc"));
        Assert.IsFalse(Hit(Kw("[a]"), title: "a"));
        Assert.IsFalse(Hit(Kw("%"), title: "abc"));
        Assert.IsFalse(Hit(Kw("a?c"), title: "abc"));
    }

    // ---------------------------------------------------------------- B. Managed, Configured, Commented

    private static FilterOptions Tri(FilterTriState managed = FilterTriState.Any, FilterTriState configured = FilterTriState.Any,
        FilterTriState commented = FilterTriState.Any) => new(managed, configured, commented, KeywordFilter.Off, RequirementsFilter.Off);

    private static bool Managed(string key, FilterTriState value)
    {
        var set = Build(new Spec("P", Key: key));
        return Make(Tri(managed: value)).Matches(Subject(set.Policies[0]));
    }

    [TestMethod]
    [DataRow(@"Software\Policies\Vendor", true)]
    [DataRow(@"software\POLICIES\vendor", true)]
    [DataRow(@"Software\Policies", true)]
    [DataRow(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", true)]
    [DataRow(@"Software\Microsoft\Windows\CurrentVersion\Policies", true)]
    [DataRow(@"Software\PoliciesX", false)]
    [DataRow(@"Software\Microsoft\Windows\CurrentVersion\PoliciesZ\Y", false)]
    [DataRow(@"Software\Vendor\Policies", false)]
    [DataRow(@"Software\Other", false)]
    public void B2_ManagedKeyRoots_OnAKeyBoundary_IgnoringCase(string key, bool managed)
    {
        Assert.AreEqual(managed, Managed(key, FilterTriState.Yes), "Yes for " + key);
        Assert.AreEqual(!managed, Managed(key, FilterTriState.No), "No for " + key);
    }

    [TestMethod]
    [DataRow(@"\Software\Policies\Vendor", true)]
    [DataRow(@"Software\Policies\Vendor\", true)]
    [DataRow(@"\\Software\Policies\Vendor\\", true)]
    [DataRow(@"Software\\Policies\Vendor", false)]
    public void B3_LeadingAndTrailingBackslashesAreTrimmed_DoubledOnesAreNotRepaired(string key, bool managed)
    {
        Assert.AreEqual(managed, Managed(key, FilterTriState.Yes), key);
    }

    [TestMethod]
    public void B1_Any_MatchesEverySubject_IncludingAnUnreadableState()
    {
        var set = Build(new Spec("P"));
        var filter = Make(FilterOptions.None);
        Assert.IsTrue(filter.Matches(Subject(set.Policies[0], null)));
        Assert.IsTrue(filter.Matches(Subject(set.Policies[0], PolicyState.Enabled)));
        Assert.IsTrue(filter.Matches(Subject(set.Policies[0], PolicyState.NotConfigured, "c")));
    }

    [TestMethod]
    [DataRow(PolicyState.Enabled, FilterTriState.Yes, true)]
    [DataRow(PolicyState.Disabled, FilterTriState.Yes, true)]
    [DataRow(PolicyState.NotConfigured, FilterTriState.Yes, false)]
    [DataRow(PolicyState.Enabled, FilterTriState.No, false)]
    [DataRow(PolicyState.Disabled, FilterTriState.No, false)]
    [DataRow(PolicyState.NotConfigured, FilterTriState.No, true)]
    [DataRow(PolicyState.NotConfigured, FilterTriState.Any, true)]
    [DataRow(PolicyState.Enabled, FilterTriState.Any, true)]
    public void B4_Configured_IsEnabledOrDisabled(PolicyState state, FilterTriState filterValue, bool expected)
    {
        var set = Build(new Spec("P"));
        Assert.AreEqual(expected, Make(Tri(configured: filterValue)).Matches(Subject(set.Policies[0], state)));
    }

    [TestMethod]
    [DataRow(FilterTriState.Yes)]
    [DataRow(FilterTriState.No)]
    public void B5_AnUnreadableState_MatchesOnlyAny(FilterTriState value)
    {
        var set = Build(new Spec("P"));
        Assert.IsFalse(Make(Tri(configured: value)).Matches(Subject(set.Policies[0], null)));
    }

    [TestMethod]
    [DataRow("a real comment", FilterTriState.Yes, true)]
    [DataRow("a real comment", FilterTriState.No, false)]
    [DataRow("", FilterTriState.Yes, false)]
    [DataRow("", FilterTriState.No, true)]
    [DataRow("   \t\r\n", FilterTriState.Yes, false)]
    [DataRow("   \t\r\n", FilterTriState.No, true)]
    [DataRow(null, FilterTriState.Yes, false)]
    [DataRow(null, FilterTriState.No, true)]
    public void B6_B7_Commented_NeedsANonBlankComment(string? comment, FilterTriState value, bool expected)
    {
        var set = Build(new Spec("P"));
        Assert.AreEqual(expected, Make(Tri(commented: value)).Matches(Subject(set.Policies[0], PolicyState.NotConfigured, comment)));
    }

    [TestMethod]
    public void B8_TheFilterUsesTheStateAndCommentOfTheSubject_NotAnotherSide()
    {
        var set = Build(new Spec("P", Cls: "Both"));
        var policy = set.Policies[0];
        var filter = Make(Tri(configured: FilterTriState.Yes, commented: FilterTriState.Yes));
        Assert.IsTrue(filter.Matches(Subject(policy, PolicyState.Enabled, "machine note")));
        Assert.IsFalse(filter.Matches(Subject(policy, PolicyState.NotConfigured, null)));
    }

    [TestMethod]
    public void E7_AllPartsAreCombinedWithAnd()
    {
        var set = Build(new Spec("P", Key: @"Software\Policies\X", Title: "Alpha"));
        var policy = set.Policies[0];
        var options = Tri(FilterTriState.Yes, FilterTriState.Yes, FilterTriState.Yes) with
        {
            Keyword = new KeywordFilter(true, "alpha", KeywordMatchMode.Any, true, false, false),
            Requirements = new RequirementsFilter(true, [PlatformList.KeyOf(set.SupportedOn[0])], PlatformMatchMode.Any),
        };
        var filter = Make(options);
        Assert.IsTrue(filter.Matches(Subject(policy, PolicyState.Enabled, "note")));
        Assert.IsFalse(filter.Matches(Subject(policy, PolicyState.NotConfigured, "note")), "configured fails");
        Assert.IsFalse(filter.Matches(Subject(policy, PolicyState.Enabled, null)), "commented fails");
        Assert.IsFalse(Make(options with { Keyword = options.Keyword with { Text = "zzz" } }).Matches(Subject(policy, PolicyState.Enabled, "note")), "keyword fails");
        Assert.IsFalse(Make(options with { Requirements = options.Requirements with { PlatformKeys = [FilterNs + ":SUP2"] } })
            .Matches(Subject(policy, PolicyState.Enabled, "note")), "requirements fail");
        var other = Build(new Spec("P", Key: @"Software\Other", Title: "Alpha")).Policies[0];
        Assert.IsFalse(filter.Matches(Subject(other, PolicyState.Enabled, "note")), "managed fails");
    }

    // ---------------------------------------------------------------- C. Requirements

    private static FilterOptions Req(PlatformMatchMode mode, params string[] keys) =>
        FilterOptions.None with { Requirements = new RequirementsFilter(true, keys, mode) };

    [TestMethod]
    public void C1_EnabledWithNoPlatform_IsPlatformsMissing()
    {
        var options = Req(PlatformMatchMode.Any);
        CollectionAssert.Contains(options.Validate().ToList(), FilterProblem.PlatformsMissing);
        Assert.IsFalse(PolicyFilter.TryCreate(options, out _, out _));
        var off = FilterOptions.None with { Requirements = new RequirementsFilter(false, [], PlatformMatchMode.All) };
        Assert.AreEqual(0, off.Validate().Count);
    }

    [TestMethod]
    public void C3_C4_C5_AnyAndAllAndNoSupportedOn()
    {
        var set = Build(new Spec("P1", Sup: "SUP"), new Spec("P2", Sup: "SUP2"), new Spec("P3", Sup: null));
        var key1 = FilterNs + ":SUP";
        var key2 = FilterNs + ":SUP2";
        var p1 = Subject(set.Policies[0]);
        var p2 = Subject(set.Policies[1]);
        var p3 = Subject(set.Policies[2]);

        var any = Make(Req(PlatformMatchMode.Any, key1, key2));
        Assert.IsTrue(any.Matches(p1));
        Assert.IsTrue(any.Matches(p2));
        Assert.IsFalse(any.Matches(p3), "a policy with no supported-on fails the requirements filter");

        var anyOne = Make(Req(PlatformMatchMode.Any, key1));
        Assert.IsTrue(anyOne.Matches(p1));
        Assert.IsFalse(anyOne.Matches(p2));

        var allOne = Make(Req(PlatformMatchMode.All, key1));
        Assert.IsTrue(allOne.Matches(p1));
        Assert.IsFalse(allOne.Matches(p2));

        var allTwo = Make(Req(PlatformMatchMode.All, key1, key2));
        Assert.IsFalse(allTwo.Matches(p1));
        Assert.IsFalse(allTwo.Matches(p2));
        Assert.IsFalse(allTwo.Matches(p3));
    }

    [TestMethod]
    public void C2_E10_AStaleKeyMatchesNothing_ButDoesNotBlockAnyWithAValidKey_AndPruneDropsIt()
    {
        var set = Build(new Spec("P1", Sup: "SUP"));
        var p1 = Subject(set.Policies[0]);
        Assert.IsFalse(Make(Req(PlatformMatchMode.Any, "Other.Ns:Gone")).Matches(p1));
        Assert.IsTrue(Make(Req(PlatformMatchMode.Any, "Other.Ns:Gone", FilterNs + ":SUP")).Matches(p1));
        var pruned = PlatformList.Prune(["Other.Ns:Gone", FilterNs + ":SUP", FilterNs + ":SUP2"], set);
        CollectionAssert.AreEquivalent(new[] { FilterNs + ":SUP", FilterNs + ":SUP2" }, pruned.ToArray());
        CollectionAssert.AreEqual(Array.Empty<string>(), PlatformList.Prune([], set).ToArray());
    }

    [TestMethod]
    public void C7_E10_KeysAreComparedIgnoringCase()
    {
        var set = Build(new Spec("P1", Sup: "SUP"));
        Assert.IsTrue(Make(Req(PlatformMatchMode.Any, "BLIND.F:sup")).Matches(Subject(set.Policies[0])));
        Assert.AreEqual(1, PlatformList.Prune(["blind.f:SUP"], set).Count, "a key in other case is still defined by the set");
    }

    [TestMethod]
    public void C6_C8_PlatformListHasOneEntryPerDefinition_InSourceOrder_AndSelectAllIsEveryKey()
    {
        var set = Build(new Spec("P1"));
        var platforms = PlatformList.From(set);
        CollectionAssert.AreEqual(new[] { FilterNs + ":SUP", FilterNs + ":SUP2" }, platforms.Select(p => p.Key).ToArray());
        Assert.AreEqual("SUP text", platforms[0].DisplayName);
        Assert.AreEqual("SUP2 text", platforms[1].DisplayName);
        CollectionAssert.AreEqual(platforms.Select(p => p.Key).ToArray(), PlatformList.All(platforms).ToArray());
        Assert.AreEqual(FilterNs + ":SUP", PlatformList.KeyOf(set.SupportedOn[0]));
    }

    [TestMethod]
    public void C4_AllWithTheSameKeyTwice_OrInAnotherCase_IsTheSameAsOneKey()
    {
        var set = Build(new Spec("P1", Sup: "SUP"), new Spec("P2", Sup: "SUP2"));
        var key1 = FilterNs + ":SUP";
        var p1 = Subject(set.Policies[0]);
        var p2 = Subject(set.Policies[1]);
        var twice = Make(Req(PlatformMatchMode.All, key1, key1));
        Assert.IsTrue(twice.Matches(p1));
        Assert.IsFalse(twice.Matches(p2));
        var cased = Make(Req(PlatformMatchMode.All, key1, key1.ToUpperInvariant()));
        Assert.IsTrue(cased.Matches(p1));
    }

    [TestMethod]
    public void C6_TwoDefinitionsWithTheSameTextInTwoFiles_AreBothListed()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("PA"), ["PA"]);
        Std(t, "b", "Blind.B", P("PB"), ["PB"]);
        var platforms = PlatformList.From(t.Load());
        Assert.AreEqual(2, platforms.Count);
        Assert.AreEqual(platforms[0].DisplayName, platforms[1].DisplayName);
        Assert.AreNotEqual(platforms[0].Key, platforms[1].Key);
    }

    // ---------------------------------------------------------------- D. Aggregates, All Settings, misc

    [TestMethod]
    public void D1_D2_E6_CountApplyAndTotalAgree_AndKeepInputOrder()
    {
        var set = Build(new Spec("P1", Title: "alpha"), new Spec("P2", Title: "beta"), new Spec("P3", Title: "alpha two"));
        var subjects = set.Policies.Select(p => Subject(p)).ToList();
        var filter = Make(Kw("alpha"));
        var result = filter.Apply(subjects);
        Assert.AreEqual(3, result.Total);
        CollectionAssert.AreEqual(new[] { "P1", "P3" }, result.Shown.Select(s => s.Policy.Name).ToArray());
        var count = filter.Count(subjects);
        Assert.AreEqual(new FilterCount(2, 3), count);
        Assert.AreEqual(count.Shown, result.Shown.Count);
        var none = Make(Kw("nomatch")).Count(subjects);
        Assert.AreEqual(new FilterCount(0, 3), none, "a category with zero matches reports 0 of N");
        Assert.AreEqual(new FilterCount(0, 0), filter.Count([]));
        Assert.AreEqual(0, filter.Apply([]).Total);
        Assert.AreEqual(0, filter.Apply([]).Shown.Count);
    }

    [TestMethod]
    public void D5_ACancelledToken_ThrowsOperationCanceled_ForBigInputs()
    {
        var set = Build(new Spec("P1"));
        var subjects = Enumerable.Repeat(Subject(set.Policies[0]), 700).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var filter = Make(FilterOptions.None);
        Assert.Throws<OperationCanceledException>(() => filter.Apply(subjects, cts.Token));
        Assert.Throws<OperationCanceledException>(() => filter.Count(subjects, cts.Token));
    }

    [TestMethod]
    public void D6_ThePlatformKeysAndWordsAreCopiedWhenTheFilterIsBuilt()
    {
        var set = Build(new Spec("P1", Sup: "SUP"));
        var keys = new List<string> { FilterNs + ":SUP" };
        var filter = Make(Req(PlatformMatchMode.Any, keys.ToArray()));
        var mutable = new List<string> { FilterNs + ":SUP" };
        var options = FilterOptions.None with { Requirements = new RequirementsFilter(true, mutable, PlatformMatchMode.Any) };
        var built = Make(options);
        mutable.Clear();
        mutable.Add("Other:Key");
        Assert.IsTrue(built.Matches(Subject(set.Policies[0])), "later changes to the caller's list must not reach the filter");
        Assert.IsTrue(filter.Matches(Subject(set.Policies[0])));
    }

    [TestMethod]
    public void D7_E8_NullArguments()
    {
        var set = Build(new Spec("P1"));
        var filter = Make(FilterOptions.None);
        Assert.Throws<ArgumentNullException>(() => filter.Matches(new FilterSubject(null!, null, null)));
        Assert.Throws<ArgumentNullException>(() => filter.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => filter.Count(null!));
        Assert.Throws<ArgumentNullException>(() => AllSettings.List(null!, GpoSide.Machine));
        Assert.Throws<ArgumentNullException>(() => PolicyFilter.TryCreate(null!, out _, out _));
        Assert.IsNotNull(set);
    }

    [TestMethod]
    public void E5_MatchesIsPure()
    {
        var set = Build(new Spec("P1", Title: "alpha"));
        var filter = Make(Kw("alpha"));
        var subject = Subject(set.Policies[0], PolicyState.Enabled, "c");
        Assert.AreEqual(filter.Matches(subject), filter.Matches(subject));
        Assert.IsTrue(filter.Matches(subject));
        Assert.IsTrue(filter.Matches(subject));
    }

    [TestMethod]
    public void D3_D4_E11_AllSettings_OnePerSidePolicy_InSourceOrder_IncludingUncategorized()
    {
        var set = Build(
            new Spec("PM", Cls: "Machine"),
            new Spec("PU", Cls: "User"),
            new Spec("PB", Cls: "Both"),
            new Spec("PO", Cls: "Machine", Cat: null),
            new Spec("PB2", Cls: "Both"));
        var machine = AllSettings.List(set, GpoSide.Machine).Select(p => p.Name).ToArray();
        var user = AllSettings.List(set, GpoSide.User).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "PM", "PB", "PO", "PB2" }, machine);
        CollectionAssert.AreEqual(new[] { "PU", "PB", "PB2" }, user);
        Assert.AreEqual(0, AllSettings.List(Bwc.Policy.Admx.TemplateSet.Empty, GpoSide.Machine).Count);
    }

    [TestMethod]
    public void D4_AllSettings_ACancelledToken_Throws()
    {
        var set = Build(Enumerable.Range(0, 600).Select(i => new Spec("PM" + i)).ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AllSettings.List(set, GpoSide.Machine, cts.Token));
    }
}

/// <summary>
/// Blind tests of the template store, the sources and the import (docs/failure-modes/template-store.md). Everything runs in
/// scratch folders with the current-user ACL rules; the ship rules are only checked in memory.
/// </summary>
[TestClass]
public sealed class StoreBlindTests
{
    private static readonly ProtectedAcl Acl = ProtectedAcl.ForCurrentUser();
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private sealed class World : IDisposable
    {
        internal TempTemplates Sys { get; } = new();

        internal TempTemplates Src { get; } = new();

        internal TempTemplates Host { get; } = new();

        internal string Store => Path.Combine(Host.Root, "store");

        internal TemplateSources Sources => new(Sys.Root, Store, Acl);

        internal TemplateImport Importer => new(Sources);

        internal TemplateStoreWriter Writer => new(Store, Sys.Root, Acl);

        internal string SrcAdmx(string name) => Path.Combine(Src.Root, name + ".admx");

        public void Dispose()
        {
            Host.Dispose();
            Src.Dispose();
            Sys.Dispose();
        }
    }

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

    private static string[] Problems(TemplateImportCheck check) => check.Problems.Select(p => p.Code.ToString()).ToArray();

    private static void AssertCode(TemplateImportCheck check, TemplateImportProblemCode code)
    {
        Assert.IsFalse(check.CanImport, "must not be importable");
        Assert.IsTrue(check.Problems.Any(p => p.Code == code), "expected " + code + " in " + string.Join(",", Problems(check)));
    }

    private static List<string> StoreFiles(string store) =>
        Directory.Exists(store)
            ? Directory.EnumerateFileSystemEntries(store, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(store, p)).Order().ToList()
            : [];

    private static (bool Protected, List<(SecurityIdentifier Sid, FileSystemRights Rights)> Rules) ReadAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.FileSystemRights))
            .ToList();
        return (security.AreAccessRulesProtected, rules);
    }

    private static void GoodTemplate(TempTemplates folder, string file, string ns, string policy = "Pol", string usings = "")
    {
        Std(folder, file, ns, P(policy), [policy], usings: usings);
    }

    // ---------------------------------------------------------------- Names (G10)

    [TestMethod]
    [DataRow("a.admx", true)]
    [DataRow("Win-Defender.admx", true)]
    [DataRow("\u00FCn\u00EF.admx", true)]
    [DataRow("en-US", true)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("..", false)]
    [DataRow(".", false)]
    [DataRow("a\\b.admx", false)]
    [DataRow("a/b.admx", false)]
    [DataRow("C:a.admx", false)]
    [DataRow("a.admx:stream", false)]
    [DataRow("a*.admx", false)]
    [DataRow("a?.admx", false)]
    [DataRow("a.admx.", false)]
    [DataRow("a.admx ", false)]
    [DataRow(" a.admx", false)]
    [DataRow("a\u0001b.admx", false)]
    [DataRow("CON", false)]
    [DataRow("nul", false)]
    [DataRow("COM1", false)]
    [DataRow("LPT9", false)]
    [DataRow("CON.admx", false)]
    [DataRow("com1.admx", false)]
    public void G10_SafeSegment(string? name, bool expected)
    {
        Assert.AreEqual(expected, TemplateFileNames.IsSafeSegment(name), name);
    }

    [TestMethod]
    public void G10_SafeSegment_LengthLimit()
    {
        Assert.IsTrue(TemplateFileNames.IsSafeSegment(new string('a', AdmxLimits.MaxFileNameLength)));
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(new string('a', AdmxLimits.MaxFileNameLength + 1)));
    }

    [TestMethod]
    [DataRow("a.admx", true)]
    [DataRow("A.ADMX", true)]
    [DataRow("a.Admx", true)]
    [DataRow(".admx", false)]
    [DataRow("a.adml", false)]
    [DataRow("a.txt", false)]
    [DataRow("a", false)]
    [DataRow("a.admx.bak", false)]
    [DataRow("..\\a.admx", false)]
    [DataRow("NUL.admx", false)]
    [DataRow(null, false)]
    public void G10_SafeAdmxFileName(string? name, bool expected)
    {
        Assert.AreEqual(expected, TemplateFileNames.IsSafeAdmxFileName(name), name);
    }

    [TestMethod]
    public void G10_StemAndAdmlName()
    {
        Assert.AreEqual("Foo", TemplateFileNames.StemOf("Foo.admx"));
        Assert.AreEqual("Foo.adml", TemplateFileNames.AdmlNameOf("Foo.admx"));
    }

    // ---------------------------------------------------------------- A. The store folder (writer)

    [TestMethod]
    public void A2_F2_TheStoreIsCreatedWithAProtectedDacl_AndMissingParentsToo()
    {
        using var w = new World();
        var store = Path.Combine(w.Host.Root, "p1", "p2", "store");
        var writer = new TemplateStoreWriter(store, w.Sys.Root, Acl);
        writer.EnsureStore();
        var me = WindowsIdentity.GetCurrent().User!;
        foreach (var folder in new[] { store, Path.Combine(w.Host.Root, "p1", "p2") })
        {
            var (isProtected, rules) = ReadAcl(folder);
            Assert.IsTrue(isProtected, "inheritance is off: " + folder);
            CollectionAssert.AreEquivalent(new[] { me, System }, rules.Select(r => r.Sid).Distinct().ToArray(), "only the current user and SYSTEM: " + folder);
            Assert.IsTrue(rules.All(r => (r.Rights & FileSystemRights.FullControl) == FileSystemRights.FullControl), "full control: " + folder);
        }
    }

    [TestMethod]
    public void F1_TheShipRulesInMemory_AreProtected_OwnerAdministrators_SystemAndAdministratorsOnly()
    {
        var security = ProtectedAcl.Ship().NewSecurity();
        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.AreEqual(Admins, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        CollectionAssert.AreEquivalent(new[] { System, Admins }, rules.Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToArray());
        Assert.IsTrue(rules.All(r => r.AccessControlType == AccessControlType.Allow));
        Assert.IsTrue(rules.All(r => (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl));
    }

    [TestMethod]
    public void G12_EnsureStoreTwice_IsIdempotent_AndKeepsTheRules()
    {
        using var w = new World();
        w.Writer.EnsureStore();
        var first = ReadAcl(w.Store);
        w.Writer.EnsureStore();
        var second = ReadAcl(w.Store);
        Assert.AreEqual(first.Protected, second.Protected);
        CollectionAssert.AreEquivalent(first.Rules.Select(r => r.Sid).ToArray(), second.Rules.Select(r => r.Sid).ToArray());
    }

    [TestMethod]
    public void A6_AStoreThatIsAFile_IsNotADirectory()
    {
        using var w = new World();
        File.WriteAllText(w.Store, "i am a file");
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.EnsureStore());
        Assert.AreEqual(TemplateStoreFailure.NotADirectory, ex.Reason);
        Assert.IsFalse(string.IsNullOrEmpty(ex.Path));
        Assert.IsFalse(string.IsNullOrEmpty(ex.Operation));
    }

    [TestMethod]
    public void A7_AUncRoot_IsNotLocal()
    {
        using var w = new World();
        var ex = Assert.Throws<TemplateStoreException>(() => new TemplateStoreWriter(@"\\server\share\store", w.Sys.Root, Acl));
        Assert.AreEqual(TemplateStoreFailure.StoreRootNotLocal, ex.Reason);
    }

    [TestMethod]
    public void A11_ARootThatIsTheSystemFolder_InsideItOrAroundIt_IsForbidden()
    {
        using var w = new World();
        foreach (var root in new[] { w.Sys.Root, Path.Combine(w.Sys.Root, "inside"), Path.GetDirectoryName(w.Sys.Root)! })
        {
            var ex = Assert.Throws<TemplateStoreException>(() => new TemplateStoreWriter(root, w.Sys.Root, Acl), root);
            Assert.AreEqual(TemplateStoreFailure.StoreRootForbidden, ex.Reason, root);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(@"relative\store")]
    [DataRow(@"C:\")]
    [DataRow("C:\\bad<>|name")]
    public void A12_UnusableRootText_IsRefusedWhenTheWriterIsBuilt(string root)
    {
        using var w = new World();
        Assert.Throws<TemplateStoreException>(() => new TemplateStoreWriter(root, w.Sys.Root, Acl), root);
    }

    [TestMethod]
    public void G11_TheRootIsNormalizedToAFullPath()
    {
        using var w = new World();
        var odd = Path.Combine(w.Host.Root, "a", "b", "..", "store") + Path.DirectorySeparatorChar;
        var writer = new TemplateStoreWriter(odd, w.Sys.Root, Acl);
        Assert.AreEqual(Path.Combine(w.Host.Root, "a", "store"), writer.StoreRoot.TrimEnd('\\'), true);
        Assert.IsTrue(Path.IsPathFullyQualified(writer.StoreRoot));
    }

    [TestMethod]
    public void A9_ShipRulesWithoutElevation_AreATypedFailure_AndNothingIsLeftBehind()
    {
        using var w = new World();
        var store = Path.Combine(w.Host.Root, "shipstore");
        var writer = new TemplateStoreWriter(store, w.Sys.Root, ProtectedAcl.Ship());
        try
        {
            writer.EnsureStore();
        }
        catch (TemplateStoreException ex)
        {
            Assert.IsTrue(ex.Reason is TemplateStoreFailure.AccessDenied or TemplateStoreFailure.OwnerNotAllowed, ex.Reason.ToString());
            Assert.IsTrue(ex.Win32Error is 5 or 1307 or 1308, "Win32 " + ex.Win32Error);
            Assert.IsFalse(Directory.Exists(store), "nothing is left behind");
        }
    }

    [TestMethod]
    public void A5_AStoreThatIsAJunction_IsRefusedForWriting_AndNothingIsWrittenThroughIt()
    {
        using var w = new World();
        using var target = new TempTemplates();
        w.Host.MakeJunction(w.Store, target.Root);
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]));
        Assert.AreEqual(TemplateStoreFailure.ReparsePoint, ex.Reason);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(target.Root).Length);
    }

    [TestMethod]
    public void A5_ALanguageFolderThatIsAJunction_IsRefusedForWriting()
    {
        using var w = new World();
        using var target = new TempTemplates();
        w.Writer.EnsureStore();
        w.Host.MakeJunction(Path.Combine(w.Store, "en-US"), target.Root);
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]));
        Assert.AreEqual(TemplateStoreFailure.ReparsePoint, ex.Reason);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(target.Root).Length);
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "a.admx")));
    }

    // ---------------------------------------------------------------- D. Copy

    [TestMethod]
    public void D1_D2_G13_WriteTemplate_WritesBothFiles_NoTempLeft_AndALaterWriteReplaces()
    {
        using var w = new World();
        var admx = new byte[] { 1, 2, 3 };
        var adml = new byte[] { 4, 5, 6 };
        w.Writer.WriteTemplate("a.admx", admx, "en-US", adml);
        CollectionAssert.AreEqual(admx, Bytes(Path.Combine(w.Store, "a.admx")));
        CollectionAssert.AreEqual(adml, Bytes(Path.Combine(w.Store, "en-US", "a.adml")));
        CollectionAssert.AreEqual(new[] { "a.admx", "en-US", Path.Combine("en-US", "a.adml") }, StoreFiles(w.Store));
        w.Writer.WriteTemplate("a.admx", [9, 9], "en-US", [8, 8]);
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, Bytes(Path.Combine(w.Store, "a.admx")));
        CollectionAssert.AreEqual(new byte[] { 8, 8 }, Bytes(Path.Combine(w.Store, "en-US", "a.adml")));
        Assert.IsFalse(StoreFiles(w.Store).Any(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void D11_ALanguageFolderIsCreatedWithTheProtectedDacl()
    {
        using var w = new World();
        w.Writer.WriteTemplate("a.admx", [1], "de-DE", [2]);
        var (isProtected, rules) = ReadAcl(Path.Combine(w.Store, "de-DE"));
        Assert.IsTrue(isProtected);
        CollectionAssert.AreEquivalent(new[] { WindowsIdentity.GetCurrent().User!, System }, rules.Select(r => r.Sid).Distinct().ToArray());
    }

    [TestMethod]
    [DataRow(@"..\x.admx")]
    [DataRow("a/b.admx")]
    [DataRow(@"a\b.admx")]
    [DataRow("C:x.admx")]
    [DataRow("x.admx:stream")]
    [DataRow("..")]
    [DataRow("CON.admx")]
    [DataRow("x.admx ")]
    [DataRow("x.admx.")]
    [DataRow("")]
    [DataRow("x.txt")]
    public void D8_E6_UnsafeFileNames_AreRefusedBeforeAnyIo(string name)
    {
        using var w = new World();
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate(name, [1], "en-US", [2]), name);
        Assert.AreEqual(TemplateStoreFailure.UnsafeName, ex.Reason, name);
        Assert.IsFalse(Directory.Exists(w.Store), "no I/O happened for " + name);
        var remove = Assert.Throws<TemplateStoreException>(() => w.Writer.RemoveTemplate(name), name);
        Assert.AreEqual(TemplateStoreFailure.UnsafeName, remove.Reason, name);
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow(@"a\b")]
    [DataRow("a/b")]
    [DataRow("")]
    [DataRow("C:")]
    public void D9_UnsafeCultureNames_AreRefused(string culture)
    {
        using var w = new World();
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [1], culture, [2]), culture);
        Assert.AreEqual(TemplateStoreFailure.UnsafeName, ex.Reason, culture);
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void D3_D6_TheTargetNameIsAFolder_FailsWithIo_AndTheAdmlThisCallCreatedIsRemoved()
    {
        using var w = new World();
        w.Writer.EnsureStore();
        Directory.CreateDirectory(Path.Combine(w.Store, "a.admx"));
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]));
        Assert.AreEqual(TemplateStoreFailure.Io, ex.Reason);
        Assert.IsTrue(Directory.Exists(Path.Combine(w.Store, "a.admx")));
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "en-US", "a.adml")), "the ADML this call created is removed again");
        Assert.IsFalse(StoreFiles(w.Store).Any(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void D3_AnAdmlThatExistedBeforeTheCall_IsNeverRemoved()
    {
        using var w = new World();
        w.Writer.WriteTemplate("keep.admx", [1], "en-US", [7]);
        var adml = Path.Combine(w.Store, "en-US", "a.adml");
        File.WriteAllBytes(adml, [5, 5]);
        Directory.CreateDirectory(Path.Combine(w.Store, "a.admx"));
        Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]));
        Assert.IsTrue(File.Exists(adml), "an ADML that existed before the call stays");
    }

    [TestMethod]
    public void D4_ATargetHeldOpen_IsInUse_TheTempIsRemoved_AndTheOldFileIsUntouched()
    {
        using var w = new World();
        w.Writer.WriteTemplate("a.admx", [1, 1], "en-US", [2, 2]);
        var path = Path.Combine(w.Store, "a.admx");
        using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [9], "en-US", [8]));
            Assert.AreEqual(TemplateStoreFailure.InUse, ex.Reason);
            Assert.IsTrue(ex.Win32Error is 32 or 33, "Win32 " + ex.Win32Error);
            Assert.IsNotNull(hold);
        }

        CollectionAssert.AreEqual(new byte[] { 1, 1 }, Bytes(path));
        Assert.IsFalse(StoreFiles(w.Store).Any(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void D5_AReadOnlyTarget_IsAccessDenied_AndTheAttributeIsNeverCleared()
    {
        using var w = new World();
        w.Writer.WriteTemplate("a.admx", [1, 1], "en-US", [2, 2]);
        var path = Path.Combine(w.Store, "a.admx");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [9], "en-US", [8]));
            Assert.AreEqual(TemplateStoreFailure.AccessDenied, ex.Reason);
            Assert.IsTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            CollectionAssert.AreEqual(new byte[] { 1, 1 }, Bytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ---------------------------------------------------------------- E. Remove (writer)

    [TestMethod]
    public void E3_E4_RemoveTemplate_DeletesTheAdmxAndTheAdmlOfEveryLanguageFolder_AndReturnsThePaths()
    {
        using var w = new World();
        w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]);
        w.Writer.WriteTemplate("a.admx", [1], "de-DE", [3]);
        w.Writer.WriteTemplate("b.admx", [1], "en-US", [4]);
        var removed = w.Writer.RemoveTemplate("a.admx");
        Assert.AreEqual(3, removed.Count);
        Assert.IsTrue(removed.Any(p => p.EndsWith("a.admx", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "a.admx")));
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "en-US", "a.adml")));
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "de-DE", "a.adml")));
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "b.admx")), "another template is untouched");
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "en-US", "b.adml")));
    }

    [TestMethod]
    public void E4_ALanguageFolderThatIsAJunction_IsSkipped_NeverFollowed()
    {
        using var w = new World();
        using var outside = new TempTemplates();
        File.WriteAllBytes(Path.Combine(outside.Root, "a.adml"), [7]);
        w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]);
        w.Host.MakeJunction(Path.Combine(w.Store, "fr-FR"), outside.Root);
        w.Writer.RemoveTemplate("a.admx");
        Assert.IsTrue(File.Exists(Path.Combine(outside.Root, "a.adml")), "nothing is deleted through a link");
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "a.admx")));
    }

    [TestMethod]
    public void E5_ARemoveOfAnOpenAdmx_IsInUse()
    {
        using var w = new World();
        w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]);
        using var hold = new FileStream(Path.Combine(w.Store, "a.admx"), FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.RemoveTemplate("a.admx"));
        Assert.AreEqual(TemplateStoreFailure.InUse, ex.Reason);
        Assert.IsNotNull(hold);
    }

    // ---------------------------------------------------------------- Sources (A1, A3, A5, A6, B)

    [TestMethod]
    public void A1_AMissingStore_IsNormal_TheSystemFolderLoadsAlone()
    {
        using var w = new World();
        GoodTemplate(w.Sys, "sys", "Blind.Sys");
        var result = w.Sources.Load();
        Assert.AreEqual(TemplateStoreStatus.Missing, result.Store);
        FailureAssert.None(result.Set);
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Sys", "Pol"));
        Assert.AreEqual(0, result.ImportedFiles.Count);
        Assert.IsFalse(Directory.Exists(w.Store), "reading never creates the store");
        Assert.AreEqual(TemplateStoreStatus.Missing, w.Sources.InspectStore().Status);
    }

    [TestMethod]
    public void A3_AStoreThatGrantsWriteToEveryone_IsRejected_NotLoaded_AndTheSystemFolderStillLoads()
    {
        using var w = new World();
        GoodTemplate(w.Sys, "sys", "Blind.Sys");
        w.Writer.WriteTemplate("imp.admx", File.ReadAllBytes(Path.Combine(w.Sys.Root, "sys.admx")), "en-US", File.ReadAllBytes(Path.Combine(w.Sys.Root, "en-US", "sys.adml")));
        var info = new DirectoryInfo(w.Store);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Write | FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        var result = w.Sources.Load();
        Assert.AreEqual(TemplateStoreStatus.AclRejected, result.Store);
        Assert.IsFalse(string.IsNullOrEmpty(result.StoreDetail));
        Assert.AreEqual(0, result.ImportedFiles.Count);
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Sys", "Pol"));
        Assert.AreEqual(1, result.Set.Files.Count);
        Assert.AreEqual(TemplateStoreStatus.AclRejected, w.Sources.InspectStore().Status);
    }

    [TestMethod]
    public void A3_ARejectedStore_CannotBeWritten_AndTheAclIsNotRepaired()
    {
        using var w = new World();
        w.Writer.EnsureStore();
        var info = new DirectoryInfo(w.Store);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Write | FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        var ex = Assert.Throws<TemplateStoreException>(() => w.Writer.WriteTemplate("a.admx", [1], "en-US", [2]));
        Assert.AreEqual(TemplateStoreFailure.StoreAclRejected, ex.Reason);
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "a.admx")));
        var rules = ReadAcl(w.Store).Rules;
        Assert.IsTrue(rules.Any(r => r.Sid == new SecurityIdentifier(WellKnownSidType.WorldSid, null)), "the ACL is never repaired silently");
    }

    [TestMethod]
    public void A5_AStoreThatIsAJunction_IsReportedAndNotLoaded()
    {
        using var w = new World();
        using var real = new TempTemplates();
        GoodTemplate(w.Sys, "sys", "Blind.Sys");
        GoodTemplate(real, "imp", "Blind.Imp");
        w.Host.MakeJunction(w.Store, real.Root);
        var result = w.Sources.Load();
        Assert.AreEqual(TemplateStoreStatus.ReparsePoint, result.Store);
        Assert.IsNull(result.Set.FindPolicy("Blind.Imp", "Pol"));
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Sys", "Pol"));
    }

    [TestMethod]
    public void A6_Sources_AStoreThatIsAFile_IsNotADirectory()
    {
        using var w = new World();
        GoodTemplate(w.Sys, "sys", "Blind.Sys");
        File.WriteAllText(w.Store, "file");
        var result = w.Sources.Load();
        Assert.AreEqual(TemplateStoreStatus.NotADirectory, result.Store);
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Sys", "Pol"));
    }

    [TestMethod]
    public void B1_B6_G9_TheSystemWinsADuplicateNamespace_TheImportIsShadowed_AndAStoreTemplateCanUseASystemNamespace()
    {
        using var w = new World();
        Std(w.Sys, "sys", "Blind.Shared", P("FromSystem"), ["FromSystem"]);
        Std(w.Src, "imp", "Blind.Shared", P("FromImport"), ["FromImport"]);
        Std(w.Src, "user", "Blind.User", P("UsesSystem", cat: "s:Root"), ["UsesSystem"], usings: Using("s", "Blind.Shared"));
        foreach (var name in new[] { "imp", "user" })
        {
            w.Writer.WriteTemplate(name + ".admx", Bytes(w.SrcAdmx(name)), "en-US", Bytes(Path.Combine(w.Src.Root, "en-US", name + ".adml")));
        }

        var first = w.Sources.Load();
        Assert.AreEqual(TemplateStoreStatus.Ready, first.Store);
        Assert.IsNotNull(first.Set.FindPolicy("Blind.Shared", "FromSystem"));
        Assert.IsNull(first.Set.FindPolicy("Blind.Shared", "FromImport"));
        var shadowed = first.Shadowed.Single();
        Assert.AreEqual("Blind.Shared", shadowed.Namespace, true);
        StringAssert.EndsWith(shadowed.Path, "imp.admx", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(shadowed.WinnerPath, "sys.admx", StringComparison.OrdinalIgnoreCase);
        StringAssert.StartsWith(shadowed.Path, w.Store, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "imp.admx")), "the import stays on disk");
        Assert.AreEqual(1, first.Set.Failures.Count(f => f.Code == TemplateFailureCode.NamespaceShadowed));
        var usesSystem = first.Set.FindPolicy("Blind.User", "UsesSystem");
        Assert.IsNotNull(usesSystem, "a store template resolves a system namespace");
        Assert.IsNotNull(usesSystem.Category);
        Assert.AreEqual(0, first.Set.Failures.Count(f => f.Code == TemplateFailureCode.UndefinedUsing));
        Assert.IsTrue(first.ImportedFiles.All(f => f.SourceIndex == TemplateSources.StoreSourceIndex));
        Assert.AreEqual(1, first.ImportedFiles.Count(f => f.Namespace == "Blind.User"));

        var second = w.Sources.Load();
        CollectionAssert.AreEqual(first.Set.Files.Select(f => f.AdmxPath).ToArray(), second.Set.Files.Select(f => f.AdmxPath).ToArray());
        CollectionAssert.AreEqual(first.Set.Failures.ToArray(), second.Set.Failures.ToArray());
        CollectionAssert.AreEqual(first.Shadowed.ToArray(), second.Shadowed.ToArray());
    }

    [TestMethod]
    public void B2_TwoStoreFilesWithOneNamespace_AreDuplicateNamespace_NotShadowed()
    {
        using var w = new World();
        Std(w.Src, "one", "Blind.Same", P("P1"), ["P1"]);
        Std(w.Src, "two", "Blind.Same", P("P2"), ["P2"]);
        foreach (var name in new[] { "one", "two" })
        {
            w.Writer.WriteTemplate(name + ".admx", Bytes(w.SrcAdmx(name)), "en-US", Bytes(Path.Combine(w.Src.Root, "en-US", name + ".adml")));
        }

        var result = w.Sources.Load();
        Assert.AreEqual(1, result.Set.Failures.Count(f => f.Code == TemplateFailureCode.DuplicateNamespace));
        Assert.AreEqual(0, result.Shadowed.Count);
    }

    [TestMethod]
    public void B3_AMissingSystemFolder_IsReported_AndTheStoreStillLoads()
    {
        using var w = new World();
        Std(w.Src, "imp", "Blind.Imp", P("PImp"), ["PImp"]);
        w.Writer.WriteTemplate("imp.admx", Bytes(w.SrcAdmx("imp")), "en-US", Bytes(Path.Combine(w.Src.Root, "en-US", "imp.adml")));
        Directory.Delete(w.Sys.Root, true);
        var result = w.Sources.Load();
        Assert.AreEqual(1, result.Set.Failures.Count(f => f.Code == TemplateFailureCode.FolderNotFound));
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Imp", "PImp"));
        Directory.CreateDirectory(w.Sys.Root);
    }

    [TestMethod]
    public void B4_G13_ABadStoreTemplate_FailsAloneAndATempFileIsIgnored()
    {
        using var w = new World();
        Std(w.Src, "good", "Blind.Good", P("PGood"), ["PGood"]);
        w.Writer.WriteTemplate("good.admx", Bytes(w.SrcAdmx("good")), "en-US", Bytes(Path.Combine(w.Src.Root, "en-US", "good.adml")));
        File.WriteAllText(Path.Combine(w.Store, "broken.admx"), "<not xml");
        File.WriteAllText(Path.Combine(w.Store, "good." + Guid.NewGuid().ToString("N") + ".tmp"), "stray");
        var result = w.Sources.Load();
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Good", "PGood"));
        Assert.AreEqual(1, result.Set.Failures.Count);
        Assert.AreEqual(TemplateFailureCode.MalformedXml, result.Set.Failures[0].Code);
    }

    [TestMethod]
    public void B5_AnImportWithOnlyAnEnUsAdml_LoadsForAnotherUiCulture()
    {
        using var w = new World();
        Std(w.Src, "imp", "Blind.Imp", P("PImp"), ["PImp"]);
        w.Writer.WriteTemplate("imp.admx", Bytes(w.SrcAdmx("imp")), "en-US", Bytes(Path.Combine(w.Src.Root, "en-US", "imp.adml")));
        var result = w.Sources.Load("de-DE");
        FailureAssert.None(result.Set);
        Assert.AreEqual("en-US", result.Set.FindFile("Blind.Imp")!.Culture, true);
    }

    [TestMethod]
    public void B7_ARemovedSystemTemplate_GivesUndefinedUsing_AndTheImportIsNotDeleted()
    {
        using var w = new World();
        Std(w.Sys, "base", "Blind.Base", P("PBase"), ["PBase"]);
        Std(w.Src, "imp", "Blind.Imp", P("PImp"), ["PImp"], usings: Using("b", "Blind.Base"));
        w.Writer.WriteTemplate("imp.admx", Bytes(w.SrcAdmx("imp")), "en-US", Bytes(Path.Combine(w.Src.Root, "en-US", "imp.adml")));
        FailureAssert.None(w.Sources.Load().Set);
        File.Delete(Path.Combine(w.Sys.Root, "base.admx"));
        var result = w.Sources.Load();
        Assert.AreEqual(1, result.Set.Failures.Count(f => f.Code == TemplateFailureCode.UndefinedUsing));
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "imp.admx")));
        Assert.IsNotNull(result.Set.FindPolicy("Blind.Imp", "PImp"));
    }

    // ---------------------------------------------------------------- C. Import checks

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    [DataRow("bad<>|name.admx")]
    [DataRow(@"C:\no\such\folder\file.admx")]
    public void C1_G7_AMissingOrInvalidPath_IsSourceMissing(string? path)
    {
        using var w = new World();
        var check = w.Importer.Check(path!);
        AssertCode(check, TemplateImportProblemCode.SourceMissing);
        var result = w.Importer.Import(path!);
        Assert.IsFalse(result.Imported);
        Assert.IsTrue(result.Problems.Any(p => p.Code == TemplateImportProblemCode.SourceMissing));
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C2_AClassicAdm_IsNotSupported()
    {
        using var w = new World();
        var path = w.Src.Write("old.adm", "CLASS MACHINE\nCATEGORY !!x\nEND CATEGORY\n");
        AssertCode(w.Importer.Check(path), TemplateImportProblemCode.ClassicAdmNotSupported);
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    [DataRow("a.adml")]
    [DataRow("a.xml")]
    [DataRow("noextension")]
    [DataRow("a.admx.txt")]
    public void C3_AnotherExtension_IsNotAdmx(string name)
    {
        using var w = new World();
        var path = w.Src.Write(name, "<x />");
        AssertCode(w.Importer.Check(path), TemplateImportProblemCode.NotAdmx);
    }

    [TestMethod]
    public void C5_ASourceFolderThatIsAJunction_IsSourceReparsePoint()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        var link = Path.Combine(w.Host.Root, "srclink");
        w.Host.MakeJunction(link, w.Src.Root);
        AssertCode(w.Importer.Check(Path.Combine(link, "a.admx")), TemplateImportProblemCode.SourceReparsePoint);
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C5_ALanguageFolderThatIsAJunction_IsSourceReparsePoint()
    {
        using var real = new TempTemplates();
        using var w = new World();
        GoodTemplate(real, "a", "Blind.A");
        File.Copy(Path.Combine(real.Root, "a.admx"), Path.Combine(w.Src.Root, "a.admx"));
        w.Host.MakeJunction(Path.Combine(w.Src.Root, "en-US"), Path.Combine(real.Root, "en-US"));
        AssertCode(w.Importer.Check(w.SrcAdmx("a")), TemplateImportProblemCode.SourceReparsePoint);
    }

    [TestMethod]
    public void C6_ASourceOverTheCap_IsSourceTooLarge_WithoutReadingIt()
    {
        using var w = new World();
        var path = w.SrcAdmx("big");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(AdmxLimits.MaxFileBytes + 1);
        }

        var check = w.Importer.Check(path);
        AssertCode(check, TemplateImportProblemCode.SourceTooLarge);
        Assert.IsFalse(check.Problems.Any(p => p.Code == TemplateImportProblemCode.LoadFailed && p.LoadCode == TemplateFailureCode.MalformedXml));
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C7_ASourceThatCannotBeRead_IsSourceUnreadable_WithAnHresult()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        using var hold = new FileStream(w.SrcAdmx("a"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var check = w.Importer.Check(w.SrcAdmx("a"));
        AssertCode(check, TemplateImportProblemCode.SourceUnreadable);
        Assert.IsFalse(string.IsNullOrEmpty(check.Problems.First(p => p.Code == TemplateImportProblemCode.SourceUnreadable).Detail));
        Assert.IsNotNull(hold);
    }

    [TestMethod]
    public void C8_ABadAdmx_IsLoadFailed_WithTheLoadersCode()
    {
        using var w = new World();
        var cases = new (string Name, string Text, TemplateFailureCode Code)[]
        {
            ("malformed", "<policyDefinitions><oops></policyDefinitions>", TemplateFailureCode.MalformedXml),
            ("dtd", "<?xml version=\"1.0\"?><!DOCTYPE policyDefinitions [<!ENTITY e \"x\">]><policyDefinitions />", TemplateFailureCode.DtdProhibited),
            ("notatemplate", "<?xml version=\"1.0\"?><other />", TemplateFailureCode.NotATemplate),
            ("deep", AdmxText("Blind.Deep", "t", string.Concat(Enumerable.Repeat("<n>", 150)) + string.Concat(Enumerable.Repeat("</n>", 150))), TemplateFailureCode.XmlTooDeep),
        };
        foreach (var (name, text, code) in cases)
        {
            w.Src.Write(name + ".admx", text);
            w.Src.Write(Path.Combine("en-US", name + ".adml"), AdmlText(""));
            var check = w.Importer.Check(w.SrcAdmx(name));
            AssertCode(check, TemplateImportProblemCode.LoadFailed);
            Assert.IsTrue(check.Problems.Any(p => p.Code == TemplateImportProblemCode.LoadFailed && p.LoadCode == code),
                name + ": " + string.Join(",", check.Problems.Select(p => p.Code + "/" + p.LoadCode)));
        }

        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C9_NoAdmlAtAll_IsAdmlMissing()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        Directory.Delete(Path.Combine(w.Src.Root, "en-US"), true);
        AssertCode(w.Importer.Check(w.SrcAdmx("a")), TemplateImportProblemCode.AdmlMissing);
    }

    [TestMethod]
    public void C10_AnEnUsOnlyAdml_WithAnotherUiCulture_IsNotAProblem_AndIsCopiedToEnUs()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        var check = w.Importer.Check(w.SrcAdmx("a"), "de-DE");
        Assert.IsTrue(check.CanImport, string.Join(",", Problems(check)));
        Assert.AreEqual("en-US", check.Culture, true);
        var result = w.Importer.Import(w.SrcAdmx("a"), "de-DE");
        Assert.IsTrue(result.Imported, string.Join(",", result.Problems.Select(p => p.Code)));
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "en-US", "a.adml")));
        Assert.IsFalse(Directory.Exists(Path.Combine(w.Store, "de-DE")));
    }

    [TestMethod]
    public void C10_TheRequestedLanguageAdml_IsCopiedWhenItExists()
    {
        using var w = new World();
        Std(w.Src, "a", "Blind.A", P("Pol"), ["Pol"], culture: "de-DE");
        var result = w.Importer.Import(w.SrcAdmx("a"), "de-DE");
        Assert.IsTrue(result.Imported, string.Join(",", result.Problems.Select(p => p.Code)));
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "de-DE", "a.adml")));
    }

    [TestMethod]
    public void C11_ABadAdml_IsLoadFailed()
    {
        using var w = new World();
        Std(w.Src, "a", "Blind.A", P("Pol", attrs: "explainText=\"$(string.Missing)\""), ["Pol"]);
        var check = w.Importer.Check(w.SrcAdmx("a"));
        AssertCode(check, TemplateImportProblemCode.LoadFailed);
        Assert.IsTrue(check.Problems.Any(p => p.LoadCode == TemplateFailureCode.UndefinedString));
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C12_C19_ANamespaceTheSystemDefines_IsNamespaceDefinedBySystem_AndNothingIsCopied()
    {
        using var w = new World();
        GoodTemplate(w.Sys, "sys", "Blind.Same");
        GoodTemplate(w.Src, "other", "Blind.Same");
        AssertCode(w.Importer.Check(w.SrcAdmx("other")), TemplateImportProblemCode.NamespaceDefinedBySystem);
        Assert.IsFalse(w.Importer.Import(w.SrcAdmx("other")).Imported);
        Assert.IsFalse(Directory.Exists(w.Store));
        // The system folder itself as the source.
        AssertCode(w.Importer.Check(Path.Combine(w.Sys.Root, "sys.admx")), TemplateImportProblemCode.NamespaceDefinedBySystem);
    }

    [TestMethod]
    public void C13_G3_ASecondImportOfANamespace_IsNamespaceAlreadyImported_AndTheStoreIsUnchanged()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("a")).Imported);
        var before = StoreFiles(w.Store);
        var admxBefore = Bytes(Path.Combine(w.Store, "a.admx"));
        var again = w.Importer.Import(w.SrcAdmx("a"));
        Assert.IsFalse(again.Imported);
        Assert.IsTrue(again.Problems.Any(p => p.Code == TemplateImportProblemCode.NamespaceAlreadyImported));
        CollectionAssert.AreEqual(before, StoreFiles(w.Store));
        CollectionAssert.AreEqual(admxBefore, Bytes(Path.Combine(w.Store, "a.admx")));
        // The store itself as the source.
        AssertCode(w.Importer.Check(Path.Combine(w.Store, "a.admx")), TemplateImportProblemCode.NamespaceAlreadyImported);
    }

    [TestMethod]
    public void C12_C13_TheNamespaceChecksIgnoreCase_AndFileNamesDoToo()
    {
        using var w = new World();
        using var other = new TempTemplates();
        using var third = new TempTemplates();
        GoodTemplate(w.Sys, "sys", "Blind.Sys");
        GoodTemplate(w.Src, "a", "Blind.A");
        GoodTemplate(other, "up", "BLIND.SYS");
        GoodTemplate(third, "A", "Blind.Other");
        AssertCode(w.Importer.Check(Path.Combine(other.Root, "up.admx")), TemplateImportProblemCode.NamespaceDefinedBySystem);
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("a")).Imported);
        GoodTemplate(other, "again", "blind.a");
        AssertCode(w.Importer.Check(Path.Combine(other.Root, "again.admx")), TemplateImportProblemCode.NamespaceAlreadyImported);
        AssertCode(w.Importer.Check(Path.Combine(third.Root, "A.admx")), TemplateImportProblemCode.FileNameInUse);
    }

    [TestMethod]
    public void C14_ADifferentTemplateWithTheSameFileName_IsFileNameInUse()
    {
        using var w = new World();
        using var other = new TempTemplates();
        GoodTemplate(w.Src, "a", "Blind.A");
        GoodTemplate(other, "a", "Blind.B");
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("a")).Imported);
        var before = Bytes(Path.Combine(w.Store, "a.admx"));
        var check = w.Importer.Check(Path.Combine(other.Root, "a.admx"));
        AssertCode(check, TemplateImportProblemCode.FileNameInUse);
        CollectionAssert.AreEqual(before, Bytes(Path.Combine(w.Store, "a.admx")));
    }

    [TestMethod]
    public void C15_AUsingThatOnlyANeighbourDefines_IsUsingNotInstalled_WithTheNamespace()
    {
        using var w = new World();
        GoodTemplate(w.Src, "base", "Blind.Base");
        Std(w.Src, "child", "Blind.Child", P("PChild"), ["PChild"], usings: Using("b", "Blind.Base"));
        var check = w.Importer.Check(w.SrcAdmx("child"));
        AssertCode(check, TemplateImportProblemCode.UsingNotInstalled);
        Assert.AreEqual("Blind.Base", check.Problems.First(p => p.Code == TemplateImportProblemCode.UsingNotInstalled).Detail, true);
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C15_AUsingThatTheSystemFolderDefines_IsFine()
    {
        using var w = new World();
        GoodTemplate(w.Sys, "base", "Blind.Base");
        Std(w.Src, "child", "Blind.Child", P("PChild", cat: "b:Root"), ["PChild"], usings: Using("b", "Blind.Base"));
        var check = w.Importer.Check(w.SrcAdmx("child"));
        Assert.IsTrue(check.CanImport, string.Join(",", Problems(check)));
        Assert.AreEqual("Blind.Child", check.Namespace, true);
        Assert.AreEqual("child.admx", check.AdmxFileName, true);
    }

    [TestMethod]
    public void C16_AUsingThatNothingDefines_IsLoadFailedUndefinedUsing()
    {
        using var w = new World();
        Std(w.Src, "child", "Blind.Child", P("PChild"), ["PChild"], usings: Using("g", "Blind.Ghost"));
        var check = w.Importer.Check(w.SrcAdmx("child"));
        AssertCode(check, TemplateImportProblemCode.LoadFailed);
        Assert.IsTrue(check.Problems.Any(p => p.LoadCode == TemplateFailureCode.UndefinedUsing));
    }

    [TestMethod]
    public void C18_ABadNeighbourFile_DoesNotBlockTheImport()
    {
        using var w = new World();
        GoodTemplate(w.Src, "good", "Blind.Good");
        w.Src.Write("neighbour.admx", "<not xml");
        var check = w.Importer.Check(w.SrcAdmx("good"));
        Assert.IsTrue(check.CanImport, string.Join(",", check.Problems.Select(p => p.Code + "/" + p.LoadCode)));
    }

    [TestMethod]
    public void C20_ACancelledToken_Throws_AndWritesNothing()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => w.Importer.Check(w.SrcAdmx("a"), null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => w.Importer.Import(w.SrcAdmx("a"), null, cts.Token));
        Assert.IsFalse(Directory.Exists(w.Store));
    }

    [TestMethod]
    public void C21_AnUnusableStore_IsStoreUnavailable_BeforeAnyCopy()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        File.WriteAllText(w.Store, "i am a file");
        var result = w.Importer.Import(w.SrcAdmx("a"));
        Assert.IsFalse(result.Imported);
        Assert.IsTrue(result.Problems.Any(p => p.Code == TemplateImportProblemCode.StoreUnavailable), string.Join(",", result.Problems.Select(p => p.Code)));
        Assert.IsTrue(File.Exists(w.SrcAdmx("a")), "the source is untouched");
    }

    // ---------------------------------------------------------------- Import and remove, end to end

    [TestMethod]
    public void Import_CopiesBytes_NormalizesTheExtension_AndTheTemplateLoadsFromTheStore()
    {
        using var w = new World();
        GoodTemplate(w.Src, "Foo", "Blind.Foo");
        File.Move(w.SrcAdmx("Foo"), Path.Combine(w.Src.Root, "Foo.ADMX"));
        var source = Path.Combine(w.Src.Root, "Foo.ADMX");
        var check = w.Importer.Check(source);
        Assert.IsTrue(check.CanImport, string.Join(",", Problems(check)));
        Assert.IsFalse(Directory.Exists(w.Store), "a check writes nothing, not even the store folder");
        var result = w.Importer.Import(source);
        Assert.IsTrue(result.Imported, string.Join(",", result.Problems.Select(p => p.Code)));
        Assert.AreEqual("Blind.Foo", result.Namespace, true);
        Assert.AreEqual(Path.Combine(w.Store, "Foo.admx"), result.AdmxPath, true);
        Assert.AreEqual(Path.Combine(w.Store, "en-US", "Foo.adml"), result.AdmlPath, true);
        CollectionAssert.AreEqual(Bytes(source), Bytes(result.AdmxPath!));
        CollectionAssert.AreEqual(Bytes(Path.Combine(w.Src.Root, "en-US", "Foo.adml")), Bytes(result.AdmlPath!));
        Assert.IsTrue(Path.GetFileName(Directory.GetFiles(w.Store).Single()).EndsWith(".admx", StringComparison.Ordinal), "lowercase extension");
        CollectionAssert.AreEqual(new[] { "en-US", Path.Combine("en-US", "Foo.adml"), "Foo.admx" }.Order().ToArray(), StoreFiles(w.Store).ToArray());
        var loaded = w.Sources.Load();
        FailureAssert.None(loaded.Set);
        Assert.IsNotNull(loaded.Set.FindPolicy("Blind.Foo", "Pol"));
        Assert.AreEqual(1, loaded.ImportedFiles.Count);
    }

    [TestMethod]
    public void G4_G8_ImportRemoveImport_LeavesNoResidue_AndTheNamespaceCompareIgnoresCase()
    {
        using var w = new World();
        GoodTemplate(w.Src, "a", "Blind.A");
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("a")).Imported);
        var removed = w.Importer.Remove("BLIND.a");
        Assert.IsTrue(removed.Removed, string.Join(",", removed.Problems.Select(p => p.Code)));
        Assert.AreEqual(2, removed.RemovedPaths.Count);
        CollectionAssert.AreEqual(new[] { "en-US" }, StoreFiles(w.Store).Where(f => !f.Contains('\\')).ToArray());
        Assert.AreEqual(0, StoreFiles(w.Store).Count(f => f.EndsWith(".admx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".adml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("a")).Imported);
    }

    [TestMethod]
    public void E1_RemovingASystemOrUnknownNamespace_IsNotImported_AndTheSystemFileStays()
    {
        using var w = new World();
        GoodTemplate(w.Sys, "sys", "Blind.Sys");
        GoodTemplate(w.Src, "a", "Blind.A");
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("a")).Imported);
        foreach (var ns in new[] { "Blind.Sys", "Blind.Unknown" })
        {
            var result = w.Importer.Remove(ns);
            Assert.IsFalse(result.Removed, ns);
            Assert.IsTrue(result.Problems.Any(p => p.Code == TemplateImportProblemCode.NotImported), ns);
            Assert.AreEqual(0, result.RemovedPaths.Count);
        }

        Assert.IsTrue(File.Exists(Path.Combine(w.Sys.Root, "sys.admx")));
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "a.admx")));
    }

    [TestMethod]
    public void E2_ATemplateAnotherImportUses_CannotBeRemoved_AndNothingIsDeleted()
    {
        using var w = new World();
        GoodTemplate(w.Src, "base", "Blind.Base");
        Std(w.Src, "child", "Blind.Child", P("PChild", cat: "b:Root"), ["PChild"], usings: Using("b", "Blind.Base"));
        Assert.IsTrue(w.Importer.Import(w.SrcAdmx("base")).Imported);
        var child = w.Importer.Import(w.SrcAdmx("child"));
        Assert.IsTrue(child.Imported, string.Join(",", child.Problems.Select(p => p.Code)));
        var result = w.Importer.Remove("Blind.Base");
        Assert.IsFalse(result.Removed);
        var problem = result.Problems.Single(p => p.Code == TemplateImportProblemCode.UsedByTemplate);
        StringAssert.Contains(problem.Detail + problem.Path, "child", StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "base.admx")));
        Assert.IsTrue(File.Exists(Path.Combine(w.Store, "en-US", "base.adml")));
    }

    [TestMethod]
    public void E7_RemoveFile_WorksForAFileThatDoesNotLoad_AndRefusesUnsafeAndUnknownNames()
    {
        using var w = new World();
        w.Writer.EnsureStore();
        File.WriteAllText(Path.Combine(w.Store, "broken.admx"), "<not xml");
        Directory.CreateDirectory(Path.Combine(w.Store, "en-US"));
        File.WriteAllText(Path.Combine(w.Store, "en-US", "broken.adml"), "<not xml");
        var removed = w.Importer.RemoveFile("broken.admx");
        Assert.IsTrue(removed.Removed, string.Join(",", removed.Problems.Select(p => p.Code)));
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "broken.admx")));
        Assert.IsFalse(File.Exists(Path.Combine(w.Store, "en-US", "broken.adml")));

        var outside = Path.Combine(w.Host.Root, "outside.admx");
        File.WriteAllText(outside, "keep");
        foreach (var name in new[] { @"..\outside.admx", "a/b.admx", "x.txt", "NUL.admx" })
        {
            var result = w.Importer.RemoveFile(name);
            Assert.IsFalse(result.Removed, name);
            Assert.IsTrue(result.Problems.Any(p => p.Code == TemplateImportProblemCode.UnsafeFileName), name + ": " + string.Join(",", result.Problems.Select(p => p.Code)));
        }

        Assert.IsTrue(File.Exists(outside), "nothing outside the store is deleted");
        var missing = w.Importer.RemoveFile("nothere.admx");
        Assert.IsTrue(missing.Problems.Any(p => p.Code == TemplateImportProblemCode.NotImported));
    }
}
