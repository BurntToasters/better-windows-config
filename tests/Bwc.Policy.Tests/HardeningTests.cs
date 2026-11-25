using System.Text;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class HardeningTests
{
    private const string GoodPolicy =
        """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""";

    [TestMethod]
    public void OversizedFile_IsRefusedBeforeParsing()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("Big", "T.Big", GoodPolicy, """<string id="P">P</string>""");
        var admx = Path.Combine(temp.Root, "Big.admx");
        using (var stream = new FileStream(admx, FileMode.Create, FileAccess.Write))
        {
            // Not well formed on purpose: a parse would report MalformedXml, so FileTooLarge proves no parse happened.
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'<');
            for (var written = 0L; written <= AdmxLimits.MaxFileBytes; written += chunk.Length)
            {
                stream.Write(chunk, 0, chunk.Length);
            }
        }

        Assert.IsTrue(new FileInfo(admx).Length > AdmxLimits.MaxFileBytes);
        var set = temp.Load();
        var failure = FailureAssert.Single(set, TemplateFailureCode.FileTooLarge);
        Assert.AreEqual(admx, failure.FilePath);
        Assert.AreEqual(1, set.Failures.Count);
        Assert.AreEqual(0, set.Files.Count);
    }

    [TestMethod]
    public void FileOfExactlyTheCap_IsNotRefusedForSize()
    {
        using var temp = new TempTemplates();
        var path = temp.Write("Edge.admx", string.Empty);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var head = Encoding.UTF8.GetBytes(TempTemplates.Header + "<policyDefinitions><policyNamespaces><target prefix=\"e\" namespace=\"T.Edge\" /></policyNamespaces>");
            var tail = Encoding.UTF8.GetBytes("</policyDefinitions>");
            stream.Write(head);
            var padding = new byte[AdmxLimits.MaxFileBytes - head.Length - tail.Length];
            Array.Fill(padding, (byte)' ');
            stream.Write(padding);
            stream.Write(tail);
        }

        Assert.AreEqual(AdmxLimits.MaxFileBytes, new FileInfo(path).Length);
        var set = temp.Load();
        Assert.AreEqual(0, set.Failures.Count(f => f.Code == TemplateFailureCode.FileTooLarge));
        Assert.AreEqual(TemplateFailureCode.MissingAdml, FailureAssert.Single(set, TemplateFailureCode.MissingAdml).Code);
    }

    [TestMethod]
    public void NestingOverTheDepthLimit_IsRefused()
    {
        using var temp = new TempTemplates();
        var deep = new StringBuilder(TempTemplates.Header);
        for (var i = 0; i < AdmxLimits.MaxXmlDepth + 5; i++)
        {
            deep.Append("<a>");
        }

        for (var i = 0; i < AdmxLimits.MaxXmlDepth + 5; i++)
        {
            deep.Append("</a>");
        }

        var path = temp.Write("Deep.admx", deep.ToString());
        var set = temp.Load();
        Assert.AreEqual(path, FailureAssert.Single(set, TemplateFailureCode.XmlTooDeep).FilePath);
    }

    [TestMethod]
    public void NestingExactlyAtTheLimit_IsAccepted()
    {
        using var temp = new TempTemplates();
        var deep = new StringBuilder(TempTemplates.Header);
        for (var i = 0; i < AdmxLimits.MaxXmlDepth; i++)
        {
            deep.Append("<a>");
        }

        for (var i = 0; i < AdmxLimits.MaxXmlDepth; i++)
        {
            deep.Append("</a>");
        }

        temp.Write("Edge.admx", deep.ToString());
        var set = temp.Load();
        Assert.AreEqual(0, set.Failures.Count(f => f.Code == TemplateFailureCode.XmlTooDeep));
        Assert.AreEqual(TemplateFailureCode.NotATemplate, FailureAssert.Single(set, TemplateFailureCode.NotATemplate).Code);
    }

    [TestMethod]
    public void DtdInAnyForm_IsRefused_AndNothingIsExpandedOrFetched()
    {
        using var temp = new TempTemplates();
        temp.Write("External.admx", $"""
            {TempTemplates.Header}
            <!DOCTYPE policyDefinitions SYSTEM "http://127.0.0.1:9/never-fetched.dtd">
            <policyDefinitions />
            """);
        temp.Write("Parameter.admx", $"""
            {TempTemplates.Header}
            <!DOCTYPE policyDefinitions [ <!ENTITY % p "x"> %p; ]>
            <policyDefinitions />
            """);
        var set = temp.Load();
        Assert.AreEqual(2, set.Failures.Count(f => f.Code == TemplateFailureCode.DtdProhibited));
        Assert.AreEqual(2, set.Failures.Count);
    }

    [TestMethod]
    public void DtdInAnAdml_IsRefusedWithTheAdmlPath()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("One", "T.One", GoodPolicy, """<string id="P">P</string>""");
        var adml = temp.Write(Path.Combine("en-US", "One.adml"), $"""
            {TempTemplates.Header}
            <!DOCTYPE policyDefinitionResources [ <!ENTITY e "x"> ]>
            <policyDefinitionResources />
            """);
        var set = temp.Load();
        Assert.AreEqual(adml, FailureAssert.Single(set, TemplateFailureCode.DtdProhibited).FilePath);
        Assert.AreEqual(0, set.Files.Count);
    }

    [TestMethod]
    public void JunctionAsSourceFolder_IsRefused()
    {
        using var temp = new TempTemplates();
        var real = Path.Combine(temp.Root, "real");
        var link = Path.Combine(temp.Root, "link");
        Directory.CreateDirectory(real);
        File.Copy(Path.Combine(Fixture.Root, "BwcOracleBase.admx"), Path.Combine(real, "BwcOracleBase.admx"));
        temp.MakeJunction(link, real);
        var set = TemplateLoader.Load([link]);
        Assert.AreEqual(link, FailureAssert.Single(set, TemplateFailureCode.ReparsePoint).FilePath);
        Assert.AreEqual(0, set.Files.Count);
        Assert.AreEqual(1, set.Failures.Count);
    }

    [TestMethod]
    public void JunctionAsLanguageFolder_IsReportedOnceAndTreatedAsMissing()
    {
        using var temp = new TempTemplates();
        var folder = Path.Combine(temp.Root, "src");
        var realLang = Path.Combine(temp.Root, "reallang");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(realLang);
        File.Copy(Path.Combine(Fixture.Root, "BwcOracleBase.admx"), Path.Combine(folder, "BwcOracleBase.admx"));
        File.Copy(Path.Combine(Fixture.Root, "BwcOracle.admx"), Path.Combine(folder, "BwcOracle.admx"));
        File.Copy(Path.Combine(Fixture.Root, "en-US", "BwcOracleBase.adml"), Path.Combine(realLang, "BwcOracleBase.adml"));
        File.Copy(Path.Combine(Fixture.Root, "en-US", "BwcOracle.adml"), Path.Combine(realLang, "BwcOracle.adml"));
        var link = Path.Combine(folder, "en-US");
        temp.MakeJunction(link, realLang);
        var set = TemplateLoader.Load([folder], "en-US");
        Assert.AreEqual(1, set.Failures.Count(f => f.Code == TemplateFailureCode.ReparsePoint));
        Assert.AreEqual(link, set.Failures.First(f => f.Code == TemplateFailureCode.ReparsePoint).FilePath);
        Assert.AreEqual(2, set.Failures.Count(f => f.Code == TemplateFailureCode.MissingAdml));
        Assert.AreEqual(0, set.Files.Count);
    }

    [TestMethod]
    public void SymbolicLinkAsTemplateFile_IsRefused()
    {
        using var temp = new TempTemplates();
        var target = temp.Write(Path.Combine("targets", "Real.admx"), $"{TempTemplates.Header}<policyDefinitions />");
        var link = Path.Combine(temp.Root, "Linked.admx");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("File symbolic links need a privilege this test must not use: " + ex.Message);
            return;
        }

        var set = temp.Load();
        Assert.AreEqual(link, FailureAssert.Single(set, TemplateFailureCode.ReparsePoint).FilePath);
    }

    [TestMethod]
    public void ReparseAttribute_IsRecognized()
    {
        Assert.IsTrue(SafeXml.IsReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Archive));
        Assert.IsFalse(SafeXml.IsReparsePoint(FileAttributes.Archive));
        Assert.IsFalse(SafeXml.IsReparsePointPath(Path.Combine(AppContext.BaseDirectory, "no-such-path-" + Guid.NewGuid().ToString("N"))));
    }

    [TestMethod]
    public void AttributesThatCannotBeRead_CountAsALink()
    {
        // A real unreadable path needs elevation, so the attribute read is a seam (failure mode 48).
        static FileAttributes Throw(Exception ex) => throw ex;
        Assert.IsTrue(SafeXml.IsReparsePointPath("x", _ => Throw(new UnauthorizedAccessException())));
        Assert.IsTrue(SafeXml.IsReparsePointPath("x", _ => Throw(new IOException("sharing violation"))));
        Assert.IsTrue(SafeXml.IsReparsePointPath("x", _ => Throw(new ArgumentException("bad path"))));
        Assert.IsTrue(SafeXml.IsReparsePointPath("x", _ => Throw(new NotSupportedException())));
        Assert.IsTrue(SafeXml.IsReparsePointPath("x", _ => Throw(new PathTooLongException())));
    }

    [TestMethod]
    public void AMissingPath_IsMissingNotALink()
    {
        static FileAttributes Throw(Exception ex) => throw ex;
        Assert.IsFalse(SafeXml.IsReparsePointPath("x", _ => Throw(new FileNotFoundException())));
        Assert.IsFalse(SafeXml.IsReparsePointPath("x", _ => Throw(new DirectoryNotFoundException())));
        Assert.IsTrue(SafeXml.IsReparsePointPath("x", _ => FileAttributes.ReparsePoint | FileAttributes.Directory));
        Assert.IsFalse(SafeXml.IsReparsePointPath("x", _ => FileAttributes.Directory));
    }

    [TestMethod]
    public void MissingLanguageFolderAndAdml_StillFallBackToEnUs_NotALinkFailure()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("One", "T.One", GoodPolicy, """<string id="P">P</string>""");

        var set = temp.Load("de-DE");

        FailureAssert.None(set);
        Assert.AreEqual("en-US", set.Files.Single().Culture);
    }

    [TestMethod]
    public void Reader_SettingsProhibitDtdAndCapTheDocument()
    {
        var settings = SafeXml.CreateSettings();
        Assert.AreEqual(System.Xml.DtdProcessing.Prohibit, settings.DtdProcessing);
        Assert.AreEqual(Value(AdmxLimits.MaxFileBytes), settings.MaxCharactersInDocument);
    }

    [TestMethod]
    public void LockedFile_IsAnUnreadableFailureNotAnException()
    {
        using var temp = new TempTemplates();
        var path = temp.Write("Locked.admx", $"{TempTemplates.Header}<policyDefinitions />");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var set = temp.Load();
        var failure = FailureAssert.Single(set, TemplateFailureCode.Unreadable);
        Assert.AreEqual(path, failure.FilePath);
        Assert.IsTrue(failure.Detail.StartsWith("0x", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EmptyFile_IsMalformedNotAnException()
    {
        using var temp = new TempTemplates();
        var path = temp.Write("Empty.admx", string.Empty);
        var set = temp.Load();
        Assert.AreEqual(path, FailureAssert.Single(set, TemplateFailureCode.MalformedXml).FilePath);
    }

    private static T Value<T>(T value) => value;
}
