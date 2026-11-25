using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>
/// A key that ends in a backslash (docs/failure-modes/policy-admx.md, section 46): the parser trims it, so the registry
/// layer accepts the key, the state reads Enabled and an edit writes the trimmed key.
/// </summary>
[TestClass]
public sealed class KeyBackslashTests
{
    private const string Ns = "T.Slash";
    private const string PolicyKey = @"Software\Policies\BwcSlash";
    private const string ElementKey = @"Software\Policies\BwcSlash\Elem";
    private const string ItemKey = @"Software\Policies\BwcSlash\Items";
    private const string DefaultKey = @"Software\Policies\BwcSlash\Def";

    // The policy key ends in one backslash, the element key in two, the item key in one and the default key in one.
    private const string Policy = """
        <policy name="Slash" class="Machine" displayName="$(string.Slash)" key="Software\Policies\BwcSlash\" valueName="Flag">
          <parentCategory ref="Root" />
          <enabledList defaultKey="Software\Policies\BwcSlash\Def\">
            <item key="Software\Policies\BwcSlash\Items\" valueName="ItemA"><value><decimal value="1" /></value></item>
            <item valueName="ItemB"><value><decimal value="2" /></value></item>
          </enabledList>
          <elements>
            <text id="Txt" key="Software\Policies\BwcSlash\Elem\\" valueName="Txt" />
            <boolean id="Chk" valueName="Chk">
              <trueList defaultKey="Software\Policies\BwcSlash\True\">
                <item valueName="T1"><value><decimal value="1" /></value></item>
              </trueList>
            </boolean>
          </elements>
        </policy>
        """;

    private static PolicyDefinition Load(TempTemplates temp)
    {
        temp.AddSimple("Slash", Ns, Policy, """<string id="Slash">Slash</string>""");
        var set = temp.Load();
        FailureAssert.None(set);
        return set.FindPolicy(Ns, "Slash") ?? throw new AssertFailedException("Policy missing.");
    }

    [TestMethod]
    public void PolicyElementItemAndDefaultKeys_LoadWithTheBackslashTrimmed()
    {
        using var temp = new TempTemplates();
        var policy = Load(temp);

        Assert.AreEqual(PolicyKey, policy.Key);
        var text = policy.Element<TextElement>("Txt");
        Assert.AreEqual(ElementKey, text.Key);
        Assert.AreEqual(ElementKey, text.EffectiveKey);
        var enabled = policy.EnabledList!;
        Assert.AreEqual(DefaultKey, enabled.DefaultKey);
        Assert.AreEqual(ItemKey, enabled.Items[0].Key);
        Assert.AreEqual(ItemKey, enabled.Items[0].EffectiveKey);
        Assert.IsNull(enabled.Items[1].Key);
        Assert.AreEqual(DefaultKey, enabled.Items[1].EffectiveKey);
        var trueList = policy.Element<BooleanElement>("Chk").TrueList!;
        Assert.AreEqual(@"Software\Policies\BwcSlash\True", trueList.DefaultKey);
        Assert.AreEqual(@"Software\Policies\BwcSlash\True", trueList.Items[0].EffectiveKey);
    }

    [TestMethod]
    public void StateReadsEnabled_OverADocumentThatHoldsTheTrimmedKeys()
    {
        using var temp = new TempTemplates();
        var policy = Load(temp);
        var document = Parse(
            RecDw(PolicyKey, "Flag", 1),
            RecDw(ItemKey, "ItemA", 1),
            RecDw(DefaultKey, "ItemB", 2),
            RecSz(ElementKey, "Txt", "hello"));

        var snapshot = PolicyStateReader.Read(policy, document);

        Assert.AreEqual(PolicyState.Enabled, snapshot.State);
        Assert.AreEqual(new TextValue("hello"), snapshot.Elements["Txt"]);
    }

    [TestMethod]
    public void Edit_Succeeds_AndWritesTheTrimmedKeys()
    {
        using var temp = new TempTemplates();
        var policy = Load(temp);
        var document = NewDocument();

        var result = PolicyStateWriter.Apply(
            policy, document, PolicyEdit.Enabled(("Txt", ElementValue.FromText("hi")), ("Chk", ElementValue.FromBoolean(true))));

        Assert.IsTrue(result.IsValid, string.Join(", ", result.Problems));
        Assert.IsTrue(result.Changed);
        Assert.IsNotNull(document.GetValue(PolicyKey, "Flag"));
        Assert.IsNotNull(document.GetValue(ItemKey, "ItemA"));
        Assert.IsNotNull(document.GetValue(DefaultKey, "ItemB"));
        Assert.IsNotNull(document.GetValue(ElementKey, "Txt"));
        Assert.IsNotNull(document.GetValue(@"Software\Policies\BwcSlash\True", "T1"));
        Assert.AreEqual(PolicyState.Enabled, PolicyStateReader.Read(policy, document).State);
    }

    [TestMethod]
    public void PolicyKeyOfOnlyBackslashes_IsAMissingKey()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Slash",
            Ns,
            """<policy name="P" class="Machine" displayName="$(string.P)" key="\\"><parentCategory ref="Root" /></policy>""",
            """<string id="P">P</string>""");

        var set = temp.Load();

        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual("key", failure.Detail);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void LeadingBackslashAndEmptyMiddleSegment_AreLeftUnchanged()
    {
        // Open oracle question (failure mode 46): only trailing backslashes are trimmed.
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Slash",
            Ns,
            """
            <policy name="Lead" class="Machine" displayName="$(string.P)" key="\A\B"><parentCategory ref="Root" /></policy>
            <policy name="Mid" class="Machine" displayName="$(string.P)" key="A\\B"><parentCategory ref="Root" /></policy>
            """,
            """<string id="P">P</string>""");

        var set = temp.Load();

        FailureAssert.None(set);
        Assert.AreEqual(@"\A\B", set.FindPolicy(Ns, "Lead")!.Key);
        Assert.AreEqual(@"A\\B", set.FindPolicy(Ns, "Mid")!.Key);
    }
}
