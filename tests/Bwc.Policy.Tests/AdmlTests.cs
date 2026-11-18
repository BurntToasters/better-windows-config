using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class AdmlTests
{
    private static TemplateSet fixture = null!;

    [ClassInitialize]
    public static void Init(TestContext context) => fixture = Fixture.Load();

    [TestMethod]
    public void Strings_ResolveDisplayNameExplainTextAndSupportedOn()
    {
        var policy = fixture.Policy("Pol_Class_Both");
        Assert.AreEqual("Class both flag", policy.DisplayName);
        Assert.IsTrue(policy.ExplainText.StartsWith("A policy of class Both.", StringComparison.Ordinal));
        Assert.AreEqual("BWC oracle fixture: older systems only", policy.SupportedOnText);
        Assert.AreEqual("BWC Oracle", fixture.FindCategory(Fixture.BaseNamespace, "BwcOracle")!.DisplayName);
    }

    [TestMethod]
    public void Strings_FileDisplayNameAndDescription_AreRecorded()
    {
        var file = fixture.FindFile(Fixture.BaseNamespace)!;
        Assert.AreEqual("BWC Oracle Base", file.DisplayName);
        Assert.IsNotNull(file.Description);
        Assert.AreEqual("en-US", file.Culture);
        Assert.IsTrue(file.AdmlPath.EndsWith(Path.Combine("en-US", "BwcOracleBase.adml"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Ampersand_InLabelsAndNames_StaysLiteral()
    {
        Assert.AreEqual("Tools & Utilities", fixture.Category("Ampersand").DisplayName);
        Assert.AreEqual("Research & Development flag", fixture.Policy("Pol_Amp_DisplayName").DisplayName);

        var single = fixture.Policy("Pol_Amp_LabelSingle");
        var labels = single.Presentation!.Controls.Select(c => c.Label).ToArray();
        Assert.IsTrue(labels.Any(l => l is not null && l.Contains('&') && !l.Contains("&&", StringComparison.Ordinal)));
        Assert.IsFalse(labels.Any(l => l is not null && l.Contains("&amp;", StringComparison.Ordinal)));
        var choice = single.Element<EnumElement>("AmpChoice");
        Assert.IsTrue(choice.Items.Any(i => i.DisplayName.Contains('&')));

        var doubled = fixture.Policy("Pol_Amp_LabelDouble").Presentation!.Controls.Single(c => c.Label is not null);
        Assert.IsTrue(doubled.Label!.Contains("&&", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExplainText_KeepsParagraphsAndNewlinesWithoutTrimming()
    {
        var text = fixture.Policy("Pol_Explain_MultiParagraph").ExplainText;
        Assert.IsFalse(text.Contains('\r'));
        Assert.AreEqual(
            "This is the first paragraph of the explanation.\n\nThis is the second paragraph. It follows an empty line.\n\n"
            + "This is the third paragraph.\nIt continues on a new line without an empty line before it.",
            text);
    }

    [TestMethod]
    public void ExplainText_EmptyStringAndOmittedAttribute_AreEmptyWithoutFailure()
    {
        Assert.AreEqual(string.Empty, fixture.Policy("Pol_Explain_EmptyString").ExplainText);
        Assert.AreEqual(string.Empty, fixture.Policy("Pol_Explain_AttributeOmitted").ExplainText);
        FailureAssert.None(fixture);
    }

    [TestMethod]
    public void ExplainText_LeadingAndTrailingBlanks_AreKept()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Blanks",
            "T.Blanks",
            """<policy name="P" class="User" displayName="$(string.P)" explainText="$(string.H)" key="K"><parentCategory ref="Root" /></policy>""",
            "<string id=\"P\">P</string><string id=\"H\">  padded\n\n  text  </string>");
        Assert.AreEqual("  padded\n\n  text  ", temp.Load().Policies.Single().ExplainText);
    }

    [TestMethod]
    public void Presentation_ControlsKeepOrderAndLinkToElements()
    {
        var presentation = fixture.Policy("Pol_MultiText_Limits").Presentation!;
        Assert.AreEqual(2, presentation.Controls.Count);
        Assert.AreEqual(PresentationControlKind.Text, presentation.Controls[0].Kind);
        Assert.AreEqual("At most three lines.", presentation.Controls[0].Label);
        Assert.IsNull(presentation.Controls[0].RefId);
        Assert.IsNull(presentation.Controls[0].Element);
        Assert.AreEqual(PresentationControlKind.MultiTextBox, presentation.Controls[1].Kind);
        Assert.AreEqual(3UL, presentation.Controls[1].DefaultHeight);
        Assert.AreSame(fixture.Policy("Pol_MultiText_Limits").Elements[0], presentation.Controls[1].Element);

        var reversed = fixture.Policy("Pol_Bool_TwoChecksReverseOrder").Presentation!;
        CollectionAssert.AreEqual(new[] { "ZetaCheck", "AlphaCheck" }, reversed.Controls.Select(c => c.RefId).ToArray());
    }

    [TestMethod]
    public void Presentation_CheckBoxAndDecimalAttributes()
    {
        var check = fixture.Policy("Pol_Bool_StringValues").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.CheckBox, check.Kind);
        Assert.AreEqual("String check", check.Label);
        Assert.AreEqual(true, check.DefaultChecked);

        var number = fixture.Policy("Pol_Decimal_MinMax").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.DecimalTextBox, number.Kind);
        Assert.AreEqual("25", number.DefaultValue);
        Assert.AreEqual(true, number.Spin);
        Assert.AreEqual(5UL, number.SpinStep);
        Assert.AreEqual("Number from 10 to 500:", number.Label);

        var noSpin = fixture.Policy("Pol_Decimal_Required").Presentation!.Controls.Single();
        Assert.AreEqual(false, noSpin.Spin);
        Assert.IsNull(noSpin.DefaultValue);

        var longBox = fixture.Policy("Pol_LongDecimal_Basic").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.LongDecimalTextBox, longBox.Kind);
        Assert.AreEqual("123456789012", longBox.DefaultValue);
        Assert.AreEqual(1000UL, longBox.SpinStep);
    }

    [TestMethod]
    public void Presentation_TextBoxLabelAndDefaultAreChildren()
    {
        var box = fixture.Policy("Pol_Text_Plain").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.TextBox, box.Kind);
        Assert.AreEqual("Plain text:", box.Label);
        Assert.AreEqual("plain default", box.DefaultValue);
        Assert.AreEqual("%SystemRoot%\\bwc-oracle", fixture.Policy("Pol_Text_Expandable").Presentation!.Controls.Single().DefaultValue);
    }

    [TestMethod]
    public void Presentation_ComboBox_HasDefaultSuggestionsAndNoSort()
    {
        var sorted = fixture.Policy("Pol_Text_ComboBoxSuggestions").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.ComboBox, sorted.Kind);
        Assert.AreEqual("Oslo", sorted.DefaultValue);
        CollectionAssert.AreEqual(new[] { "Paris", "Oslo", "Lima", "Cairo" }, sorted.Suggestions.ToArray());
        Assert.IsNull(sorted.NoSort);
        Assert.IsInstanceOfType<TextElement>(sorted.Element);

        var unsorted = fixture.Policy("Pol_Text_ComboBoxNoSortRequired").Presentation!.Controls.Single();
        Assert.AreEqual(true, unsorted.NoSort);
    }

    [TestMethod]
    public void Presentation_DropdownAndListBox()
    {
        var drop = fixture.Policy("Pol_Enum_NoSortDefaultItem").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.DropdownList, drop.Kind);
        Assert.AreEqual(true, drop.NoSort);
        Assert.AreEqual(1UL, drop.DefaultItem);
        Assert.IsInstanceOfType<EnumElement>(drop.Element);

        var list = fixture.Policy("Pol_List_Plain").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.ListBox, list.Kind);
        Assert.AreEqual("Plain entries:", list.Label);
        Assert.IsInstanceOfType<ListElement>(list.Element);
    }

    [TestMethod]
    public void Presentation_PolicyWithoutPresentationAttribute_HasNone()
    {
        Assert.IsNull(fixture.Policy("Pol_Class_Machine").Presentation);
        Assert.AreEqual(39, fixture.Policies.Count(p => p.Presentation is not null));
    }

    [TestMethod]
    public void Presentation_ShippedSchemaBreaks_AreIgnoredWithoutFailure()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Shipped",
            "T.Shipped",
            """
            <policy name="P" class="Machine" displayName="$(string.P)" key="K" presentation="$(presentation.P)"><parentCategory ref="Root" />
              <elements><boolean id="B" valueName="B" /><enum id="E" valueName="E" /><list id="L" key="K\L" /></elements></policy>
            """,
            """<string id="P">P</string>""",
            presentations: """
            <presentation id="P">
              <checkBox refId="B" defaultItem="text" noSort="x">Check</checkBox>
              <dropdownList refId="E" oSort="true">Drop</dropdownList>
              <listBox refId="L" required="true">List</listBox>
            </presentation>
            """);
        var set = temp.Load();
        FailureAssert.None(set);
        var controls = set.Policies.Single().Presentation!.Controls;
        Assert.AreEqual(3, controls.Count);
        Assert.AreEqual(true, controls[2].Required);
        Assert.IsNull(controls[0].DefaultItem);
        Assert.IsNull(controls[1].NoSort);
    }

    [TestMethod]
    public void Presentation_UnknownControlKind_IsIgnored()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Unknown",
            "T.Unknown",
            """<policy name="P" class="Machine" displayName="$(string.P)" key="K" presentation="$(presentation.P)"><parentCategory ref="Root" /></policy>""",
            """<string id="P">P</string>""",
            presentations: """<presentation id="P"><sparkle refId="X">Fancy</sparkle><text>Hello</text></presentation>""");
        var set = temp.Load();
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Policies.Single().Presentation!.Controls.Count);
    }

    [TestMethod]
    public void Presentation_SharedByTwoPolicies_BindsEachToItsOwnElements()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Shared",
            "T.Shared",
            """
            <policy name="One" class="Machine" displayName="$(string.P)" key="K" presentation="$(presentation.P)"><parentCategory ref="Root" />
              <elements><text id="X" valueName="One" /></elements></policy>
            <policy name="Two" class="Machine" displayName="$(string.P)" key="K" presentation="$(presentation.P)"><parentCategory ref="Root" />
              <elements><text id="X" valueName="Two" /></elements></policy>
            """,
            """<string id="P">P</string>""",
            presentations: """<presentation id="P"><textBox refId="X"><label>L</label></textBox></presentation>""");
        var set = temp.Load();
        FailureAssert.None(set);
        var one = set.FindPolicy("T.Shared", "One")!;
        var two = set.FindPolicy("T.Shared", "Two")!;
        Assert.AreSame(one.Elements[0], one.Presentation!.Controls[0].Element);
        Assert.AreSame(two.Elements[0], two.Presentation!.Controls[0].Element);
        Assert.AreNotSame(one.Elements[0], two.Elements[0]);
    }

    [TestMethod]
    public void Presentation_ControlWithoutElement_IsRecordedAndTheFileIsRejected()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Dangling",
            "T.Dangling",
            """<policy name="P" class="Machine" displayName="$(string.P)" key="K" presentation="$(presentation.P)"><parentCategory ref="Root" /></policy>""",
            """<string id="P">P</string>""",
            presentations: """<presentation id="P"><textBox refId="Nope"><label>L</label></textBox></presentation>""");
        var set = temp.Load();
        Assert.AreEqual("Nope", FailureAssert.Single(set, TemplateFailureCode.UndefinedPresentationRef).Detail);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Presentation_UndefinedId_IsRecordedAndTheFileIsRejected()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "NoPres",
            "T.NoPres",
            """
            <policy name="P" class="Machine" displayName="$(string.P)" key="K" presentation="$(presentation.Ghost)"><parentCategory ref="Root" /></policy>
            <policy name="Q" class="Machine" displayName="$(string.P)" key="K" presentation="plain text"><parentCategory ref="Root" /></policy>
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        FailureAssert.NothingLoaded(set);
        var details = set.Failures.Where(f => f.Code == TemplateFailureCode.UndefinedPresentation).Select(f => f.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "Ghost", "plain text" }, details);
    }

    [TestMethod]
    public void Strings_UndefinedReference_IsRecordedAndTheFileIsRejected()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Undef",
            "T.Undef",
            """<policy name="P" class="Machine" displayName="$(string.Missing)" explainText="$(string.AlsoMissing)" key="K"><parentCategory ref="Root" /></policy>""");
        var set = temp.Load();
        FailureAssert.NothingLoaded(set);
        var details = set.Failures.Where(f => f.Code == TemplateFailureCode.UndefinedString).Select(f => f.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "AlsoMissing", "Missing" }, details);
    }

    [TestMethod]
    public void Strings_LiteralText_IsKeptAsWritten()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Literal",
            "T.Literal",
            """<policy name="P" class="Machine" displayName="Plain name" key="K"><parentCategory ref="Root" /></policy>""");
        var set = temp.Load();
        FailureAssert.None(set);
        Assert.AreEqual("Plain name", set.Policies.Single().DisplayName);
    }

    [TestMethod]
    public void Strings_DuplicateIdInOneAdml_IsRecordedAndTheFileIsRejected()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "DupStr",
            "T.DupStr",
            """<policy name="P" class="Machine" displayName="$(string.Twice)" key="K"><parentCategory ref="Root" /></policy>""",
            """<string id="Twice">First</string><string id="TWICE">Second</string>""");
        var set = temp.Load();
        FailureAssert.NothingLoaded(set);
        Assert.AreEqual("TWICE", FailureAssert.Single(set, TemplateFailureCode.DuplicateString).Detail);
    }

    [TestMethod]
    public void Strings_OverTheLengthLimit_AreNotStoredAndCountAsUndefined()
    {
        using var temp = new TempTemplates();
        var huge = new string('x', AdmxLimits.MaxStringLength + 1);
        temp.AddSimple(
            "Huge",
            "T.Huge",
            """<policy name="P" class="Machine" displayName="$(string.P)" explainText="$(string.Huge)" key="K"><parentCategory ref="Root" /></policy>""",
            $"""<string id="P">P</string><string id="Huge">{huge}</string>""");
        var set = temp.Load();
        Assert.AreEqual("Huge", FailureAssert.Single(set, TemplateFailureCode.StringTooLong).Detail);
        FailureAssert.NothingLoaded(set);
        Assert.AreEqual("Huge", FailureAssert.Single(set, TemplateFailureCode.UndefinedString).Detail);
    }

    [TestMethod]
    public void Language_RequestedCultureIsUsedPerFileWithEnUsFallback()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("Both", "T.Both", """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""", """<string id="P">English</string>""");
        temp.AddSimple("Only", "T.Only", """<policy name="Q" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""", """<string id="P">English only</string>""");
        temp.Write(Path.Combine("de-DE", "Both.adml"), $"""
            {TempTemplates.Header}
            <policyDefinitionResources><resources><stringTable>
              <string id="Root">Wurzel</string><string id="SUP">Unterstuetzt</string><string id="P">Deutsch</string>
            </stringTable></resources></policyDefinitionResources>
            """);

        var set = temp.Load("de-DE");
        FailureAssert.None(set);
        Assert.AreEqual("Deutsch", set.FindPolicy("T.Both", "P")!.DisplayName);
        Assert.AreEqual("de-DE", set.FindFile("T.Both")!.Culture);
        Assert.AreEqual("English only", set.FindPolicy("T.Only", "Q")!.DisplayName);
        Assert.AreEqual("en-US", set.FindFile("T.Only")!.Culture);
        Assert.AreEqual("de-DE", set.Culture);

        var english = temp.Load("en-US");
        Assert.AreEqual("English", english.FindPolicy("T.Both", "P")!.DisplayName);
    }

    [TestMethod]
    public void Language_NullEmptyOrUnsafeCulture_MeansEnUs()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("One", "T.One", """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""", """<string id="P">English</string>""");
        foreach (var culture in new string?[] { null, string.Empty, "  ", "..", "a/b", "x\\y" })
        {
            var set = temp.Load(culture);
            FailureAssert.None(set);
            Assert.AreEqual("en-US", set.Culture, culture);
            Assert.AreEqual("en-US", set.FindFile("T.One")!.Culture);
        }
    }

    [TestMethod]
    public void Language_BadRequestedAdml_IsNotReplacedByEnUs()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("One", "T.One", """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""", """<string id="P">English</string>""");
        var broken = temp.Write(Path.Combine("de-DE", "One.adml"), "<policyDefinitionResources><resources>");
        var set = temp.Load("de-DE");
        Assert.AreEqual(0, set.Files.Count);
        var failure = FailureAssert.Single(set, TemplateFailureCode.MalformedXml);
        Assert.AreEqual(broken, failure.FilePath);
    }

    [TestMethod]
    public void Adml_WrongRootElement_IsNotATemplate()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("One", "T.One", """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""", """<string id="P">English</string>""");
        var adml = temp.Write(Path.Combine("en-US", "One.adml"), $"{TempTemplates.Header}<other />");
        var set = temp.Load();
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(adml, FailureAssert.Single(set, TemplateFailureCode.NotATemplate).FilePath);
    }
}
