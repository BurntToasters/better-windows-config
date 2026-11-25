using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

/// <summary>The ADML language chain (docs/failure-modes/policy-admx.md, section 52, L1 to L5).</summary>
[TestClass]
public sealed class AdmlLanguageChainTests
{
    private const string Ns = "T.Chain";

    [TestMethod]
    public void DeAt_WithOnlyADeFolder_LoadsDe()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "de", "de");
        AssertLoaded(temp.Load("de-AT"), "de", "de");
    }

    [TestMethod]
    public void DeAt_WithDeAtAndDe_LoadsDeAt()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "de-AT", "de-AT");
        WriteAdml(temp, "de", "de");
        AssertLoaded(temp.Load("de-AT"), "de-AT", "de-AT");
    }

    [TestMethod]
    public void DeAt_WithOnlyEnUs_LoadsEnUs()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "en-US", "en-US");
        AssertLoaded(temp.Load("de-AT"), "en-US", "en-US");
    }

    [TestMethod]
    public void ZhHantTw_WithZhHantAndZh_LoadsTheNearerParentZhHant()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "zh", "zh");
        WriteAdml(temp, "zh-Hant", "zh-Hant");
        WriteAdml(temp, "en-US", "en-US");
        AssertLoaded(temp.Load("zh-Hant-TW"), "zh-Hant", "zh-Hant");
    }

    [TestMethod]
    public void ZhHantTw_WithOnlyZh_LoadsZh()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "zh", "zh");
        WriteAdml(temp, "en-US", "en-US");
        AssertLoaded(temp.Load("zh-Hant-TW"), "zh", "zh");
    }

    [TestMethod]
    public void DeAt_WithAFolderInAnotherCase_LoadsIt()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "DE", "DE");
        WriteAdml(temp, "en-US", "en-US");
        AssertLoaded(temp.Load("de-at"), "de", "DE");
    }

    [TestMethod]
    public void InvalidCultureName_FallsStraightToEnUs()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "not-a-culture", "not-a-culture");
        WriteAdml(temp, "en-US", "en-US");
        AssertLoaded(temp.Load("not-a-culture"), "en-US", "en-US");
    }

    [TestMethod]
    public void TemplateWithNoAdmlInAnyFolderOfTheChain_IsSkippedAndReported()
    {
        using var temp = new TempTemplates();
        WriteAdmx(temp);
        WriteAdml(temp, "fr", "fr");
        var set = temp.Load("de-AT");
        var failure = FailureAssert.Single(set, TemplateFailureCode.MissingAdml);
        Assert.AreEqual(Path.Combine(temp.Root, "en-US", "Chain.adml"), failure.FilePath);
        Assert.AreEqual(Path.Combine(temp.Root, "Chain.admx"), failure.Detail);
        Assert.AreEqual(0, set.Files.Count);
    }

    private static void WriteAdmx(TempTemplates temp) => temp.Write("Chain.admx", $"""
        {TempTemplates.Header}
        <policyDefinitions revision="1.0" schemaVersion="1.0">
          <policyNamespaces><target prefix="c" namespace="{Ns}" /></policyNamespaces>
          <resources minRequiredRevision="1.0" />
        </policyDefinitions>
        """);

    private static void WriteAdml(TempTemplates temp, string folder, string displayName) =>
        temp.Write(Path.Combine(folder, "Chain.adml"), $"""
            {TempTemplates.Header}
            <policyDefinitionResources revision="1.0" schemaVersion="1.0">
              <displayName>{displayName}</displayName>
              <description>Test file</description>
              <resources>
                <stringTable />
                <presentationTable />
              </resources>
            </policyDefinitionResources>
            """);

    /// <summary>Loads with no failure, from the expected folder. The culture is compared without case.</summary>
    private static void AssertLoaded(TemplateSet set, string culture, string displayName)
    {
        FailureAssert.None(set);
        var file = set.Files.Single();
        Assert.IsTrue(string.Equals(culture, file.Culture, StringComparison.OrdinalIgnoreCase), $"culture {file.Culture}");
        Assert.AreEqual(displayName, file.DisplayName);
        Assert.AreEqual(1, set.Files.Count);
    }
}
