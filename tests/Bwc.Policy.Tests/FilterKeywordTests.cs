using Bwc.Policy.Filter;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.FilterTestSupport;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class FilterKeywordTests
{
    private static bool Hit(KeywordFilter keyword, string policy = "P1", string? comment = null) =>
        Matches(Options(keyword: keyword), Subject(policy, comment: comment));

    [TestMethod]
    public void Within_TitleHelpAndCommentAreSearchedOnlyWhenTicked()
    {
        Assert.IsTrue(Hit(Words("Firewall", help: false, comment: false)), "title");
        Assert.IsFalse(Hit(Words("network", title: false, help: false, comment: true)), "help text is not ticked");
        Assert.IsTrue(Hit(Words("network", title: false, help: true, comment: false)), "help");
        Assert.IsFalse(Hit(Words("Alpha", title: false, help: true, comment: true)), "the title is not ticked");
    }

    [TestMethod]
    public void Within_ACommentOnlyHitNeedsTheCommentBox()
    {
        Assert.IsFalse(Hit(Words("zebra", comment: false), comment: "the zebra rule"));
        Assert.IsTrue(Hit(Words("zebra", comment: true), comment: "the zebra rule"));
        Assert.IsFalse(Hit(Words("zebra", title: false, help: false, comment: true), comment: null));
        Assert.IsFalse(Hit(Words("zebra", title: false, help: false, comment: true), comment: string.Empty));
    }

    [TestMethod]
    public void Help_APolicyWithNoHelpTextNeverCrashesOrMatchesByHelp()
    {
        Assert.AreEqual(string.Empty, P("P2").ExplainText);
        Assert.IsFalse(Hit(Words("anything", title: false, help: true, comment: false), "P2"));
        Assert.IsTrue(Hit(Words("printer", title: true, help: true, comment: false), "P2"));
        Assert.IsFalse(Hit(Words("printer", title: false, help: true, comment: false), "P2"));
    }

    [TestMethod]
    public void Case_IsIgnored()
    {
        Assert.IsTrue(Hit(Words("fIREWALL")));
        Assert.IsTrue(Hit(Words("ALPHA FIREWALL", KeywordMatchMode.Exact)));
        Assert.IsTrue(FilterRules.KeywordIgnoresCase);
    }

    [TestMethod]
    public void Words_SplitOnAnyWhiteSpace()
    {
        Assert.IsTrue(Hit(Words("zzz\t\r\n  firewall")));
        Assert.IsTrue(Hit(Words("  firewall  ")));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, FilterRules.SplitWords(" a\tb \n c ").ToArray());
    }

    [TestMethod]
    public void Words_MatchInsideALongerWord()
    {
        Assert.IsTrue(Hit(Words("wall")), "wall is inside Firewall");
        Assert.IsTrue(Hit(Words("work", title: false, comment: false)), "work is inside network");
    }

    [TestMethod]
    public void Any_OneWordInOneTextIsEnough()
    {
        Assert.IsTrue(Hit(Words("zzz firewall", KeywordMatchMode.Any)));
        Assert.IsFalse(Hit(Words("zzz yyy", KeywordMatchMode.Any)));
    }

    [TestMethod]
    public void All_EveryWordMustBeFoundPossiblyInDifferentTexts()
    {
        Assert.IsTrue(Hit(Words("alpha network", KeywordMatchMode.All)), "title and help text");
        Assert.IsFalse(Hit(Words("alpha zzz", KeywordMatchMode.All)));
        Assert.IsTrue(Hit(Words("alpha zebra", KeywordMatchMode.All), comment: "zebra"));
        Assert.IsFalse(Hit(Words("alpha zebra", KeywordMatchMode.All, comment: false), comment: "zebra"));
    }

    [TestMethod]
    public void Exact_TheWholeTextIsOnePhrase()
    {
        Assert.IsTrue(Hit(Words("network firewall", KeywordMatchMode.Exact)));
        Assert.IsFalse(Hit(Words("firewall network", KeywordMatchMode.Exact)), "word order matters");
        Assert.IsFalse(Hit(Words("alpha network", KeywordMatchMode.Exact)), "the two words are in different texts");
    }

    [TestMethod]
    public void Exact_WhiteSpaceRunsCountAsOneSpaceAndPunctuationIsKept()
    {
        Assert.IsTrue(Hit(Words("alpha   beta", KeywordMatchMode.Exact), "P3"), "three spaces in the help text, three in the phrase");
        Assert.IsTrue(Hit(Words("alpha beta gamma", KeywordMatchMode.Exact), "P3"), "one space in the phrase, three in the text");
        Assert.IsTrue(Hit(Words("block all, inbound", KeywordMatchMode.Exact)));
        Assert.IsFalse(Hit(Words("block all inbound", KeywordMatchMode.Exact)), "the comma is part of the phrase");
        Assert.IsTrue(Hit(Words("  network firewall  ", KeywordMatchMode.Exact)), "the ends are trimmed");
    }

    [TestMethod]
    public void Keyword_OffPassesEverything()
    {
        Assert.IsTrue(Matches(Options(keyword: new KeywordFilter(false, "never found", KeywordMatchMode.All, true, true, true)), Subject("P1")));
    }

    [TestMethod]
    public void Keyword_ATurkishCultureDoesNotChangeTheResult()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.IsTrue(Hit(Words("FIREWALL")));
            Assert.IsFalse(Hit(Words("fırewall")), "a dotless i is a different letter, never folded");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void Keyword_AHugeHelpTextIsSearchedInLinearTime()
    {
        var huge = new string('a', 1_000_000) + " needle";
        Assert.IsTrue(Hit(Words("firewall"), "P1", comment: huge));
        Assert.IsTrue(Hit(Words("needle", title: false, help: false, comment: true), "P1", comment: huge));
    }

    [TestMethod]
    public void Keyword_StateDoesNotChangeTheTextMatch()
    {
        var options = Options(keyword: Words("firewall"));
        foreach (var state in new[] { PolicyState.Enabled, PolicyState.Disabled, PolicyState.NotConfigured })
        {
            Assert.IsTrue(Matches(options, Subject("P1", state)));
        }
    }
}
