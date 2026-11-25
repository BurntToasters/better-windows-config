using System.Text;
using Bwc.Policy.Admx;
using static Bwc.Policy.Tests.Blind.BlindKit;

namespace Bwc.Policy.Tests.Blind;

/// <summary>
/// Blind tests of the ADMX and ADML parser and linker (piece E2). Derived from docs/failure-modes/policy-admx.md and
/// docs/contracts/policy-engine.md without reading the implementation. The comment on each test names the section.
/// </summary>
[TestClass]
public sealed class AdmxBlindTests
{
    private static TemplateSet LoadBad(string name) => TemplateLoader.Load([Fixture.Bad(name)], "en-US");

    // ---------------------------------------------------------------- 1. Malformed XML

    [TestMethod]
    public void Sec01_MalformedFixture_FailsWithFileLineAndPosition_AndLoadsNothing()
    {
        var set = LoadBad("MalformedXml");
        var failure = One(set, TemplateFailureCode.MalformedXml);
        StringAssert.EndsWith(failure.FilePath, ".admx");
        Assert.IsTrue(failure.Line > 0, "line");
        Assert.IsTrue(failure.Position > 0, "position");
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec01_MalformedAdml_NamesTheAdml_AndDropsTheTemplate()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var adml = Path.Combine(t.Root, "en-US", "a.adml");
        File.WriteAllText(adml, AdmlText(Strings("Root", "SUP", "Pol")).Replace("</stringTable>", ""));
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MalformedXml);
        Assert.AreEqual(adml, failure.FilePath, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec01_TwoRootElements_IsMalformed()
    {
        using var t = new TempTemplates();
        t.Write("a.admx", AdmxText("Blind.A", "t", "") + "<extra/>");
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        One(set, TemplateFailureCode.MalformedXml);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec01_MalformedTemplate_DoesNotStopAGoodSibling()
    {
        using var t = new TempTemplates();
        Std(t, "good", "Blind.Good", P("PolGood"), ["PolGood"]);
        t.Write("bad.admx", "<policyDefinitions><oops></policyDefinitions>");
        var set = t.Load();
        One(set, TemplateFailureCode.MalformedXml);
        Assert.IsNotNull(set.FindPolicy("Blind.Good", "PolGood"));
        Assert.AreEqual(1, set.Files.Count);
    }

    // ---------------------------------------------------------------- 2. DTD and entities

    [TestMethod]
    public void Sec02_DtdFixture_IsDtdProhibited_NotMalformed_WithLineOfTheDoctype()
    {
        var set = LoadBad("DtdEntity");
        var failure = One(set, TemplateFailureCode.DtdProhibited);
        Assert.AreEqual(0, Of(set, TemplateFailureCode.MalformedXml).Count, "must not be reported as MalformedXml");
        Assert.AreEqual(2, failure.Line, "the DOCTYPE is on line 2 of the fixture");
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec02_ExternalEntity_IsRefusedWithoutFetching()
    {
        using var t = new TempTemplates();
        var admx = "<?xml version=\"1.0\"?>\n<!DOCTYPE policyDefinitions [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>\n" +
                   AdmxText("Blind.A", "t", "<policies />").Split('\n', 2)[1];
        t.Write("a.admx", admx);
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        One(set, TemplateFailureCode.DtdProhibited);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec02_DoctypeInAdml_IsRefused()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var adml = Path.Combine(t.Root, "en-US", "a.adml");
        File.WriteAllText(adml, "<?xml version=\"1.0\"?>\n<!DOCTYPE policyDefinitionResources [<!ENTITY e \"x\">]>\n" +
                                AdmlText("").Split('\n', 2)[1]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.DtdProhibited);
        Assert.AreEqual(adml, failure.FilePath, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec39_DoctypeAfterTheScanWindow_IsStillDtdProhibited_WithPositionZero()
    {
        using var t = new TempTemplates();
        var filler = "<!--" + new string('x', 70 * 1024) + "-->";
        var admx = "<?xml version=\"1.0\"?>\n" + filler + "\n<!DOCTYPE policyDefinitions [<!ENTITY e \"x\">]>\n" +
                   AdmxText("Blind.A", "t", "").Split('\n', 2)[1];
        t.Write("a.admx", admx);
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.DtdProhibited);
        Assert.AreEqual(0, failure.Line);
        Assert.AreEqual(0, failure.Position);
        Assert.AreEqual(0, Of(set, TemplateFailureCode.MalformedXml).Count);
    }

    // ---------------------------------------------------------------- 3. Oversized file

    [TestMethod]
    public void Sec03_TheCapIsSixteenMebibytes()
    {
        Assert.AreEqual(16L * 1024 * 1024, typeof(AdmxLimits).GetField(nameof(AdmxLimits.MaxFileBytes))!.GetRawConstantValue());
    }

    [TestMethod]
    public void Sec03_AdmxOverTheCap_FailsFileTooLarge_AndIsNeverParsed()
    {
        using var t = new TempTemplates();
        var path = Path.Combine(t.Root, "big.admx");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(AdmxLimits.MaxFileBytes + 1);
        }

        var set = t.Load();
        var failure = One(set, TemplateFailureCode.FileTooLarge);
        Assert.AreEqual(path, failure.FilePath, true);
        Assert.AreEqual(0, Of(set, TemplateFailureCode.MalformedXml).Count, "an oversized file must not be parsed");
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec03_AdmlOverTheCap_FailsFileTooLarge_AndDropsTheTemplate()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var adml = Path.Combine(t.Root, "en-US", "a.adml");
        using (var stream = new FileStream(adml, FileMode.Create))
        {
            stream.SetLength(AdmxLimits.MaxFileBytes + 1);
        }

        var set = t.Load();
        Assert.AreEqual(adml, One(set, TemplateFailureCode.FileTooLarge).FilePath, true);
        FailureAssert.NothingLoaded(set);
    }

    // ---------------------------------------------------------------- 4. Reparse points

    [TestMethod]
    [DataRow(FileAttributes.Archive | FileAttributes.ReparsePoint, true)]
    [DataRow(FileAttributes.ReparsePoint, true)]
    [DataRow(FileAttributes.Archive, false)]
    [DataRow(FileAttributes.Normal, false)]
    public void Sec04_ReparsePointAttribute_IsDetected(FileAttributes attributes, bool expected)
    {
        Assert.AreEqual(expected, SafeXml.IsReparsePoint(attributes));
    }

    [TestMethod]
    public void Sec04_JunctionAsSourceFolder_IsRefused_AndNothingIsReadThroughIt()
    {
        using var real = new TempTemplates();
        Std(real, "a", "Blind.A", P("Pol"), ["Pol"]);
        using var host = new TempTemplates();
        var link = Path.Combine(host.Root, "link");
        host.MakeJunction(link, real.Root);
        var set = TemplateLoader.Load([link], "en-US");
        var failure = One(set, TemplateFailureCode.ReparsePoint);
        Assert.AreEqual(link, failure.FilePath, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec04_JunctionAsLanguageFolder_IsReportedOnce_AndFallsBackToEnUs()
    {
        using var real = new TempTemplates();
        Std(real, "a", "Blind.A", P("Pol"), ["Pol"], culture: "de-DE");
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var link = Path.Combine(t.Root, "de-DE");
        t.MakeJunction(link, Path.Combine(real.Root, "de-DE"));
        var set = t.Load("de-DE");
        var failure = One(set, TemplateFailureCode.ReparsePoint);
        Assert.AreEqual(link, failure.FilePath, true);
        Assert.IsNotNull(set.FindPolicy("Blind.A", "Pol"));
        Assert.AreEqual("en-US", set.FindFile("Blind.A")!.Culture, true);
    }

    // ---------------------------------------------------------------- 5. Missing source folder

    [TestMethod]
    public void Sec05_MissingFolder_FailsFolderNotFound_AndOthersStillLoad()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var missing = Path.Combine(t.Root, "nope");
        var set = TemplateLoader.Load([missing, t.Root], "en-US");
        var failure = One(set, TemplateFailureCode.FolderNotFound);
        Assert.AreEqual(missing, failure.FilePath, true);
        Assert.IsNotNull(set.FindPolicy("Blind.A", "Pol"));
        Assert.AreEqual(1, set.Failures.Count);
    }

    // ---------------------------------------------------------------- 6. Unreadable file

    [TestMethod]
    public void Sec06_FileHeldExclusively_FailsUnreadable_WithHresult_AndNeverThrows()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var admx = Path.Combine(t.Root, "a.admx");
        using var hold = new FileStream(admx, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.Unreadable);
        Assert.AreEqual(admx, failure.FilePath, true);
        Assert.IsFalse(string.IsNullOrEmpty(failure.Detail), "the HRESULT is in Detail");
        FailureAssert.NothingLoaded(set);
    }

    // ---------------------------------------------------------------- 7. File names and extensions, 41

    [TestMethod]
    public void Sec07_Sec41_OnlyAdmxFilesAreTemplates_ExtensionIgnoresCase_SubFoldersAreNotRead()
    {
        using var t = new TempTemplates();
        Std(t, "upper", "Blind.Upper", P("PolUpper"), ["PolUpper"]);
        File.Move(Path.Combine(t.Root, "upper.admx"), Path.Combine(t.Root, "upper.ADMX"));
        t.Write("notes.txt", "not xml");
        t.Write("x.admx.bak", "not xml");
        t.Write("x.adml", "not xml");
        t.Write(Path.Combine("sub", "inner.admx"), "not xml");
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Files.Count);
        Assert.IsNotNull(set.FindPolicy("Blind.Upper", "PolUpper"));
    }

    [TestMethod]
    public void Sec41_ADirectoryNamedLikeATemplate_IsNotATemplate()
    {
        using var t = new TempTemplates();
        Directory.CreateDirectory(Path.Combine(t.Root, "folder.admx"));
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(0, set.Files.Count);
    }

    // ---------------------------------------------------------------- 8. Depth

    [TestMethod]
    public void Sec08_NestingPastTheLimit_FailsXmlTooDeep()
    {
        using var t = new TempTemplates();
        var nested = string.Concat(Enumerable.Repeat("<n>", AdmxLimits.MaxXmlDepth + 5)) +
                     string.Concat(Enumerable.Repeat("</n>", AdmxLimits.MaxXmlDepth + 5));
        t.Write("a.admx", AdmxText("Blind.A", "t", nested));
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        One(set, TemplateFailureCode.XmlTooDeep);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec08_NestingWellInsideTheLimit_Loads()
    {
        using var t = new TempTemplates();
        var nested = string.Concat(Enumerable.Repeat("<n>", 60)) + string.Concat(Enumerable.Repeat("</n>", 60));
        t.Write("a.admx", AdmxText("Blind.A", "t", nested));
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Files.Count);
    }

    [TestMethod]
    public void Sec08_DeepAdml_FailsXmlTooDeep()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var nested = string.Concat(Enumerable.Repeat("<n>", AdmxLimits.MaxXmlDepth + 5)) +
                     string.Concat(Enumerable.Repeat("</n>", AdmxLimits.MaxXmlDepth + 5));
        File.WriteAllText(Path.Combine(t.Root, "en-US", "a.adml"), AdmlText("").Replace("<stringTable>", nested + "<stringTable>"));
        var set = t.Load();
        One(set, TemplateFailureCode.XmlTooDeep);
        FailureAssert.NothingLoaded(set);
    }

    // ---------------------------------------------------------------- 9. Not a template

    [TestMethod]
    public void Sec09_WrongRoot_IsNotATemplate()
    {
        using var t = new TempTemplates();
        t.Write("a.admx", "<?xml version=\"1.0\"?><somethingElse />");
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.NotATemplate);
        Assert.IsFalse(string.IsNullOrEmpty(failure.Detail));
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    [DataRow("<policyNamespaces />")]
    [DataRow("<policyNamespaces><target namespace=\"Blind.A\" /></policyNamespaces>")]
    [DataRow("<policyNamespaces><target prefix=\"t\" /></policyNamespaces>")]
    [DataRow("")]
    public void Sec09_MissingNamespaceTarget_IsNotATemplate(string namespaces)
    {
        using var t = new TempTemplates();
        t.Write("a.admx", $"<?xml version=\"1.0\"?><policyDefinitions xmlns=\"{Xmlns}\">{namespaces}</policyDefinitions>");
        t.Write(Path.Combine("en-US", "a.adml"), AdmlText(""));
        var set = t.Load();
        One(set, TemplateFailureCode.NotATemplate);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec09_AdmlWithWrongRoot_IsNotATemplate()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var adml = Path.Combine(t.Root, "en-US", "a.adml");
        File.WriteAllText(adml, "<?xml version=\"1.0\"?><wrong />");
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.NotATemplate);
        Assert.AreEqual(adml, failure.FilePath, true);
        FailureAssert.NothingLoaded(set);
    }

    // ---------------------------------------------------------------- 10. Missing ADML

    [TestMethod]
    public void Sec10_MissingAdmlFixture_NamesTheExpectedEnUsPath_AndDropsTheTemplate()
    {
        var set = LoadBad("MissingAdml");
        var failure = One(set, TemplateFailureCode.MissingAdml);
        StringAssert.EndsWith(failure.FilePath, Path.Combine("en-US", "BwcMissingAdml.adml"), StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(failure.Detail, "BwcMissingAdml.admx", StringComparison.OrdinalIgnoreCase);
        FailureAssert.NothingLoaded(set);
    }

    // ---------------------------------------------------------------- 11. Language fallback, 42, 37

    [TestMethod]
    public void Sec11_Sec42_RequestedLanguageIsUsedPerFile_AndOthersFallBackSilently()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("PolA"), ["PolA"], culture: "de-DE", extraStrings: "");
        Std(t, "a", "Blind.A", P("PolA"), ["PolA"]);
        Std(t, "b", "Blind.B", P("PolB"), ["PolB"]);
        var set = t.Load("de-DE");
        FailureAssert.None(set);
        Assert.AreEqual("de-DE", set.FindFile("Blind.A")!.Culture, true);
        Assert.AreEqual("en-US", set.FindFile("Blind.B")!.Culture, true);
        StringAssert.Contains(set.FindFile("Blind.A")!.AdmlPath, "de-DE", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Sec11_NullOrEmptyCultureMeansEnUs(string? culture)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var set = TemplateLoader.Load([t.Root], culture);
        FailureAssert.None(set);
        Assert.AreEqual("en-US", set.FindFile("Blind.A")!.Culture, true);
    }

    [TestMethod]
    public void Sec11_ABadRequestedLanguageAdml_IsNotReplacedByEnUs()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var de = Path.Combine(t.Root, "de-DE", "a.adml");
        Directory.CreateDirectory(Path.GetDirectoryName(de)!);
        File.WriteAllText(de, "<policyDefinitionResources><broken></policyDefinitionResources>");
        var set = t.Load("de-DE");
        var failure = One(set, TemplateFailureCode.MalformedXml);
        Assert.AreEqual(de, failure.FilePath, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    [DataRow(@"..\outside")]
    [DataRow(@"..")]
    [DataRow(@"sub\de-DE")]
    public void Sec37_CultureThatIsNotOneFolderName_NeverReadsOutsideTheFolder(string culture)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var folder = Path.Combine(t.Root, "inner");
        Directory.CreateDirectory(folder);
        File.Move(Path.Combine(t.Root, "a.admx"), Path.Combine(folder, "a.admx"));
        Directory.Move(Path.Combine(t.Root, "en-US"), Path.Combine(folder, "en-US"));
        // An ADML that would be read when the culture is used as a path.
        var outside = Path.Combine(t.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "a.adml"), AdmlText(Strings("Root", "SUP", "Pol")).Replace("Blind test file", "OUTSIDE"));
        Directory.CreateDirectory(Path.Combine(folder, "sub", "de-DE"));
        File.WriteAllText(Path.Combine(folder, "sub", "de-DE", "a.adml"), AdmlText(Strings("Root", "SUP", "Pol")).Replace("Blind test file", "OUTSIDE"));
        var set = TemplateLoader.Load([folder], culture);
        var file = set.FindFile("Blind.A");
        Assert.IsNotNull(file, FailureAssert.Describe(set));
        Assert.AreEqual("en-US", file.Culture, true);
        Assert.AreNotEqual("OUTSIDE", file.Description);
    }

    // ---------------------------------------------------------------- 12, 38. Undefined string

    [TestMethod]
    public void Sec12_UndefinedStringFixture_NamesTheId_AndLoadsNothing()
    {
        var set = LoadBad("UndefinedString");
        var failure = One(set, TemplateFailureCode.UndefinedString);
        Assert.AreEqual("Pol_Bad_NotDefined", failure.Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec38_EachUndefinedIdIsOneFailure_InDocumentOrder()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol1", attrs: "explainText=\"$(string.MissingOne)\"") + P("Pol2", attrs: "explainText=\"$(string.MissingTwo)\""),
            ["Pol1", "Pol2"]);
        var set = t.Load();
        var failures = Of(set, TemplateFailureCode.UndefinedString);
        CollectionAssert.AreEqual(new[] { "MissingOne", "MissingTwo" }, failures.Select(f => f.Detail).ToArray());
        Assert.IsTrue(failures.All(f => f.FilePath.EndsWith("a.admx", StringComparison.OrdinalIgnoreCase)));
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec12_StringLookupIgnoresCase()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", attrs: "explainText=\"$(string.POL_HELP)\""), ["Pol", "pol_help"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual("pol_help text", set.FindPolicy("Blind.A", "Pol")!.ExplainText);
    }

    // ---------------------------------------------------------------- 13. Presentations

    [TestMethod]
    public void Sec13_UndefinedPresentation_FailsWithTheId_AndRejectsTheFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", presentation: "$(presentation.Nope)"), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("Nope", One(set, TemplateFailureCode.UndefinedPresentation).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec13_PresentationValueNotOfTheForm_IsUndefinedPresentation()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", presentation: "plainword"), ["Pol"]);
        var set = t.Load();
        One(set, TemplateFailureCode.UndefinedPresentation);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec13_PolicyWithElementsAndNoPresentation_HasNoneAndNoFailure()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", "<elements><text id=\"T\" valueName=\"T\" /></elements>"), ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.IsNull(set.FindPolicy("Blind.A", "Pol")!.Presentation);
    }

    [TestMethod]
    public void Sec13_ControlWithUnknownRefId_FailsUndefinedPresentationRef_AndRejectsTheFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", "<elements><text id=\"T\" valueName=\"T\" /></elements>", presentation: "$(presentation.Pr)"),
            ["Pol"], presentations: "<presentation id=\"Pr\"><textBox refId=\"Ghost\"><label>x</label></textBox></presentation>");
        var set = t.Load();
        Assert.IsTrue(Of(set, TemplateFailureCode.UndefinedPresentationRef).Count >= 1, FailureAssert.Describe(set));
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec13_ElementsWithNoControl_AreKept_AndControlsKeepTheAdmlOrder()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><text id=\"A\" valueName=\"A\" /><text id=\"B\" valueName=\"B\" /><text id=\"C\" valueName=\"C\" /></elements>",
                presentation: "$(presentation.Pr)"),
            ["Pol"],
            presentations: "<presentation id=\"Pr\"><textBox refId=\"C\"><label>c</label></textBox><textBox refId=\"A\"><label>a</label></textBox></presentation>");
        var set = t.Load();
        FailureAssert.None(set);
        var policy = set.FindPolicy("Blind.A", "Pol")!;
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, policy.Elements.Select(e => e.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "C", "A" }, policy.Presentation!.Controls.Select(c => c.RefId).ToArray());
    }

    // ---------------------------------------------------------------- 14. Duplicate string or presentation

    [TestMethod]
    public void Sec14_DuplicateString_IsReported_AndRejectsTheFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"], extraStrings: "<string id=\"Pol\">again</string>");
        var set = t.Load();
        Assert.AreEqual("Pol", One(set, TemplateFailureCode.DuplicateString).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec14_DuplicatePresentation_IsReported_AndRejectsTheFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"],
            presentations: "<presentation id=\"Pr\" /><presentation id=\"PR\" />");
        var set = t.Load();
        One(set, TemplateFailureCode.DuplicatePresentation);
        FailureAssert.NothingLoaded(set);
    }

    // ---------------------------------------------------------------- 15. String length

    [TestMethod]
    public void Sec15_StringOverTheLimit_FailsStringTooLong_AndRejectsTheFile()
    {
        using var t = new TempTemplates();
        var huge = new string('x', AdmxLimits.MaxStringLength + 1);
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"], extraStrings: $"<string id=\"Huge\">{huge}</string>");
        var set = t.Load();
        One(set, TemplateFailureCode.StringTooLong);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec15_StringAtTheLimit_IsAccepted()
    {
        using var t = new TempTemplates();
        var atLimit = new string('x', AdmxLimits.MaxStringLength);
        Std(t, "a", "Blind.A", P("Pol", attrs: "explainText=\"$(string.Help)\""), ["Pol"], extraStrings: $"<string id=\"Help\">{atLimit}</string>");
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(AdmxLimits.MaxStringLength, set.FindPolicy("Blind.A", "Pol")!.ExplainText.Length);
    }

    // ---------------------------------------------------------------- 16. Name and key limits

    [TestMethod]
    public void Sec16_PolicyNameOverTheLimit_FailsNameTooLong_NamingTheAttribute()
    {
        using var t = new TempTemplates();
        var longName = new string('p', AdmxLimits.MaxNameLength + 1);
        Std(t, "a", "Blind.A", P(longName), [longName]);
        var set = t.Load();
        Assert.AreEqual("name", One(set, TemplateFailureCode.NameTooLong).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec16_PolicyNameAtTheLimit_IsAccepted()
    {
        using var t = new TempTemplates();
        var name = new string('p', AdmxLimits.MaxNameLength);
        Std(t, "a", "Blind.A", P(name), [name]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.IsNotNull(set.FindPolicy("Blind.A", name));
    }

    [TestMethod]
    public void Sec16_CategoryNameOverTheLimit_FailsNameTooLong()
    {
        using var t = new TempTemplates();
        var longName = new string('c', AdmxLimits.MaxNameLength + 1);
        Std(t, "a", "Blind.A", P("Pol"), ["Pol", "Long"],
            categories: $"<category name=\"{longName}\" displayName=\"$(string.Long)\" />");
        var set = t.Load();
        Assert.AreEqual("name", One(set, TemplateFailureCode.NameTooLong).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec16_ElementIdOverTheLimit_FailsNameTooLong_AndTheFileIsRejected()
    {
        using var t = new TempTemplates();
        var id = new string('e', AdmxLimits.MaxNameLength + 1);
        Std(t, "a", "Blind.A", P("Pol", $"<elements><text id=\"{id}\" valueName=\"x\" /></elements>"), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("id", One(set, TemplateFailureCode.NameTooLong).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec16_NamespaceAndPrefixOverTheLimit_FailNameTooLong()
    {
        using var t = new TempTemplates();
        Std(t, "a", new string('n', AdmxLimits.MaxNameLength + 1), P("Pol"), ["Pol"]);
        Std(t, "b", "Blind.B", P("Pol"), ["Pol"], prefix: new string('x', AdmxLimits.MaxNameLength + 1));
        var set = t.Load();
        var details = Of(set, TemplateFailureCode.NameTooLong).Select(f => f.Detail).OrderBy(d => d).ToArray();
        CollectionAssert.AreEqual(new[] { "namespace", "prefix" }, details);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec16_KeyComponentOverTheLimit_FailsNameTooLong_ForKey()
    {
        using var t = new TempTemplates();
        var key = @"Software\" + new string('k', AdmxLimits.MaxKeyComponentLength + 1);
        Std(t, "a", "Blind.A", P("Pol", key: key), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("key", One(set, TemplateFailureCode.NameTooLong).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec16_KeyOverTheTotalLimit_FailsNameTooLong_ForKey()
    {
        using var t = new TempTemplates();
        var component = new string('k', 200);
        var key = string.Join('\\', Enumerable.Repeat(component, 165));
        Assert.IsTrue(key.Length > AdmxLimits.MaxKeyLength);
        Std(t, "a", "Blind.A", P("Pol", key: key), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("key", One(set, TemplateFailureCode.NameTooLong).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec16_ValueNameLimit_IsEnforcedExactly()
    {
        using var t = new TempTemplates();
        Std(t, "ok", "Blind.Ok", P("Pol", valueName: new string('v', AdmxLimits.MaxValueNameLength)), ["Pol"]);
        var good = t.Load();
        FailureAssert.None(good);
        Std(t, "bad", "Blind.Bad", P("Pol", valueName: new string('v', AdmxLimits.MaxValueNameLength + 1)), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("valueName", One(set, TemplateFailureCode.NameTooLong).Detail, true);
        Assert.IsNotNull(set.FindPolicy("Blind.Ok", "Pol"));
        Assert.IsNull(set.FindPolicy("Blind.Bad", "Pol"));
    }

    // ---------------------------------------------------------------- 17. Missing required attribute

    private static void AssertMissing(string detail, Action<TempTemplates> write)
    {
        using var t = new TempTemplates();
        write(t);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual(detail, failure.Detail, true);
        StringAssert.EndsWith(failure.FilePath, ".admx", StringComparison.OrdinalIgnoreCase);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec17_PolicyWithoutName() =>
        AssertMissing("name", t => Std(t, "a", "Blind.A", P("Pol").Replace(" name=\"Pol\"", ""), ["Pol"]));

    [TestMethod]
    public void Sec17_PolicyWithoutClass() =>
        AssertMissing("class", t => Std(t, "a", "Blind.A", P("Pol", cls: null), ["Pol"]));

    [TestMethod]
    public void Sec17_PolicyWithoutKey() =>
        AssertMissing("key", t => Std(t, "a", "Blind.A", P("Pol", key: null), ["Pol"]));

    [TestMethod]
    public void Sec17_PolicyWithoutDisplayName() =>
        AssertMissing("displayName", t => Std(t, "a", "Blind.A", P("Pol").Replace(" displayName=\"$(string.Pol)\"", ""), ["Pol"]));

    [TestMethod]
    public void Sec17_CategoryWithoutName() =>
        AssertMissing("name", t => Std(t, "a", "Blind.A", P("Pol"), ["Pol", "C2"], categories: "<category displayName=\"$(string.C2)\" />"));

    [TestMethod]
    public void Sec17_ElementWithoutId() =>
        AssertMissing("id", t => Std(t, "a", "Blind.A", P("Pol", "<elements><text valueName=\"x\" /></elements>"), ["Pol"]));

    [TestMethod]
    public void Sec17_ListItemWithoutValueName() =>
        AssertMissing("valueName", t => Std(t, "a", "Blind.A",
            P("Pol", "<enabledList defaultKey=\"Software\\K\"><item><value><decimal value=\"1\" /></value></item></enabledList>"), ["Pol"]));

    [TestMethod]
    public void Sec17_DefinitionWithoutName() =>
        AssertMissing("name", t =>
        {
            Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
            var path = Path.Combine(t.Root, "a.admx");
            File.WriteAllText(path, File.ReadAllText(path).Replace("<definition name=\"SUP\" ", "<definition "));
        });

    [TestMethod]
    public void Sec17_UsingWithoutPrefix() =>
        AssertMissing("prefix", t => Std(t, "a", "Blind.A", P("Pol"), ["Pol"], usings: "    <using namespace=\"Blind.Other\" />\n"));

    [TestMethod]
    public void Sec17_UsingWithoutNamespace() =>
        AssertMissing("namespace", t => Std(t, "a", "Blind.A", P("Pol"), ["Pol"], usings: "    <using prefix=\"o\" />\n"));

    [TestMethod]
    [Ignore("open: is a present but empty required attribute (name=\"\") a MissingAttribute, an InvalidAttributeValue, or accepted? Doc section 17 and 40 are silent. No capture answers it: the eleven H cases have no empty name attribute")]
    public void Sec40_EmptyName_IsNotTheSameAsAbsent_OpenQuestion()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol").Replace(" name=\"Pol\"", " name=\"\""), ["Pol"]);
        var set = t.Load();
        Assert.IsTrue(set.Failures.Count > 0 || set.Policies.Count == 1);
    }

    // Section 36 rule 4 last sentence: "Other failures keep reporting the position of the element name."
    [TestMethod]
    public void Sec36_OtherFailures_ReportThePositionOfTheElementName()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", "\n\n    " + P("Pol", cls: null) + "\n", ["Pol"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        var text = Raw(Path.Combine(t.Root, "a.admx"));
        var index = text.IndexOf("<policy ", StringComparison.Ordinal);
        var line = 1 + text.Take(index).Count(c => c == '\n');
        var column = index - text.LastIndexOf('\n', index);
        Assert.AreEqual(line, failure.Line);
        Assert.AreEqual(column + 1, failure.Position, "the name starts one column after the '<'");
    }

    // ---------------------------------------------------------------- 18. Invalid attribute value

    private static void AssertInvalid(string detail, string policies, string[]? ids = null)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", policies, ids ?? ["Pol"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.InvalidAttributeValue);
        Assert.AreEqual(detail, failure.Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    [DataRow("machine")]
    [DataRow("USER")]
    [DataRow("bOtH")]
    public void Sec18_ClassInAnotherLetterCase_IsRefused(string written) =>
        // Capture H/ClassLowerCase: classic refuses class="machine" with a message box.
        AssertInvalid("class", P("Pol", cls: written));

    [TestMethod]
    public void Sec18_BadClass() => AssertInvalid("class", P("Pol", cls: "Sometimes"));

    [TestMethod]
    [DataRow("Machine", PolicyClass.Machine)]
    [DataRow("User", PolicyClass.User)]
    [DataRow("Both", PolicyClass.Both)]
    public void Sec18_ClassFollowsTheSchemaLetterCase(string written, PolicyClass expected)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", cls: written), ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(expected, set.FindPolicy("Blind.A", "Pol")!.Class);
    }

    [TestMethod]
    public void Sec18_BadBoolean() =>
        AssertInvalid("required", P("Pol", "<elements><text id=\"T\" valueName=\"T\" required=\"maybe\" /></elements>"));

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    [DataRow("1", true)]
    [DataRow("0", false)]
    public void Sec18_BooleanSpellings(string written, bool expected)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", $"<elements><text id=\"T\" valueName=\"T\" required=\"{written}\" /></elements>"), ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(expected, set.FindPolicy("Blind.A", "Pol")!.Element<TextElement>("T").Required);
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("abc")]
    [DataRow("1.5")]
    [DataRow("99999999999999999999999999")]
    [DataRow("")]
    public void Sec18_BadNumber(string written) =>
        AssertInvalid("minValue", P("Pol", $"<elements><decimal id=\"N\" valueName=\"N\" minValue=\"{written}\" /></elements>"));

    [TestMethod]
    public void Sec18_BadLiteralNumberInEnabledValue_IsInvalid()
    {
        AssertInvalid("value", P("Pol", "<enabledValue><decimal value=\"abc\" /></enabledValue>"));
    }

    // ---------------------------------------------------------------- 19. Undefined prefix and using

    [TestMethod]
    public void Sec19_UndefinedPrefixInPolicyCategory_FailsWithPrefix_FileStays_PolicyHasNoCategory()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", cat: "zzz:Root"), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("zzz", One(set, TemplateFailureCode.UndefinedPrefix).Detail, true);
        Assert.AreEqual(1, set.Files.Count);
        var policy = set.FindPolicy("Blind.A", "Pol");
        Assert.IsNotNull(policy);
        Assert.IsNull(policy.Category);
    }

    [TestMethod]
    public void Sec19_UndefinedPrefixInCategoryParentAndSupportedOn_IsReportedForEach()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", sup: "qqq:SUP"), ["Pol", "C2"],
            categories: "<category name=\"C2\" displayName=\"$(string.C2)\"><parentCategory ref=\"zzz:Root\" /></category>");
        var set = t.Load();
        var details = Of(set, TemplateFailureCode.UndefinedPrefix).Select(f => f.Detail).OrderBy(d => d).ToArray();
        CollectionAssert.AreEqual(new[] { "qqq", "zzz" }, details);
        Assert.AreEqual(1, set.Files.Count);
        Assert.IsTrue(set.FindCategory("Blind.A", "C2")!.IsDetached);
        Assert.IsNull(set.FindPolicy("Blind.A", "Pol")!.SupportedOn);
    }

    [TestMethod]
    public void Sec19_UndefinedUsingFixture_NamesTheNamespace_AndKeepsTheFileAndPolicy()
    {
        var set = LoadBad("UndefinedUsing");
        var failure = One(set, TemplateFailureCode.UndefinedUsing);
        Assert.AreEqual("BWC.Policies.Bad.UndefinedUsing.Ghost", failure.Detail, true);
        StringAssert.EndsWith(failure.FilePath, "BwcUndefinedUsing.admx", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(1, set.Files.Count);
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual(1, set.Failures.Count);
    }

    [TestMethod]
    public void Sec19_UndefinedUsing_IsOncePerDeclaration()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"], usings: Using("g", "Blind.Ghost"));
        Std(t, "b", "Blind.B", P("Pol"), ["Pol"], usings: Using("g", "Blind.Ghost"));
        var set = t.Load();
        var failures = Of(set, TemplateFailureCode.UndefinedUsing);
        Assert.AreEqual(2, failures.Count);
        Assert.AreEqual(2, failures.Select(f => f.FilePath.ToLowerInvariant()).Distinct().Count());
        Assert.AreEqual(2, set.Files.Count);
    }

    // ---------------------------------------------------------------- 20, 45. Duplicate names

    [TestMethod]
    public void Sec20_DuplicatePolicyFixture_NamesTheName_AndAddsNothing()
    {
        var set = LoadBad("DuplicatePolicy");
        var failure = One(set, TemplateFailureCode.DuplicatePolicy);
        Assert.AreEqual("Pol_Bad", failure.Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec20_DuplicatePolicy_DropsTheFirstOccurrenceToo()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("PolGood") + P("Dup") + P("Dup"), ["PolGood", "Dup"]);
        var set = t.Load();
        One(set, TemplateFailureCode.DuplicatePolicy);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec20_Sec30_NamesThatDifferOnlyInCase_AreDuplicates()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol") + P("POL"), ["Pol", "POL"]);
        var set = t.Load();
        One(set, TemplateFailureCode.DuplicatePolicy);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec20_DuplicateCategory()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol", "C2"],
            categories: "<category name=\"C2\" displayName=\"$(string.C2)\" /><category name=\"c2\" displayName=\"$(string.C2)\" />");
        var set = t.Load();
        One(set, TemplateFailureCode.DuplicateCategory);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec20_DuplicateElementId_RejectsTheFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><text id=\"E\" valueName=\"a\" /><decimal id=\"e\" valueName=\"b\" /></elements>"), ["Pol"]);
        var set = t.Load();
        One(set, TemplateFailureCode.DuplicateElement);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec20_DuplicateSupportedOnDefinition()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var path = Path.Combine(t.Root, "a.admx");
        File.WriteAllText(path, File.ReadAllText(path).Replace("</definitions>", "<definition name=\"sup\" displayName=\"$(string.SUP)\" /></definitions>"));
        var set = t.Load();
        One(set, TemplateFailureCode.DuplicateSupportedOn);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec20_Sec45_SecondFileWithTheSameNamespaceInOneSource_IsDuplicateNamespace_AndDropped()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("PolFirst"), ["PolFirst"]);
        Std(t, "b", "blind.a", P("PolSecond"), ["PolSecond"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.DuplicateNamespace);
        StringAssert.EndsWith(failure.FilePath, "b.admx", StringComparison.OrdinalIgnoreCase);
        Assert.IsNotNull(set.FindPolicy("Blind.A", "PolFirst"));
        Assert.IsNull(set.FindPolicy("Blind.A", "PolSecond"));
        Assert.AreEqual(1, set.Files.Count);
    }

    [TestMethod]
    public void Sec20_EarlierSourceWins_LaterIsNamespaceShadowed()
    {
        using var system = new TempTemplates();
        using var store = new TempTemplates();
        Std(system, "a", "Blind.A", P("FromSystem"), ["FromSystem"]);
        Std(store, "a", "Blind.A", P("FromStore"), ["FromStore"]);
        var set = TemplateLoader.Load([system.Root, store.Root], "en-US");
        var failure = One(set, TemplateFailureCode.NamespaceShadowed);
        StringAssert.StartsWith(failure.FilePath, store.Root, StringComparison.OrdinalIgnoreCase);
        Assert.IsNotNull(set.FindPolicy("Blind.A", "FromSystem"));
        Assert.IsNull(set.FindPolicy("Blind.A", "FromStore"));
    }

    [TestMethod]
    public void Sec20_TwoNamespacesMayDefineTheSameNames()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Same"), ["Same"]);
        Std(t, "b", "Blind.B", P("Same"), ["Same"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.IsNotNull(set.FindPolicy("Blind.A", "Same"));
        Assert.IsNotNull(set.FindPolicy("Blind.B", "Same"));
        Assert.AreEqual(2, set.Policies.Count);
    }

    // ---------------------------------------------------------------- 21. Category cycles

    [TestMethod]
    public void Sec21_SelfParent_IsACycle_AndTheCategoryIsDetached()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol", "Me"],
            categories: "<category name=\"Me\" displayName=\"$(string.Me)\"><parentCategory ref=\"Me\" /></category>");
        var set = t.Load();
        One(set, TemplateFailureCode.CategoryCycle);
        var me = set.FindCategory("Blind.A", "Me")!;
        Assert.IsTrue(me.IsDetached);
        Assert.IsFalse(me.IsRoot);
        Assert.IsFalse(set.RootCategories.Contains(me));
        Assert.AreEqual(1, set.Files.Count);
    }

    [TestMethod]
    public void Sec21_ThreeCycle_IsCutAtTheFirstMemberInSourceOrder_ChildrenStayAttached()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol", "CA", "CB", "CC", "CD"],
            categories:
                "<category name=\"CA\" displayName=\"$(string.CA)\"><parentCategory ref=\"CB\" /></category>" +
                "<category name=\"CB\" displayName=\"$(string.CB)\"><parentCategory ref=\"CC\" /></category>" +
                "<category name=\"CC\" displayName=\"$(string.CC)\"><parentCategory ref=\"CA\" /></category>" +
                "<category name=\"CD\" displayName=\"$(string.CD)\"><parentCategory ref=\"CA\" /></category>");
        var set = t.Load();
        One(set, TemplateFailureCode.CategoryCycle);
        var a = set.FindCategory("Blind.A", "CA")!;
        var b = set.FindCategory("Blind.A", "CB")!;
        var c = set.FindCategory("Blind.A", "CC")!;
        var d = set.FindCategory("Blind.A", "CD")!;
        Assert.IsNull(a.Parent, "the first member of the loop loses its parent");
        Assert.IsTrue(a.IsDetached);
        Assert.AreSame(c, b.Parent);
        Assert.AreSame(a, c.Parent);
        Assert.AreSame(a, d.Parent);
        // No walk of parents loops, and no root reaches the loop.
        foreach (var category in set.Categories)
        {
            var hops = 0;
            for (var x = category; x is not null; x = x.Parent)
            {
                Assert.IsTrue(++hops <= set.Categories.Count, "parent walk must end");
            }
        }

        Assert.IsFalse(set.RootCategories.Contains(a));
        Assert.IsTrue(set.RootCategories.Select(r => r.Name).Contains("Root"));
    }

    // ---------------------------------------------------------------- 22. Parent in another file

    [TestMethod]
    public void Sec22_ParentInAFileThatLoadsLater_IsResolved()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", cat: "Child"), ["Pol", "Child"],
            categories: "<category name=\"Child\" displayName=\"$(string.Child)\"><parentCategory ref=\"base:Top\" /></category>",
            usings: Using("base", "Blind.Z"));
        var zBody = "<categories><category name=\"Top\" displayName=\"$(string.Top)\" /></categories>";
        t.Write("z.admx", AdmxText("Blind.Z", "zz", zBody));
        t.Write(Path.Combine("en-US", "z.adml"), AdmlText(Strings("Top")));
        var set = t.Load();
        FailureAssert.None(set);
        var child = set.FindCategory("Blind.A", "Child")!;
        Assert.AreSame(set.FindCategory("Blind.Z", "Top"), child.Parent);
        Assert.IsTrue(set.FindCategory("Blind.Z", "Top")!.IsRoot);
        CollectionAssert.Contains(set.FindCategory("Blind.Z", "Top")!.Children.ToList(), child);
    }

    [TestMethod]
    public void Sec22_OwnPrefixAndNoPrefix_MeanTheSameFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol", "C1", "C2"],
            categories:
                "<category name=\"C1\" displayName=\"$(string.C1)\"><parentCategory ref=\"Root\" /></category>" +
                "<category name=\"C2\" displayName=\"$(string.C2)\"><parentCategory ref=\"t:C1\" /></category>");
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual("Root", set.FindCategory("Blind.A", "C1")!.Parent!.Name);
        Assert.AreEqual("C1", set.FindCategory("Blind.A", "C2")!.Parent!.Name);
    }

    [TestMethod]
    public void Sec22_UnresolvedParent_IsUndefinedCategory_AndDetached()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol", "C2"],
            categories: "<category name=\"C2\" displayName=\"$(string.C2)\"><parentCategory ref=\"NoSuch\" /></category>");
        var set = t.Load();
        One(set, TemplateFailureCode.UndefinedCategory);
        var c2 = set.FindCategory("Blind.A", "C2")!;
        Assert.IsTrue(c2.IsDetached);
        Assert.IsFalse(c2.IsRoot);
        Assert.AreEqual(1, set.Files.Count);
    }

    [TestMethod]
    public void Sec22_FixtureParentsResolveAcrossFiles()
    {
        var set = Fixture.Load();
        FailureAssert.None(set);
        var basics = set.Category("Basics");
        Assert.AreEqual("BwcOracle", basics.Parent!.Name);
        Assert.AreEqual(Fixture.BaseNamespace, basics.Parent.Namespace);
        Assert.IsTrue(basics.Parent.IsRoot);
        Assert.AreEqual("Elements", set.Category("Booleans").Parent!.Name);
    }

    // ---------------------------------------------------------------- 23. Policy whose category is missing

    [TestMethod]
    public void Sec23_PolicyWithUnknownCategory_StaysInTheListWithNoCategory()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", cat: "NoSuch") + P("Fine"), ["Pol", "Fine"]);
        var set = t.Load();
        One(set, TemplateFailureCode.UndefinedCategory);
        var policy = set.FindPolicy("Blind.A", "Pol")!;
        Assert.IsNull(policy.Category);
        CollectionAssert.Contains(set.Policies.ToList(), policy);
        Assert.IsFalse(set.Categories.Any(c => c.Policies.Contains(policy)));
        Assert.AreEqual(2, set.Policies.Count);
    }

    [TestMethod]
    public void Sec23_PolicyWithNoParentCategory_IsMissingCategory_AndStays()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", cat: null), ["Pol"]);
        var set = t.Load();
        One(set, TemplateFailureCode.MissingCategory);
        var policy = set.FindPolicy("Blind.A", "Pol")!;
        Assert.IsNull(policy.Category);
        Assert.AreEqual(1, set.Files.Count);
    }

    [TestMethod]
    public void Sec23_PolicyInADetachedCategory_HasACategory_ButIsNotReachableFromTheRoots()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", cat: "Orphan"), ["Pol", "Orphan"],
            categories: "<category name=\"Orphan\" displayName=\"$(string.Orphan)\"><parentCategory ref=\"NoSuch\" /></category>");
        var set = t.Load();
        var policy = set.FindPolicy("Blind.A", "Pol")!;
        Assert.IsNotNull(policy.Category);
        Assert.IsTrue(policy.Category.IsDetached);
        var reachable = new HashSet<PolicyCategory>();
        var stack = new Stack<PolicyCategory>(set.RootCategories);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (reachable.Add(c))
            {
                foreach (var child in c.Children)
                {
                    stack.Push(child);
                }
            }
        }

        Assert.IsFalse(reachable.Contains(policy.Category));
    }

    // ---------------------------------------------------------------- 24. Supported-on

    [TestMethod]
    public void Sec24_UnresolvedSupportedOn_KeepsThePolicy_WithEmptyText()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", sup: "NoSuchSupport"), ["Pol"]);
        var set = t.Load();
        One(set, TemplateFailureCode.UndefinedSupportedOn);
        var policy = set.FindPolicy("Blind.A", "Pol")!;
        Assert.IsNull(policy.SupportedOn);
        Assert.AreEqual(string.Empty, policy.SupportedOnText);
        Assert.AreEqual(1, set.Files.Count);
    }

    [TestMethod]
    public void Sec24_NoSupportedOnElement_HasNoneAndNoFailure()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", sup: null), ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.IsNull(set.FindPolicy("Blind.A", "Pol")!.SupportedOn);
    }

    [TestMethod]
    public void Sec24_ProductsAndRangesInsideADefinition_AreIgnored_OnlyTheDisplayNameIsRead()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        var path = Path.Combine(t.Root, "a.admx");
        File.WriteAllText(path, File.ReadAllText(path).Replace("<definition name=\"SUP\" displayName=\"$(string.SUP)\" />",
            "<definition name=\"SUP\" displayName=\"$(string.SUP)\"><or><reference ref=\"X\" /><range ref=\"Y\" minVersionIndex=\"1\" /></or></definition>"));
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual("SUP text", set.FindPolicy("Blind.A", "Pol")!.SupportedOnText);
    }

    // ---------------------------------------------------------------- 25. Class

    [TestMethod]
    public void Sec25_ClassBothFixturePolicy_IsOnceInTheLists_AndAppliesToBoth()
    {
        var set = Fixture.Load();
        var both = set.Policy("Pol_Class_Both");
        Assert.AreEqual(PolicyClass.Both, both.Class);
        Assert.IsTrue(both.AppliesToMachine);
        Assert.IsTrue(both.AppliesToUser);
        Assert.AreEqual(1, set.Policies.Count(p => ReferenceEquals(p, both)));
        Assert.AreEqual(1, both.Category!.Policies.Count(p => ReferenceEquals(p, both)));
        Assert.IsFalse(set.Policy("Pol_Class_Machine").AppliesToUser);
        Assert.IsTrue(set.Policy("Pol_Class_Machine").AppliesToMachine);
        Assert.IsFalse(set.Policy("Pol_Class_User").AppliesToMachine);
        Assert.IsTrue(set.Policy("Pol_Class_User").AppliesToUser);
    }

    // ---------------------------------------------------------------- 26. Keys and values

    [TestMethod]
    public void Sec26_FixtureLiteralsAndKeys()
    {
        var set = Fixture.Load();
        var dec = set.Policy("Pol_Value_Decimal");
        Assert.AreEqual(PolicyValueKind.Decimal, dec.EnabledValue!.Kind);
        Assert.AreEqual(5UL, dec.EnabledValue.Number);
        Assert.AreEqual(9UL, dec.DisabledValue!.Number);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics", dec.Key);
        Assert.AreEqual("ModeNumber", dec.ValueName);

        var str = set.Policy("Pol_Value_String");
        Assert.AreEqual(PolicyValueKind.String, str.EnabledValue!.Kind);
        Assert.AreEqual("on", str.EnabledValue.Text);
        Assert.AreEqual("off", str.DisabledValue!.Text);

        var enabledOnly = set.Policy("Pol_Value_EnabledOnly");
        Assert.IsNotNull(enabledOnly.EnabledValue);
        Assert.IsNull(enabledOnly.DisabledValue);
        Assert.IsNull(set.Policy("Pol_Value_DisabledOnly").EnabledValue);
        Assert.IsNull(set.Policy("Pol_Value_NoValueName_ElementsOnly").ValueName);
    }

    [TestMethod]
    public void Sec26_ListKeysOverrideInTheDocumentedOrder()
    {
        var set = Fixture.Load();
        var policy = set.Policy("Pol_Value_EnabledListOnly");
        var list = policy.EnabledList!;
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\EnabledListKey", list.DefaultKey);
        Assert.AreEqual(3, list.Items.Count);
        Assert.IsNull(list.Items[0].Key);
        Assert.AreEqual(list.DefaultKey, list.Items[0].EffectiveKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\ItemKeyOverride", list.Items[1].EffectiveKey);
        Assert.AreEqual("ListItemTwo", list.Items[1].ValueName);
        Assert.AreEqual("two", list.Items[1].Value!.Text);
        Assert.AreEqual(list.DefaultKey, list.Items[2].EffectiveKey);
        CollectionAssert.AreEqual(new[] { "ListItemOne", "ListItemTwo", "ListItemThree" }, list.Items.Select(i => i.ValueName).ToArray());
    }

    [TestMethod]
    public void Sec26_ItemWithoutDefaultKey_FallsBackToTheItemKey_ThenThePolicyKey()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<enabledList><item key=\"Software\\Own\" valueName=\"i\"><value><decimal value=\"1\" /></value></item></enabledList>") +
            P("Pol2", "<enabledList defaultKey=\"Software\\Default\"><item valueName=\"i\"><value><decimal value=\"1\" /></value></item></enabledList>"),
            ["Pol", "Pol2"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(@"Software\Own", set.FindPolicy("Blind.A", "Pol")!.EnabledList!.Items[0].EffectiveKey);
        Assert.AreEqual(@"Software\Default", set.FindPolicy("Blind.A", "Pol2")!.EnabledList!.Items[0].EffectiveKey);
    }

    [TestMethod]
    public void Sec26_ElementEffectiveKey_IsItsOwnKeyElseThePolicyKey()
    {
        var set = Fixture.Load();
        var p = set.Policy("Pol_Element_KeyOverride");
        var e = p.Element<TextElement>("OverrideText");
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\ElementKey", e.Key);
        Assert.AreEqual(e.Key, e.EffectiveKey);
        var plain = set.Policy("Pol_Text_Plain").Element<TextElement>("PlainText");
        Assert.IsNull(plain.Key);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Text", plain.EffectiveKey);
    }

    [TestMethod]
    public void Sec26_LongDecimalStringAndDeleteLiterals()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<enabledValue><longDecimal value=\"5000000000\" /></enabledValue><disabledValue><delete /></disabledValue>"),
            ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        var p = set.FindPolicy("Blind.A", "Pol")!;
        Assert.AreEqual(PolicyValueKind.LongDecimal, p.EnabledValue!.Kind);
        Assert.AreEqual(5000000000UL, p.EnabledValue.Number);
        Assert.AreEqual(PolicyValueKind.Delete, p.DisabledValue!.Kind);
    }

    [TestMethod]
    public void Sec26_ValueNameMayBeAbsent_AndAttributeValuesAreNotTrimmed()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", valueName: " v ") + P("Pol2", valueName: null), ["Pol", "Pol2"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(" v ", set.FindPolicy("Blind.A", "Pol")!.ValueName);
        Assert.IsNull(set.FindPolicy("Blind.A", "Pol2")!.ValueName);
    }

    // ---------------------------------------------------------------- 27. Element attributes

    [TestMethod]
    public void Sec27_DecimalAttributes()
    {
        var set = Fixture.Load();
        var mm = set.Policy("Pol_Decimal_MinMax").Element<DecimalElement>("MinMaxNumber");
        Assert.AreEqual(PolicyElementKind.Decimal, mm.Kind);
        Assert.AreEqual(10UL, mm.MinValue);
        Assert.AreEqual(500UL, mm.MaxValue);
        Assert.IsFalse(mm.Required);
        Assert.IsFalse(mm.Soft);
        Assert.IsFalse(mm.StoreAsText);
        Assert.IsTrue(set.Policy("Pol_Decimal_Required").Element<DecimalElement>("RequiredNumber").Required);
        Assert.IsTrue(set.Policy("Pol_Decimal_Soft").Element<DecimalElement>("SoftNumber").Soft);
        Assert.IsTrue(set.Policy("Pol_Decimal_StoreAsText").Element<DecimalElement>("TextNumber").StoreAsText);
        var req = set.Policy("Pol_Decimal_Required").Element<DecimalElement>("RequiredNumber");
        Assert.IsNull(req.MinValue, "absent numbers stay null");
        Assert.IsNull(req.MaxValue);
        var lng = set.Policy("Pol_LongDecimal_Basic").Element<DecimalElement>("LongNumber");
        Assert.AreEqual(PolicyElementKind.LongDecimal, lng.Kind);
        Assert.AreEqual(9000000000000UL, lng.MaxValue);
        var all = set.Policy("Pol_LongDecimal_RequiredSoftText").Element<DecimalElement>("LongTextNumber");
        Assert.IsTrue(all.Required && all.Soft && all.StoreAsText);
    }

    [TestMethod]
    public void Sec27_TextAndMultiTextAttributes()
    {
        var set = Fixture.Load();
        var plain = set.Policy("Pol_Text_Plain").Element<TextElement>("PlainText");
        Assert.IsFalse(plain.Required || plain.Expandable);
        Assert.IsNull(plain.MaxLength);
        Assert.IsTrue(set.Policy("Pol_Text_Expandable").Element<TextElement>("ExpandText").Expandable);
        Assert.AreEqual(12UL, set.Policy("Pol_Text_MaxLength").Element<TextElement>("ShortText").MaxLength);
        Assert.IsTrue(set.Policy("Pol_Text_Required").Element<TextElement>("RequiredText").Required);
        var multi = set.Policy("Pol_MultiText_Limits").Element<MultiTextElement>("LimitedLines");
        Assert.IsTrue(multi.Required);
        Assert.AreEqual(20UL, multi.MaxLength);
        Assert.AreEqual(3UL, multi.MaxStrings);
        var basic = set.Policy("Pol_MultiText_Basic").Element<MultiTextElement>("MultiLines");
        Assert.IsNull(basic.MaxLength);
        Assert.IsNull(basic.MaxStrings);
    }

    [TestMethod]
    public void Sec27_BooleanValuesAndLists()
    {
        var set = Fixture.Load();
        var tf = set.Policy("Pol_Bool_TrueFalseValues").Element<BooleanElement>("TrueFalseCheck");
        Assert.AreEqual(10UL, tf.TrueValue!.Number);
        Assert.AreEqual(20UL, tf.FalseValue!.Number);
        var noFalse = set.Policy("Pol_Bool_NoFalseValue").Element<BooleanElement>("NoFalseCheck");
        Assert.IsNotNull(noFalse.TrueValue);
        Assert.IsNull(noFalse.FalseValue);
        var strings = set.Policy("Pol_Bool_StringValues").Element<BooleanElement>("StringCheck");
        Assert.AreEqual("yes", strings.TrueValue!.Text);
        var lists = set.Policy("Pol_Bool_TrueFalseLists").Element<BooleanElement>("ListCheck");
        Assert.AreEqual(2, lists.TrueList!.Items.Count);
        Assert.AreEqual(2, lists.FalseList!.Items.Count);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Bool\TrueListOtherKey", lists.TrueList.Items[1].EffectiveKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Bool\FalseListKey", lists.FalseList.Items[1].EffectiveKey);
    }

    [TestMethod]
    public void Sec27_EnumItemsAndTheirValueLists()
    {
        var set = Fixture.Load();
        var dec = set.Policy("Pol_Enum_DecimalItems").Element<EnumElement>("DecimalChoice");
        Assert.AreEqual(4, dec.Items.Count);
        CollectionAssert.AreEqual(new ulong[] { 0, 1, 2, 3 }, dec.Items.Select(i => i.Value!.Number).ToArray());
        var profile = set.Policy("Pol_Enum_ItemValueList").Element<EnumElement>("ProfileChoice");
        Assert.AreEqual(3, profile.Items.Count);
        Assert.IsNull(profile.Items[1].ValueList);
        var a = profile.Items[0].ValueList!;
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Choice\ProfileA", a.DefaultKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Choice\ProfileAOther", a.Items[1].EffectiveKey);
        Assert.IsNull(profile.Items[2].ValueList!.DefaultKey);
        Assert.IsTrue(set.Policy("Pol_Enum_Required").Element<EnumElement>("RequiredChoice").Required);
    }

    [TestMethod]
    public void Sec27_ListFlags_AndAbsentValuePrefixIsDifferentFromEmpty()
    {
        var set = Fixture.Load();
        var plain = set.Policy("Pol_List_Plain").Element<ListElement>("PlainList");
        Assert.IsFalse(plain.Additive || plain.ExplicitValue || plain.Expandable);
        Assert.IsNull(plain.ValuePrefix);
        Assert.AreEqual(string.Empty, set.Policy("Pol_List_EmptyPrefix").Element<ListElement>("EmptyPrefixList").ValuePrefix);
        Assert.AreEqual("Item", set.Policy("Pol_List_ValuePrefix").Element<ListElement>("PrefixList").ValuePrefix);
        Assert.IsTrue(set.Policy("Pol_List_Additive").Element<ListElement>("AdditiveList").Additive);
        Assert.IsTrue(set.Policy("Pol_List_ExplicitValue").Element<ListElement>("ExplicitList").ExplicitValue);
        Assert.IsTrue(set.Policy("Pol_List_Expandable").Element<ListElement>("ExpandList").Expandable);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Lists\Plain", plain.Key);
    }

    [TestMethod]
    public void Sec27_UnknownElementKindIsIgnored_ClientExtensionIsRecorded()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><futureKind id=\"F\" /><text id=\"T\" valueName=\"T\" clientExtension=\"{GUID-1}\" /></elements>",
                attrs: "clientExtension=\"{GUID-2}\""),
            ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        var policy = set.FindPolicy("Blind.A", "Pol")!;
        Assert.AreEqual(1, policy.Elements.Count);
        Assert.AreEqual("{GUID-1}", policy.Elements[0].ClientExtension);
        Assert.AreEqual("{GUID-2}", policy.ClientExtension);
    }

    [TestMethod]
    public void Sec01_UnreadAttributesAndElements_AreIgnoredWithoutFailure()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<whatever a=\"b\"><deeper /></whatever>", attrs: "madeUp=\"x\" another=\"y\""), ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.IsNotNull(set.FindPolicy("Blind.A", "Pol"));
    }

    // ---------------------------------------------------------------- 28, 43. Presentation controls

    [TestMethod]
    public void Sec28_ControlsOfTheFixture_KeepKindsLabelsDefaultsAndAttributes()
    {
        var set = Fixture.Load();
        var combo = set.Policy("Pol_Text_ComboBoxSuggestions").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.ComboBox, combo.Kind);
        Assert.AreEqual("ComboText", combo.RefId);
        Assert.AreEqual("Pick or type a city:", combo.Label);
        Assert.AreEqual("Oslo", combo.DefaultValue);
        CollectionAssert.AreEqual(new[] { "Paris", "Oslo", "Lima", "Cairo" }, combo.Suggestions.ToArray());
        Assert.AreEqual(true, set.Policy("Pol_Text_ComboBoxNoSortRequired").Presentation!.Controls.Single().NoSort);

        var num = set.Policy("Pol_Decimal_MinMax").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.DecimalTextBox, num.Kind);
        Assert.AreEqual("25", num.DefaultValue);
        Assert.AreEqual(true, num.Spin);
        Assert.AreEqual(5UL, num.SpinStep);
        Assert.AreEqual("Number from 10 to 500:", num.Label);
        Assert.IsNull(num.DefaultChecked, "an attribute of another kind is never read");

        var longNum = set.Policy("Pol_LongDecimal_Basic").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.LongDecimalTextBox, longNum.Kind);

        var drop = set.Policy("Pol_Enum_NoSortDefaultItem").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.DropdownList, drop.Kind);
        Assert.AreEqual(true, drop.NoSort);
        Assert.AreEqual(1UL, drop.DefaultItem);

        var check = set.Policy("Pol_Bool_StringValues").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.CheckBox, check.Kind);
        Assert.AreEqual(true, check.DefaultChecked);
        Assert.AreEqual("String check", check.Label);

        var multi = set.Policy("Pol_MultiText_Limits").Presentation!.Controls;
        Assert.AreEqual(PresentationControlKind.Text, multi[0].Kind);
        Assert.AreEqual("At most three lines.", multi[0].Label);
        Assert.AreEqual(PresentationControlKind.MultiTextBox, multi[1].Kind);
        Assert.AreEqual(3UL, multi[1].DefaultHeight);

        Assert.AreEqual(PresentationControlKind.ListBox, set.Policy("Pol_List_Plain").Presentation!.Controls.Single().Kind);
        var textBox = set.Policy("Pol_Text_Plain").Presentation!.Controls.Single();
        Assert.AreEqual(PresentationControlKind.TextBox, textBox.Kind);
        Assert.AreEqual("plain default", textBox.DefaultValue);
        Assert.AreEqual("Plain text:", textBox.Label);
    }

    [TestMethod]
    public void Sec28_ControlsAreLinkedToTheirElements_InAdmlOrder()
    {
        var set = Fixture.Load();
        var policy = set.Policy("Pol_Bool_TwoChecksReverseOrder");
        var controls = policy.Presentation!.Controls;
        CollectionAssert.AreEqual(new[] { "ZetaCheck", "AlphaCheck" }, controls.Select(c => c.RefId).ToArray());
        Assert.AreSame(policy.Elements[0], controls[0].Element);
        Assert.AreSame(policy.Elements[1], controls[1].Element);
    }

    [TestMethod]
    public void Sec28_Sec43_StrayInvalidAttributesAndUnknownControls_NeverFail()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><text id=\"T\" valueName=\"T\" /><enum id=\"E\" valueName=\"E\"><item displayName=\"$(string.Pol)\"><value><decimal value=\"1\" /></value></item></enum></elements>",
                presentation: "$(presentation.Pr)"),
            ["Pol"],
            presentations:
                "<presentation id=\"Pr\">" +
                "<textBox refId=\"T\" spin=\"banana\" spinStep=\"-5\" noSort=\"zzz\" defaultItem=\"q\" defaultChecked=\"no\" defaultHeight=\"x\" required=\"huh\"><label>x</label></textBox>" +
                "<dropdownList refId=\"E\" oSort=\"true\" defaultChecked=\"maybe\">Pick</dropdownList>" +
                "<futureControl refId=\"T\" />" +
                "</presentation>");
        var set = t.Load();
        FailureAssert.None(set);
        var controls = set.FindPolicy("Blind.A", "Pol")!.Presentation!.Controls;
        Assert.AreEqual(2, controls.Count, "an unknown control kind is ignored");
        Assert.IsNull(controls[0].Spin);
        Assert.IsNull(controls[0].DefaultChecked);
    }

    [TestMethod]
    public void Sec28_ShippedSchemaBreakers_AreKeptOrIgnored_WithoutFailure()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><boolean id=\"B\" valueName=\"B\" /><list id=\"L\" key=\"Software\\L\" /><enum id=\"E\" valueName=\"E\"><item displayName=\"$(string.Pol)\"><value><decimal value=\"1\" /></value></item></enum></elements>",
                presentation: "$(presentation.Pr)"),
            ["Pol"],
            presentations:
                "<presentation id=\"Pr\">" +
                "<checkBox refId=\"B\" defaultItem=\"1\">b</checkBox>" +
                "<listBox refId=\"L\" required=\"true\">l</listBox>" +
                "<dropdownList refId=\"E\" oSort=\"true\">e</dropdownList>" +
                "</presentation>");
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(3, set.FindPolicy("Blind.A", "Pol")!.Presentation!.Controls.Count);
    }

    // ---------------------------------------------------------------- 29. Text details

    [TestMethod]
    public void Sec29_AmpersandsAreKeptAsWritten()
    {
        var set = Fixture.Load();
        Assert.AreEqual("Tools & Utilities", set.Category("Ampersand").DisplayName);
        Assert.AreEqual("Research & Development flag", set.Policy("Pol_Amp_DisplayName").DisplayName);
        var controls = set.Policy("Pol_Amp_LabelSingle").Presentation!.Controls;
        Assert.AreEqual("Name & address:", controls[0].Label);
        Assert.AreEqual("Fish & Chips", controls[0].DefaultValue);
        Assert.AreEqual("Save & close", controls[1].Label);
        var items = set.Policy("Pol_Amp_LabelSingle").Element<EnumElement>("AmpChoice").Items;
        Assert.AreEqual("Tom & Jerry", items[0].DisplayName);
        Assert.AreEqual("Fish && Chips:", set.Policy("Pol_Amp_LabelDouble").Presentation!.Controls.Single().Label);
    }

    [TestMethod]
    public void Sec29_ExplainText_KeepsNewlinesAndBlanks_NormalizedToLineFeed()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", attrs: "explainText=\"$(string.Help)\"") + P("Empty", attrs: "explainText=\"$(string.None)\"") + P("Omitted"),
            ["Pol", "Empty", "Omitted"],
            extraStrings: "<string id=\"Help\">  lead\r\nsecond\r\n\r\n third  </string><string id=\"None\"></string>");
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual("  lead\nsecond\n\n third  ", set.FindPolicy("Blind.A", "Pol")!.ExplainText);
        Assert.AreEqual(string.Empty, set.FindPolicy("Blind.A", "Empty")!.ExplainText);
        Assert.AreEqual(string.Empty, set.FindPolicy("Blind.A", "Omitted")!.ExplainText);
    }

    [TestMethod]
    public void Sec29_FixtureExplainTexts()
    {
        var set = Fixture.Load();
        var multi = set.Policy("Pol_Explain_MultiParagraph").ExplainText;
        StringAssert.Contains(multi, "\n");
        Assert.IsFalse(multi.Contains('\r'));
        Assert.AreEqual(string.Empty, set.Policy("Pol_Explain_EmptyString").ExplainText);
        Assert.AreEqual(string.Empty, set.Policy("Pol_Explain_AttributeOmitted").ExplainText);
    }

    // ---------------------------------------------------------------- 30. Case

    [TestMethod]
    public void Sec30_NamesCompareIgnoringCase_AndKeepTheWrittenCase()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("MixedCase", cat: "t:ROOT"), ["MixedCase"]);
        var set = t.Load();
        FailureAssert.None(set);
        var policy = set.FindPolicy("blind.a", "MIXEDCASE");
        Assert.IsNotNull(policy);
        Assert.AreEqual("MixedCase", policy.Name);
        Assert.AreEqual("Blind.A", policy.Namespace);
        Assert.AreEqual("Root", policy.Category!.Name);
        Assert.IsNotNull(set.FindCategory("BLIND.A", "root"));
        Assert.IsNotNull(set.FindSupportedOn("blind.a", "sup"));
        Assert.IsNotNull(set.FindFile("BLIND.A"));
    }

    // ---------------------------------------------------------------- 31. Determinism and order

    [TestMethod]
    public void Sec31_LoadingTwice_GivesTheSameListsInTheSameOrder()
    {
        var first = Fixture.Load();
        var second = Fixture.Load();
        CollectionAssert.AreEqual(first.Files.Select(f => f.AdmxPath).ToArray(), second.Files.Select(f => f.AdmxPath).ToArray());
        CollectionAssert.AreEqual(first.Categories.Select(c => c.Namespace + ":" + c.Name).ToArray(), second.Categories.Select(c => c.Namespace + ":" + c.Name).ToArray());
        CollectionAssert.AreEqual(first.Policies.Select(c => c.FullName).ToArray(), second.Policies.Select(c => c.FullName).ToArray());
        CollectionAssert.AreEqual(first.SupportedOn.Select(c => c.Namespace + ":" + c.Name).ToArray(), second.SupportedOn.Select(c => c.Namespace + ":" + c.Name).ToArray());
        CollectionAssert.AreEqual(first.Failures.ToArray(), second.Failures.ToArray());
    }

    [TestMethod]
    public void Sec31_FilesAreReadByNameIgnoringCase_ThenInDocumentOrder()
    {
        using var t = new TempTemplates();
        Std(t, "b", "Blind.B", P("B2") + P("B1"), ["B1", "B2"]);
        Std(t, "C", "Blind.C", P("C1"), ["C1"]);
        Std(t, "a", "Blind.A", P("A1"), ["A1"]);
        var set = t.Load();
        FailureAssert.None(set);
        CollectionAssert.AreEqual(new[] { "Blind.A", "Blind.B", "Blind.C" }, set.Files.Select(f => f.Namespace).ToArray());
        CollectionAssert.AreEqual(new[] { "A1", "B2", "B1", "C1" }, set.Policies.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, set.Files.Select(f => f.Order).ToArray());
    }

    [TestMethod]
    public void Sec31_SourcesKeepTheOrderGiven()
    {
        using var first = new TempTemplates();
        using var second = new TempTemplates();
        Std(first, "z", "Blind.Z", P("Z1"), ["Z1"]);
        Std(second, "a", "Blind.A", P("A1"), ["A1"]);
        var set = TemplateLoader.Load([first.Root, second.Root], "en-US");
        FailureAssert.None(set);
        CollectionAssert.AreEqual(new[] { "Blind.Z", "Blind.A" }, set.Files.Select(f => f.Namespace).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1 }, set.Files.Select(f => f.SourceIndex).ToArray());
    }

    // ---------------------------------------------------------------- 32. Cancellation

    [TestMethod]
    public void Sec32_ACancelledToken_ThrowsOperationCanceled()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol"), ["Pol"]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => TemplateLoader.Load([t.Root], "en-US", cts.Token));
    }

    [TestMethod]
    public void Sec32_ANullFolderList_IsAnArgumentError()
    {
        Assert.Throws<ArgumentNullException>(() => TemplateLoader.Load(null!, "en-US"));
    }

    // ---------------------------------------------------------------- 33. Empty and unusual sets

    [TestMethod]
    public void Sec33_NoFolders_GiveAnEmptySetWithNoFailures()
    {
        var set = TemplateLoader.Load([], "en-US");
        FailureAssert.None(set);
        FailureAssert.NothingLoaded(set);
        Assert.AreEqual(0, set.RootCategories.Count);
    }

    [TestMethod]
    public void Sec33_FolderWithoutTemplates_GivesAnEmptySet()
    {
        using var t = new TempTemplates();
        Directory.CreateDirectory(Path.Combine(t.Root, "en-US"));
        t.Write("readme.txt", "hello");
        var set = t.Load();
        FailureAssert.None(set);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec33_TheGoodFixture_LoadsWithZeroFailures_AndTheDocumentedCounts()
    {
        var set = Fixture.Load();
        FailureAssert.None(set);
        Assert.AreEqual(2, set.Files.Count);
        Assert.AreEqual(70, set.Policies.Count);
        Assert.AreEqual(18, set.Categories.Count);
        Assert.AreEqual(4, set.SupportedOn.Count);
        Assert.AreEqual(1, set.RootCategories.Count);
        Assert.AreEqual("BwcOracle", set.RootCategories[0].Name);
        Assert.AreEqual("en-US", set.Culture, true);
    }

    [TestMethod]
    public void Sec33_GoodPlusBad_LoadTogether_OnlyTheBadOneFails()
    {
        using var t = new TempTemplates();
        Std(t, "good", "Blind.Good", P("PolGood"), ["PolGood"]);
        Std(t, "bad", "Blind.Bad", P("PolBad", attrs: "explainText=\"$(string.Missing)\""), ["PolBad"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.UndefinedString);
        StringAssert.EndsWith(failure.FilePath, "bad.admx", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.IsNotNull(set.FindPolicy("Blind.Good", "PolGood"));
        Assert.IsNull(set.FindPolicy("Blind.Bad", "PolBad"));
        Assert.AreEqual(1, set.Files.Count);
    }

    // ---------------------------------------------------------------- 35. A failed file adds nothing

    [TestMethod]
    public void Sec35_GoodPolicyBeforeAnUndefinedString_DoesNotLeak()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("PolGood") + P("PolBad", attrs: "explainText=\"$(string.Missing)\""), ["PolGood", "PolBad"]);
        var set = t.Load();
        One(set, TemplateFailureCode.UndefinedString);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec35_GoodPolicyBeforeADuplicate_DoesNotLeak_AndCategoriesAndSupportedOnDoNot()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Dup") + P("Dup"), ["Dup"]);
        var set = t.Load();
        FailureAssert.NothingLoaded(set);
        Assert.IsNull(set.FindCategory("Blind.A", "Root"));
        Assert.IsNull(set.FindSupportedOn("Blind.A", "SUP"));
        Assert.IsNull(set.FindFile("Blind.A"));
    }

    [TestMethod]
    public void Sec35_BadFileInOneFolderAndGoodInAnother_GoodIsComplete_AndNoFailureNamesIt()
    {
        using var one = new TempTemplates();
        using var two = new TempTemplates();
        Std(one, "bad", "Blind.Bad", P("Dup") + P("Dup"), ["Dup"]);
        Std(two, "good", "Blind.Good", P("PolGood"), ["PolGood"]);
        var set = TemplateLoader.Load([one.Root, two.Root], "en-US");
        Assert.IsNotNull(set.FindPolicy("Blind.Good", "PolGood"));
        Assert.IsNotNull(set.FindCategory("Blind.Good", "Root"));
        Assert.IsNotNull(set.FindSupportedOn("Blind.Good", "SUP"));
        Assert.IsTrue(set.Failures.All(f => !f.FilePath.EndsWith("good.admx", StringComparison.OrdinalIgnoreCase)
                                            && !f.FilePath.EndsWith("good.adml", StringComparison.OrdinalIgnoreCase)),
            FailureAssert.Describe(set));
    }

    [TestMethod]
    public void Sec35_ACategoryAndSupportedOnOfARejectedFile_AreNotFoundByASibling()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.Bad", P("Dup") + P("Dup"), ["Dup"]);
        Std(t, "b", "Blind.Good",
            P("Pol", cat: "bad:Root", sup: "bad:SUP"), ["Pol"], usings: Using("bad", "Blind.Bad"));
        var set = t.Load();
        var policy = set.FindPolicy("Blind.Good", "Pol")!;
        Assert.IsNull(policy.Category);
        Assert.IsNull(policy.SupportedOn);
        Assert.IsTrue(Of(set, TemplateFailureCode.UndefinedCategory).Count >= 1, FailureAssert.Describe(set));
        Assert.IsTrue(Of(set, TemplateFailureCode.UndefinedSupportedOn).Count >= 1, FailureAssert.Describe(set));
    }

    [TestMethod]
    public void Sec35_DanglingReferencesDoNotRejectTheFile()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", P("Pol", sup: "NoSuch", cat: "NoCat") + P("Fine"), ["Pol", "Fine"],
            usings: Using("g", "Blind.Ghost"));
        var set = t.Load();
        Assert.AreEqual(1, set.Files.Count);
        Assert.AreEqual(2, set.Policies.Count);
        Assert.IsTrue(set.Failures.Select(f => f.Code).All(c =>
            c is TemplateFailureCode.UndefinedUsing or TemplateFailureCode.UndefinedCategory or TemplateFailureCode.UndefinedSupportedOn));
    }

    // ---------------------------------------------------------------- 36. List items and boolean elements

    private static string ListXml(string kind, string items, string? defaultKey = null) =>
        $"<{kind}{(defaultKey is null ? "" : $" defaultKey=\"{defaultKey}\"")}>{items}</{kind}>";

    private const string ItemNoKey = "<item valueName=\"I\"><value><decimal value=\"1\" /></value></item>";
    private const string ItemWithKey = "<item key=\"Software\\K\" valueName=\"I\"><value><decimal value=\"1\" /></value></item>";

    private static string PolicyWithList(string kind, string items, string? defaultKey = null) => kind switch
    {
        "enabledList" or "disabledList" => P("Pol", ListXml(kind, items, defaultKey)),
        "trueList" => P("Pol", $"<elements><boolean id=\"B\" valueName=\"B\">{ListXml(kind, items, defaultKey)}</boolean></elements>"),
        "falseList" => P("Pol", $"<elements><boolean id=\"B\" valueName=\"B\">{ListXml(kind, items, defaultKey)}</boolean></elements>"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [TestMethod]
    [DataRow("enabledList")]
    [DataRow("disabledList")]
    [DataRow("trueList")]
    [DataRow("falseList")]
    public void Sec36_ItemWithoutKeyAndNoDefaultKey_IsMissingAttributeKey(string kind)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", PolicyWithList(kind, ItemNoKey), ["Pol"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual("key", failure.Detail, true);
        StringAssert.EndsWith(failure.FilePath, "a.admx", StringComparison.OrdinalIgnoreCase);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    [DataRow("enabledList")]
    [DataRow("disabledList")]
    [DataRow("trueList")]
    [DataRow("falseList")]
    public void Sec36_ListWithDefaultKey_OrItemsWithKeys_IsAccepted(string kind)
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", PolicyWithList(kind, ItemNoKey, @"Software\D"), ["Pol"]);
        Std(t, "b", "Blind.B", PolicyWithList(kind, ItemWithKey), ["Pol"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(2, set.Files.Count);
    }

    [TestMethod]
    public void Sec36_ADefaultKeyOnTheOtherList_DoesNotRescueAnItem()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", ListXml("enabledList", ItemNoKey) + ListXml("disabledList", ItemWithKey, @"Software\Other")), ["Pol"]);
        var set = t.Load();
        One(set, TemplateFailureCode.MissingAttribute);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec36_ElementKeyPolicyKeyAndAnotherElementsDefaultKey_DoNotRescueAnItem()
    {
        using var t = new TempTemplates();
        var inner =
            "<elements>" +
            "<boolean id=\"A\" valueName=\"A\" key=\"Software\\ElementKey\">" + ListXml("trueList", ItemWithKey, @"Software\Elsewhere") + "</boolean>" +
            "<boolean id=\"B\" valueName=\"B\" key=\"Software\\ElementKey\">" + ListXml("trueList", ItemNoKey) + "</boolean>" +
            "</elements>";
        Std(t, "a", "Blind.A", P("Pol", inner), ["Pol"]);
        var set = t.Load();
        Assert.AreEqual(1, Of(set, TemplateFailureCode.MissingAttribute).Count, FailureAssert.Describe(set));
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec36_SeveralItemsMissingKey_GiveOneFailureEach_InDocumentOrder()
    {
        using var t = new TempTemplates();
        var items = ItemNoKey + "\n" + ItemNoKey + "\n" + ItemNoKey;
        Std(t, "a", "Blind.A", PolicyWithList("enabledList", items), ["Pol"]);
        var set = t.Load();
        var failures = Of(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual(3, failures.Count);
        Assert.IsTrue(failures.All(f => f.Detail.Equals("key", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(failures.Select(f => f.Line).OrderBy(l => l).ToArray(), failures.Select(f => f.Line).ToArray());
        Assert.AreEqual(3, failures.Select(f => f.Line).Distinct().Count());
    }

    [TestMethod]
    public void Sec36_ListItemFixture_ReportsTheClassicPosition_21_39()
    {
        var set = LoadBad("ListItemNoKey");
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual("key", failure.Detail, true);
        StringAssert.EndsWith(failure.FilePath, "BwcListItemNoKey.admx", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(21, failure.Line);
        Assert.AreEqual(39, failure.Position);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec36_BooleanFixture_ReportsTheClassicPosition_21_31()
    {
        var set = LoadBad("BooleanNoValueName");
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual("valueName", failure.Detail, true);
        StringAssert.EndsWith(failure.FilePath, "BwcBooleanNoValueName.admx", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(21, failure.Line);
        Assert.AreEqual(31, failure.Position);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec36_PositionIsTheClosingAngleBracket_OnOneLine()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A", "\n  " + PolicyWithList("enabledList", "\n        " + ItemNoKey + "\n"), ["Pol"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        var text = Raw(Path.Combine(t.Root, "a.admx"));
        var (line, col) = ClosePosition(text, "<item valueName=\"I\"");
        Assert.AreEqual(line, failure.Line);
        Assert.AreEqual(col, failure.Position);
    }

    [TestMethod]
    public void Sec36_PositionOfAStartTagOverTwoLines_IsTheLineOfTheClosingBracket()
    {
        using var t = new TempTemplates();
        var item = "<item\n          valueName=\"I\"\n        >\n<value><decimal value=\"1\" /></value></item>";
        Std(t, "a", "Blind.A", PolicyWithList("enabledList", item), ["Pol"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        var (line, col) = ClosePosition(Raw(Path.Combine(t.Root, "a.admx")), "<item\n");
        Assert.AreEqual(line, failure.Line);
        Assert.AreEqual(col, failure.Position);
    }

    [TestMethod]
    public void Sec36_PositionOfASelfClosingTag_IsTheClosingBracketOfTheSlashGreater()
    {
        using var t = new TempTemplates();
        var inner = "<elements>\n    <boolean id=\"B\" />\n</elements>";
        Std(t, "a", "Blind.A", P("Pol", inner), ["Pol"]);
        var set = t.Load();
        var failure = One(set, TemplateFailureCode.MissingAttribute);
        Assert.AreEqual("valueName", failure.Detail, true);
        var (line, col) = ClosePosition(Raw(Path.Combine(t.Root, "a.admx")), "<boolean id=\"B\"");
        Assert.AreEqual(line, failure.Line);
        Assert.AreEqual(col, failure.Position);
    }

    [TestMethod]
    public void Sec36_BooleanWithoutValueName_IsRefused_EvenWithOnlyTrueAndFalseValues()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><boolean id=\"B\"><trueValue><decimal value=\"1\" /></trueValue><falseValue><decimal value=\"0\" /></falseValue></boolean></elements>"),
            ["Pol"]);
        var set = t.Load();
        Assert.AreEqual("valueName", One(set, TemplateFailureCode.MissingAttribute).Detail, true);
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void Sec36_BooleanWithValueName_OrWithAnEmptyValueName_IsAccepted()
    {
        using var t = new TempTemplates();
        Std(t, "a", "Blind.A",
            P("Pol", "<elements><boolean id=\"B\" valueName=\"V\" /></elements>") +
            P("Pol2", "<elements><boolean id=\"B\" valueName=\"\" /></elements>"),
            ["Pol", "Pol2"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(2, set.Policies.Count);
    }

    [TestMethod]
    public void Sec36_NoExtraStrictness_OnOtherShapes()
    {
        using var t = new TempTemplates();
        var inner =
            "<elements>" +
            "<decimal id=\"D\" /><text id=\"T\" /><list id=\"L\" key=\"Software\\L\" />" +
            "<enum id=\"E\"><item displayName=\"$(string.Pol)\"><value><decimal value=\"1\" /></value>" +
            "<valueList><item valueName=\"Only\"><value><decimal value=\"2\" /></value></item></valueList></item></enum>" +
            "</elements>";
        Std(t, "a", "Blind.A", P("Pol", inner, valueName: null) + P("Pol2", ListXml("enabledList", ItemWithKey)), ["Pol", "Pol2"]);
        var set = t.Load();
        FailureAssert.None(set);
        Assert.AreEqual(2, set.Policies.Count);
    }
}
