using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

/// <summary>
/// Template files that classic refuses (docs/failure-modes/policy-admx.md, sections 35 and 36): a list item with no
/// key, a boolean with no value name, and the rule that a refused file adds nothing to the set.
/// </summary>
[TestClass]
public sealed class StrictTemplateTests
{
    private const string PolicyStart = """<policy name="P" class="Machine" displayName="$(string.P)" key="K">""";
    private const string PolicyEnd = """<parentCategory ref="Root" /></policy>""";
    private const string PolicyString = """<string id="P">P</string>""";

    /// <summary>
    /// The one-based line and column of the angle bracket that closes the start tag of the Nth element that starts with
    /// <paramref name="needle"/>. Classic reports this position (confirmed by two captures: column 37 for an 8 space indent
    /// plus a 29 character tag, column 32 for 8 spaces plus a 24 character tag). A tag may span lines.
    /// </summary>
    private static (int Line, int Position) PositionOfNth(string path, string needle, int occurrence = 1)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var index = -1;
        for (var i = 0; i < occurrence; i++)
        {
            index = text.IndexOf(needle, index + 1, StringComparison.Ordinal);
            Assert.IsTrue(index >= 0, needle);
        }

        var quote = '\0';
        var end = index;
        for (; end < text.Length; end++)
        {
            var c = text[end];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                break;
            }
        }

        var before = text[..end];
        var line = before.Count(c => c == '\n') + 1;
        var position = end - (before.LastIndexOf('\n') + 1) + 1;
        return (line, position);
    }

    private static void AssertNothingLoaded(TemplateSet set)
    {
        Assert.AreEqual(0, set.Files.Count, "files");
        Assert.AreEqual(0, set.Categories.Count, "categories");
        Assert.AreEqual(0, set.Policies.Count, "policies");
        Assert.AreEqual(0, set.SupportedOn.Count, "supportedOn");
    }

    private static TemplateSet LoadPolicy(string policyInner, string elements = "")
    {
        using var temp = new TempTemplates();
        temp.AddSimple("One", "T.One", PolicyStart + elements + policyInner + PolicyEnd, PolicyString);
        return temp.Load();
    }

    private static string ListXml(string kind, string items, string defaultKey = "") =>
        kind switch
        {
            "enabledList" or "disabledList" => $"<{kind}{defaultKey}>{items}</{kind}>",
            _ => string.Empty,
        };

    private static TemplateSet LoadListKind(string kind, string listAttributes, string items)
    {
        if (kind is "enabledList" or "disabledList")
        {
            return LoadPolicy(ListXml(kind, items, listAttributes));
        }

        var other = kind == "trueList" ? "falseList" : "trueList";
        var boolean = $"""<elements><boolean id="B" valueName="V"><{kind}{listAttributes}>{items}</{kind}><{other}><item key="K" valueName="N"><value><decimal value="0" /></value></item></{other}></boolean></elements>""";
        return LoadPolicy(string.Empty, boolean);
    }

    private const string ItemNoKey = """<item valueName="N"><value><decimal value="1" /></value></item>""";
    private const string ItemWithKey = """<item key="K\Sub" valueName="N"><value><decimal value="1" /></value></item>""";

    [TestMethod]
    public void ListItemNoKeyFixture_FailsWithTheAttributeFileLineAndPosition_AndShowsNothing()
    {
        var set = TemplateLoader.Load([Fixture.Bad("ListItemNoKey")], "en-US");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual(1, set.Failures.Count, FailureAssert.Describe(set));
        var path = Path.Combine(Fixture.Bad("ListItemNoKey"), "BwcListItemNoKey.admx");
        Assert.AreEqual("key", failure.Detail);
        Assert.AreEqual(path, failure.FilePath);
        var (line, position) = PositionOfNth(path, "<item ");
        Assert.AreEqual(line, failure.Line);
        Assert.AreEqual(position, failure.Position);
        Assert.AreEqual((21, 39), (failure.Line, failure.Position));
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void BooleanNoValueNameFixture_FailsWithTheAttributeFileLineAndPosition_AndShowsNothing()
    {
        var set = TemplateLoader.Load([Fixture.Bad("BooleanNoValueName")], "en-US");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual(1, set.Failures.Count, FailureAssert.Describe(set));
        var path = Path.Combine(Fixture.Bad("BooleanNoValueName"), "BwcBooleanNoValueName.admx");
        Assert.AreEqual("valueName", failure.Detail);
        Assert.AreEqual(path, failure.FilePath);
        var (line, position) = PositionOfNth(path, "<boolean ");
        Assert.AreEqual(line, failure.Line);
        Assert.AreEqual(position, failure.Position);
        Assert.AreEqual((21, 31), (failure.Line, failure.Position));
        AssertNothingLoaded(set);
    }

    [TestMethod]
    [DataRow("enabledList")]
    [DataRow("disabledList")]
    [DataRow("trueList")]
    [DataRow("falseList")]
    public void ListItemNoKey_WithoutDefaultKey_IsMissingAttributeKey_ForEveryListKind(string kind)
    {
        var set = LoadListKind(kind, string.Empty, ItemNoKey);
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual(1, set.Failures.Count, FailureAssert.Describe(set));
        Assert.AreEqual("key", failure.Detail);
        Assert.IsTrue(failure.Line > 0 && failure.Position > 0);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    [DataRow("enabledList")]
    [DataRow("disabledList")]
    [DataRow("trueList")]
    [DataRow("falseList")]
    public void ListItemNoKey_WithDefaultKeyOnItsOwnList_Loads(string kind)
    {
        var set = LoadListKind(kind, """ defaultKey="K\Default" """, ItemNoKey);
        FailureAssert.None(set);
        var policy = set.Policies.Single();
        var list = kind switch
        {
            "enabledList" => policy.EnabledList,
            "disabledList" => policy.DisabledList,
            "trueList" => policy.Elements.OfType<BooleanElement>().Single().TrueList,
            _ => policy.Elements.OfType<BooleanElement>().Single().FalseList,
        };
        Assert.AreEqual("K\\Default", list!.Items.Single().EffectiveKey);
    }

    [TestMethod]
    [DataRow("enabledList")]
    [DataRow("disabledList")]
    [DataRow("trueList")]
    [DataRow("falseList")]
    public void ListItemWithItsOwnKey_Loads(string kind)
    {
        var set = LoadListKind(kind, string.Empty, ItemWithKey);
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Policies.Count);
    }

    [TestMethod]
    public void DefaultKeyOnTheOtherList_DoesNotRescueAnItem()
    {
        var set = LoadPolicy(
            $"""<enabledList>{ItemNoKey}</enabledList><disabledList defaultKey="K\Default">{ItemWithKey}</disabledList>""");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual("key", failure.Detail);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void ElementKeyAndPolicyKey_DoNotRescueAnItem()
    {
        var boolean = $"""<elements><boolean id="B" key="K\Element" valueName="V"><trueList>{ItemNoKey}</trueList></boolean></elements>""";
        var set = LoadPolicy(string.Empty, boolean);
        Assert.AreEqual("key", FailureAssert.Single(set, TemplateFailureCode.MissingAttribute).Detail);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void EveryItemWithoutAKey_IsOneFailureInDocumentOrder()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            $"""
            {PolicyStart}
              <enabledList>
                {ItemWithKey}
                {ItemNoKey}
              </enabledList>
              <disabledList>
                {ItemNoKey}
              </disabledList>
            {PolicyEnd}
            """,
            PolicyString);
        var set = temp.Load();
        var failures = set.Failures.Where(f => f.Code == TemplateFailureCode.MissingAttribute).ToList();
        Assert.AreEqual(2, failures.Count, FailureAssert.Describe(set));
        Assert.IsTrue(failures.All(f => f.Detail == "key"));
        Assert.IsTrue(failures[0].Line < failures[1].Line);
        var path = Path.Combine(temp.Root, "One.admx");
        Assert.AreEqual(PositionOfNth(path, "<item ", 2), (failures[0].Line, failures[0].Position));
        Assert.AreEqual(PositionOfNth(path, "<item ", 3), (failures[1].Line, failures[1].Position));
        Assert.IsTrue(failures.All(f => f.FilePath == path));
    }

    [TestMethod]
    public void BooleanWithoutValueName_IsMissingAttributeValueName_WithItsPosition()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            $"""
            {PolicyStart}
              <elements>
                <boolean id="B" />
              </elements>
            {PolicyEnd}
            """,
            PolicyString);
        var set = temp.Load();
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual(1, set.Failures.Count, FailureAssert.Describe(set));
        Assert.AreEqual("valueName", failure.Detail);
        var path = Path.Combine(temp.Root, "One.admx");
        Assert.AreEqual(path, failure.FilePath);
        Assert.AreEqual(PositionOfNth(path, "<boolean "), (failure.Line, failure.Position));
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void StartTagSpanningTwoLines_ReportsTheLineAndColumnOfTheClosingBracket()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            $"""
            {PolicyStart}
              <elements>
                <boolean id="B"
                         clientExtension="X">
                  <trueList>
                    <item
                      valueName="N"
                      >
                      <value><decimal value="1" /></value>
                    </item>
                  </trueList>
                </boolean>
              </elements>
            {PolicyEnd}
            """,
            PolicyString);
        var set = temp.Load();
        var path = Path.Combine(temp.Root, "One.admx");
        var failures = set.Failures.Where(f => f.Code == TemplateFailureCode.MissingAttribute).ToList();
        Assert.AreEqual(2, failures.Count, FailureAssert.Describe(set));
        var boolean = failures.Single(f => f.Detail == "valueName");
        var item = failures.Single(f => f.Detail == "key");
        Assert.AreEqual(PositionOfNth(path, "<boolean "), (boolean.Line, boolean.Position));
        Assert.AreEqual(PositionOfNth(path, "<item"), (item.Line, item.Position));
        var lines = File.ReadAllLines(path);
        var booleanRow = Array.FindIndex(lines, l => l.Contains("<boolean id=", StringComparison.Ordinal));
        var itemRow = Array.FindIndex(lines, l => l.TrimEnd() == "<item".PadLeft(l.TrimEnd().Length));
        Assert.AreEqual(booleanRow + 2, boolean.Line, "the boolean tag closes on its second line");
        Assert.AreEqual(itemRow + 3, item.Line, "the item tag closes on its third line");
        Assert.AreEqual(lines[booleanRow + 1].IndexOf('>') + 1, boolean.Position);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void SelfClosingBoolean_ReportsTheColumnOfTheClosingBracket()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            $"""
            {PolicyStart}
              <elements>
                <boolean id="B" />
              </elements>
            {PolicyEnd}
            """,
            PolicyString);
        var set = temp.Load();
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        var text = File.ReadAllLines(Path.Combine(temp.Root, "One.admx"));
        var row = Array.FindIndex(text, l => l.Contains("<boolean id=\"B\" />", StringComparison.Ordinal));
        Assert.AreEqual(row + 1, failure.Line);
        Assert.AreEqual(text[row].IndexOf('>') + 1, failure.Position);
    }

    [TestMethod]
    public void BooleanWithTrueValueAndFalseValueOnly_StillNeedsAValueName()
    {
        var set = LoadPolicy(
            string.Empty,
            """<elements><boolean id="B"><trueValue><decimal value="1" /></trueValue><falseValue><decimal value="0" /></falseValue></boolean></elements>""");
        Assert.AreEqual("valueName", FailureAssert.Single(set, TemplateFailureCode.MissingAttribute).Detail);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    [DataRow("""valueName="V" """)]
    [DataRow("""valueName="" """)]
    public void BooleanWithAValueName_Loads(string attribute)
    {
        var set = LoadPolicy(string.Empty, $"""<elements><boolean id="B" {attribute}/></elements>""");
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Policies.Count);
    }

    [TestMethod]
    public void NoOtherStrictness_OtherElementsAndLists_StayAcceptedWithoutValueNamesOrKeys()
    {
        // No capture shows classic refusing any of these, so each must load without a failure.
        var set = LoadPolicy(
            string.Empty,
            """
            <elements>
              <decimal id="D" />
              <longDecimal id="LD" />
              <text id="T" />
              <multiText id="M" />
              <list id="L" key="K\L" />
              <enum id="E">
                <item displayName="Item"><value><decimal value="1" /></value><valueList><item valueName="N"><value><decimal value="1" /></value></item></valueList></item>
              </enum>
              <boolean id="B" valueName="V" />
            </elements>
            """);
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual(7, set.Policies[0].Elements.Count);
    }

    [TestMethod]
    public void NoOtherStrictness_APolicyWithoutAValueName_Loads()
    {
        var set = LoadPolicy(string.Empty);
        FailureAssert.None(set);
        Assert.IsNull(set.Policies.Single().ValueName);
    }

    [TestMethod]
    public void AFailedFile_WithAGoodPolicyBeforeTheBadOne_AddsNothing()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            """
            <policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><supportedOn ref="SUP" /></policy>
            <policy name="Dup" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            <policy name="Dup" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            """,
            PolicyString);
        var set = temp.Load();
        FailureAssert.Single(set, TemplateFailureCode.DuplicatePolicy);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void AFailedFile_WithAnUndefinedString_AddsNothing()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            """
            <policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            <policy name="Bad" class="Machine" displayName="$(string.Nope)" key="K"><parentCategory ref="Root" /></policy>
            """,
            PolicyString);
        var set = temp.Load();
        Assert.AreEqual("Nope", FailureAssert.Single(set, TemplateFailureCode.UndefinedString).Detail);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void AFailedFile_WithABadAdml_AddsNothing()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "One",
            "T.One",
            """<policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""",
            PolicyString + PolicyString);
        var set = temp.Load();
        FailureAssert.Single(set, TemplateFailureCode.DuplicateString);
        AssertNothingLoaded(set);
    }

    [TestMethod]
    public void AGoodSibling_KeepsEverything_AndNoFailureNamesIt()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "A",
            "T.A",
            """<policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><supportedOn ref="SUP" /></policy>""",
            PolicyString);
        temp.AddSimple("B", "T.B", PolicyStart + ItemNoKeyList() + PolicyEnd, PolicyString);
        temp.AddSimple(
            "C",
            "T.C",
            """<policy name="Good" class="User" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""",
            PolicyString);
        var set = temp.Load();
        Assert.AreEqual(2, set.Files.Count);
        Assert.AreEqual(2, set.Policies.Count);
        Assert.AreEqual(2, set.Categories.Count);
        Assert.AreEqual(2, set.SupportedOn.Count);
        Assert.IsNotNull(set.FindPolicy("T.A", "Good")!.Category);
        Assert.IsNotNull(set.FindPolicy("T.A", "Good")!.SupportedOn);
        Assert.IsNull(set.FindFile("T.B"));
        Assert.IsTrue(set.Failures.All(f => f.FilePath == Path.Combine(temp.Root, "B.admx")), FailureAssert.Describe(set));
    }

    [TestMethod]
    public void AGoodFileInAnotherFolder_StillLoads()
    {
        var set = TemplateLoader.Load([Fixture.Bad("ListItemNoKey"), Fixture.Bad("BooleanNoValueName"), Fixture.Root], "en-US");
        var good = Fixture.Load();
        Assert.AreEqual(good.Files.Count, set.Files.Count);
        Assert.AreEqual(good.Policies.Count, set.Policies.Count);
        Assert.AreEqual(good.Categories.Count, set.Categories.Count);
        Assert.AreEqual(good.SupportedOn.Count, set.SupportedOn.Count);
        CollectionAssert.AreEqual(
            new[] { TemplateFailureCode.MissingAttribute, TemplateFailureCode.MissingAttribute },
            set.Failures.Select(f => f.Code).ToArray());
    }

    [TestMethod]
    public void AFailedFilesCategoryAndSupportedOn_AreNotFoundByAReferenceFromAnotherFile()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("Bad", "T.Bad", PolicyStart + ItemNoKeyList() + PolicyEnd, PolicyString);
        temp.Add(
            "User",
            "T.User",
            "u",
            """
            <categories><category name="Own" displayName="$(string.Own)"><parentCategory ref="bad:Root" /></category></categories>
            <policies><policy name="Q" class="Machine" displayName="$(string.Own)" key="K"><parentCategory ref="bad:Root" /><supportedOn ref="bad:SUP" /></policy></policies>
            """,
            """<string id="Own">Own</string>""",
            usings: """<using prefix="bad" namespace="T.Bad" />""");
        var set = temp.Load();
        Assert.IsNull(set.FindCategory("T.Bad", "Root"));
        Assert.IsNull(set.FindSupportedOn("T.Bad", "SUP"));
        Assert.IsNull(set.Policies.Single().Category);
        Assert.IsNull(set.Policies.Single().SupportedOn);
        Assert.IsTrue(set.Failures.Any(f => f.Code == TemplateFailureCode.UndefinedCategory));
        Assert.IsTrue(set.Failures.Any(f => f.Code == TemplateFailureCode.UndefinedSupportedOn));
        Assert.IsTrue(set.Failures.Any(f => f.Code == TemplateFailureCode.UndefinedUsing));
    }

    [TestMethod]
    public void ADanglingReference_DoesNotRejectItsFile()
    {
        using var temp = new TempTemplates();
        temp.Add(
            "One",
            "T.One",
            "t",
            """
            <supportedOn><definitions><definition name="SUP" displayName="$(string.P)" /></definitions></supportedOn>
            <categories><category name="Root" displayName="$(string.P)"><parentCategory ref="Missing" /></category></categories>
            <policies><policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><supportedOn ref="Nowhere" /></policy></policies>
            """,
            PolicyString,
            usings: """<using prefix="ghost" namespace="T.Ghost" />""");
        var set = temp.Load();
        Assert.AreEqual(1, set.Files.Count);
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual(1, set.Categories.Count);
        Assert.AreEqual(1, set.SupportedOn.Count);
        Assert.IsTrue(set.Failures.Any(f => f.Code == TemplateFailureCode.UndefinedUsing));
        Assert.IsTrue(set.Failures.Any(f => f.Code == TemplateFailureCode.UndefinedCategory));
        Assert.IsTrue(set.Failures.Any(f => f.Code == TemplateFailureCode.UndefinedSupportedOn));
    }

    private static string ItemNoKeyList() => $"<enabledList>{ItemNoKey}</enabledList>";
}
