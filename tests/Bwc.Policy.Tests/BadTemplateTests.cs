using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class BadTemplateTests
{
    private static TemplateSet LoadBad(string name) => TemplateLoader.Load([Fixture.Bad(name)], "en-US");

    [TestMethod]
    public void MalformedXml_IsOneFailureWithFileLineAndPosition()
    {
        var set = LoadBad("MalformedXml");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MalformedXml);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual(Path.Combine(Fixture.Bad("MalformedXml"), "BwcMalformedXml.admx"), failure.FilePath);
        Assert.AreEqual(20, failure.Line);
        Assert.IsTrue(failure.Position > 0);
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(0, set.Policies.Count);
    }

    [TestMethod]
    public void MissingAdml_NamesTheExpectedEnUsAdmlPath()
    {
        var set = LoadBad("MissingAdml");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAdml);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual(Path.Combine(Fixture.Bad("MissingAdml"), "en-US", "BwcMissingAdml.adml"), failure.FilePath);
        Assert.AreEqual(Path.Combine(Fixture.Bad("MissingAdml"), "BwcMissingAdml.admx"), failure.Detail);
        Assert.AreEqual(0, set.Files.Count);
    }

    [TestMethod]
    public void MissingAdml_WithAnotherCulture_StillNamesTheEnUsFallbackPath()
    {
        var set = TemplateLoader.Load([Fixture.Bad("MissingAdml")], "fr-FR");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAdml);
        Assert.AreEqual(Path.Combine(Fixture.Bad("MissingAdml"), "en-US", "BwcMissingAdml.adml"), failure.FilePath);
    }

    [TestMethod]
    public void UndefinedString_NamesTheIdAndTheFile_AndTheFileAddsNothing()
    {
        var set = LoadBad("UndefinedString");
        var failure = FailureAssert.Single(set, TemplateFailureCode.UndefinedString);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual("Pol_Bad_NotDefined", failure.Detail);
        Assert.AreEqual(Path.Combine(Fixture.Bad("UndefinedString"), "BwcUndefinedString.admx"), failure.FilePath);
        Assert.IsTrue(failure.Line > 0);
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(0, set.Policies.Count);
        Assert.AreEqual(0, set.Categories.Count);
        Assert.AreEqual(0, set.SupportedOn.Count);
    }

    [TestMethod]
    public void UndefinedUsing_NamesTheNamespace_AndTheFileStaysLoaded()
    {
        var set = LoadBad("UndefinedUsing");
        var failure = FailureAssert.Single(set, TemplateFailureCode.UndefinedUsing);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual("BWC.Policies.Bad.UndefinedUsing.Ghost", failure.Detail);
        Assert.AreEqual(Path.Combine(Fixture.Bad("UndefinedUsing"), "BwcUndefinedUsing.admx"), failure.FilePath);
        Assert.AreEqual(1, set.Files.Count);
        Assert.AreEqual(1, set.Policies.Count);
    }

    [TestMethod]
    public void DuplicatePolicy_NamesThePolicy_AndTheFileAddsNothing()
    {
        var set = LoadBad("DuplicatePolicy");
        var failure = FailureAssert.Single(set, TemplateFailureCode.DuplicatePolicy);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual("Pol_Bad", failure.Detail);
        Assert.AreEqual(Path.Combine(Fixture.Bad("DuplicatePolicy"), "BwcDuplicatePolicy.admx"), failure.FilePath);
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(0, set.Policies.Count);
        Assert.AreEqual(0, set.Categories.Count);
        Assert.AreEqual(0, set.SupportedOn.Count);
    }

    [TestMethod]
    public void DtdEntity_IsRefusedAsDtdProhibitedNotMalformed()
    {
        var set = LoadBad("DtdEntity");
        var failure = FailureAssert.Single(set, TemplateFailureCode.DtdProhibited);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual(Path.Combine(Fixture.Bad("DtdEntity"), "BwcDtdEntity.admx"), failure.FilePath);
        Assert.AreEqual(2, failure.Line);
        Assert.IsTrue(failure.Position > 0);
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(0, set.Policies.Count);
    }

    [TestMethod]
    public void EveryBadFixture_GivesAtLeastOneFailureAndNeverThrows()
    {
        foreach (var name in new[] { "MalformedXml", "MissingAdml", "UndefinedString", "UndefinedUsing", "DuplicatePolicy", "DtdEntity", "ListItemNoKey", "BooleanNoValueName" })
        {
            var set = LoadBad(name);
            Assert.IsTrue(set.Failures.Count >= 1, name);
            Assert.IsTrue(set.Failures.All(f => f.FilePath.StartsWith(Fixture.Bad(name), StringComparison.OrdinalIgnoreCase)), name);
        }
    }

    [TestMethod]
    public void GoodAndBadTogether_OnlyTheBadOneFails()
    {
        var good = Fixture.Load();
        var set = TemplateLoader.Load(
            [
                Fixture.Root,
                Fixture.Bad("MalformedXml"),
                Fixture.Bad("DtdEntity"),
                Fixture.Bad("MissingAdml"),
                Fixture.Bad("DuplicatePolicy"),
            ],
            "en-US");
        Assert.AreEqual(good.Policies.Count, set.Policies.Count);
        Assert.AreEqual(good.Files.Count, set.Files.Count);
        Assert.AreEqual(good.Categories.Count, set.Categories.Count);
        Assert.AreEqual(good.SupportedOn.Count, set.SupportedOn.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                TemplateFailureCode.MalformedXml,
                TemplateFailureCode.DtdProhibited,
                TemplateFailureCode.MissingAdml,
                TemplateFailureCode.DuplicatePolicy,
            },
            set.Failures.Select(f => f.Code).ToArray());
        CollectionAssert.AreEqual(
            good.Policies.Select(p => p.FullName).ToArray(),
            set.Policies.Take(good.Policies.Count).Select(p => p.FullName).ToArray());
    }

    [TestMethod]
    public void Malformed_AdmxAndAdml_AreBothReportedWithTheirOwnPath()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("Good", "T.Good", """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""", """<string id="P">P</string>""");
        var badAdmx = temp.Write("BadAdmx.admx", $"{TempTemplates.Header}\n<policyDefinitions>\n  <oops>\n</policyDefinitions>");
        temp.Write(Path.Combine("en-US", "BadAdmx.adml"), "<policyDefinitionResources />");
        temp.Write("BadAdml.admx", $"""
            {TempTemplates.Header}
            <policyDefinitions><policyNamespaces><target prefix="b" namespace="T.BadAdml" /></policyNamespaces></policyDefinitions>
            """);
        var badAdml = temp.Write(Path.Combine("en-US", "BadAdml.adml"), "<policyDefinitionResources>\n<resources>\n</policyDefinitionResources>");
        var set = temp.Load();
        Assert.AreEqual(1, set.Files.Count);
        var failures = set.Failures.Where(f => f.Code == TemplateFailureCode.MalformedXml).OrderBy(f => f.FilePath, StringComparer.Ordinal).ToList();
        Assert.AreEqual(2, failures.Count);
        Assert.IsTrue(failures.Any(f => f.FilePath == badAdmx && f.Line == 4));
        Assert.IsTrue(failures.Any(f => f.FilePath == badAdml && f.Line == 3));
    }

    [TestMethod]
    public void NotATemplate_RootOrTargetMissing_IsRecorded()
    {
        using var temp = new TempTemplates();
        temp.Write("Other.admx", $"{TempTemplates.Header}<somethingElse />");
        temp.Write("NoTarget.admx", $"{TempTemplates.Header}<policyDefinitions><policyNamespaces /></policyDefinitions>");
        temp.Write("NoPrefix.admx", $"""{TempTemplates.Header}<policyDefinitions><policyNamespaces><target namespace="T.X" /></policyNamespaces></policyDefinitions>""");
        var set = temp.Load();
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(3, set.Failures.Count(f => f.Code == TemplateFailureCode.NotATemplate));
    }
}
