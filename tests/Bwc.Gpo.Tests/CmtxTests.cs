using System.Text;
using Bwc.Gpo.Comments;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>
/// comment.cmtx model tests. This file holds the cmtx namespace URL, so it holds no write API
/// (scripts/check-policy-writer.js).
/// </summary>
[TestClass]
public sealed class CmtxTests
{
    private const string Root =
        "<policyComments xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
        "revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"http://www.microsoft.com/GroupPolicy/CommentDefinitions\">";

    private const string Declaration = "<?xml version='1.0' encoding='utf-8'?>";
    private const string Uri = "BWC.Policies.Probe";

    private static string Doc(string namespaces, string admTemplate, string strings) =>
        Declaration + "\r\n" + Root + "\r\n" +
        "  <policyNamespaces>" + namespaces + "</policyNamespaces>\r\n" +
        "  <comments>\r\n    <admTemplate>" + admTemplate + "</admTemplate>\r\n  </comments>\r\n" +
        "  <resources minRequiredRevision=\"1.0\">\r\n    <stringTable>" + strings + "</stringTable>\r\n  </resources>\r\n" +
        "</policyComments>";

    private const string UsingProbe = "\r\n    <using prefix=\"ns0\" namespace=\"BWC.Policies.Probe\"></using>\r\n  ";

    private static byte[] WithBom(string text) => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    private static string ProbeText => Doc(UsingProbe, "", "");

    private static string Text(byte[] bytes)
    {
        Assert.AreEqual(0xEF, bytes[0]);
        Assert.AreEqual(0xBB, bytes[1]);
        Assert.AreEqual(0xBF, bytes[2]);
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    [TestMethod]
    public void Probe_RoundTripsByteForByte()
    {
        var bytes = WithBom(ProbeText);
        var doc = CmtxDocument.Parse(bytes);
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
        Assert.IsFalse(doc.HasChanges);
        Assert.AreEqual(1, doc.Namespaces.Count);
        Assert.AreEqual("ns0", doc.Namespaces[0].Prefix);
        Assert.AreEqual(Uri, doc.Namespaces[0].Namespace);
        Assert.AreEqual(0, doc.Comments.Count);
    }

    [TestMethod]
    public void ProbeShape_HasBomCrlfSingleQuotedDeclarationAndNoFinalNewline()
    {
        var bytes = CmtxDocument.Parse(WithBom(ProbeText)).ToBytes();
        Assert.AreEqual(0xEF, bytes[0]);
        var text = Text(bytes);
        StringAssert.StartsWith(text, "<?xml version='1.0' encoding='utf-8'?>\r\n<policyComments ");
        Assert.IsTrue(text.EndsWith("</policyComments>", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("\n") && text.Replace("\r\n", "").Contains('\n'), "every line break is CRLF");
        StringAssert.Contains(text, "<using prefix=\"ns0\" namespace=\"BWC.Policies.Probe\"></using>");
        StringAssert.Contains(text, "<admTemplate></admTemplate>");
    }

    [TestMethod]
    public void CreateEmpty_WritesTheClassicSkeleton()
    {
        var text = Text(CmtxDocument.CreateEmpty().ToBytes());
        Assert.AreEqual(Doc("", "", ""), text);
    }

    [TestMethod]
    public void SetComment_AddsEntryAndString_InClassicShape()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment(Uri, "Probe_Alpha", "hello");
        var expected = Doc(
            UsingProbe,
            "\r\n      <comment policyRef=\"ns0:Probe_Alpha\" commentText=\"$(resource.ns0_Probe_Alpha)\"></comment>\r\n    ",
            "\r\n      <string id=\"ns0_Probe_Alpha\">hello</string>\r\n    ");
        Assert.AreEqual(expected, Text(doc.ToBytes()));
        Assert.IsTrue(doc.HasChanges);
        Assert.AreEqual("hello", doc.GetComment(Uri, "Probe_Alpha"));
        Assert.IsNull(doc.GetComment(Uri, "Probe_Beta"));
    }

    [TestMethod]
    public void SetComment_RoundTripsThroughParse()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment(Uri, "Probe_Alpha", "hello");
        doc.SetComment(Uri, "Probe_Beta", "second");
        var again = CmtxDocument.Parse(doc.ToBytes());
        Assert.AreEqual("hello", again.GetComment(Uri, "Probe_Alpha"));
        Assert.AreEqual("second", again.GetComment(Uri, "Probe_Beta"));
        CollectionAssert.AreEqual(doc.ToBytes(), again.ToBytes());
        Assert.AreEqual(2, again.Comments.Count);
    }

    [TestMethod]
    public void SetComment_Edit_ChangesOnlyTheString()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment(Uri, "Probe_Alpha", "hello");
        doc.SetComment(Uri, "Probe_Alpha", "changed");
        StringAssert.Contains(Text(doc.ToBytes()), "<string id=\"ns0_Probe_Alpha\">changed</string>");
        Assert.AreEqual(1, doc.Comments.Count);
    }

    [TestMethod]
    public void SetComment_SameText_IsNoChange()
    {
        var withComment = CmtxDocument.Parse(WithBom(ProbeText));
        withComment.SetComment(Uri, "Probe_Alpha", "hello");
        var doc = CmtxDocument.Parse(withComment.ToBytes());
        doc.SetComment(Uri, "Probe_Alpha", "hello");
        Assert.IsFalse(doc.HasChanges);
    }

    [TestMethod]
    public void RemovingTheComment_ReturnsToTheProbeBytes_AndHasNoChanges()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment(Uri, "Probe_Alpha", "hello");
        doc.SetComment(Uri, "Probe_Alpha", null);
        CollectionAssert.AreEqual(WithBom(ProbeText), doc.ToBytes());
        Assert.IsFalse(doc.HasChanges);

        doc.SetComment(Uri, "Probe_Alpha", "x");
        doc.SetComment(Uri, "Probe_Alpha", "");
        CollectionAssert.AreEqual(WithBom(ProbeText), doc.ToBytes(), "empty text removes the entry");
    }

    [TestMethod]
    public void NewNamespace_GetsNextPrefix_AndIsDroppedAgainWhenItsLastCommentGoes()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment("Other.Ns", "P", "x");
        Assert.AreEqual(2, doc.Namespaces.Count);
        Assert.AreEqual("ns1", doc.Namespaces[1].Prefix);
        var text = Text(doc.ToBytes());
        StringAssert.Contains(text, "<using prefix=\"ns1\" namespace=\"Other.Ns\"></using>");
        StringAssert.Contains(text, "policyRef=\"ns1:P\" commentText=\"$(resource.ns1_P)\"");
        StringAssert.Contains(text, "<string id=\"ns1_P\">x</string>");

        doc.SetComment("Other.Ns", "P", null);
        CollectionAssert.AreEqual(WithBom(ProbeText), doc.ToBytes());
    }

    [TestMethod]
    public void NamespaceThatWasInTheFile_IsKept_WhenItsLastCommentGoes()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment(Uri, "A", "x");
        doc.SetComment(Uri, "A", null);
        Assert.AreEqual(1, doc.Namespaces.Count);
    }

    [TestMethod]
    public void PrefixNumbering_NeverReusesAHigherNumber()
    {
        const string two = "\r\n    <using prefix=\"ns0\" namespace=\"A\"></using>\r\n    <using prefix=\"ns5\" namespace=\"B\"></using>\r\n  ";
        var doc = CmtxDocument.Parse(WithBom(Doc(two, "", "")));
        doc.SetComment("C", "P", "x");
        Assert.AreEqual("ns6", doc.Namespaces[2].Prefix);
    }

    [TestMethod]
    public void ForeignContent_IsKeptInPlace()
    {
        var text = Doc(
            UsingProbe,
            "\r\n      <!-- by another tool -->\r\n      <foreign a=\"1\">kept</foreign>\r\n    ",
            "\r\n      <string id=\"other\">stays</string>\r\n    ");
        var doc = CmtxDocument.Parse(WithBom(text));
        CollectionAssert.AreEqual(WithBom(text), doc.ToBytes());

        doc.SetComment(Uri, "Probe_Alpha", "hi");
        var after = Text(doc.ToBytes());
        StringAssert.Contains(after, "<!-- by another tool -->");
        StringAssert.Contains(after, "<foreign a=\"1\">kept</foreign>");
        StringAssert.Contains(after, "<string id=\"other\">stays</string>");
        StringAssert.Contains(after, "<string id=\"ns0_Probe_Alpha\">hi</string>");
    }

    [TestMethod]
    public void IndentationOfAForeignFile_IsNormalizedToTheClassicShape()
    {
        var odd = Declaration + "\n" + Root + "<policyNamespaces><using prefix=\"ns0\" namespace=\"BWC.Policies.Probe\"/></policyNamespaces>" +
                  "<comments><admTemplate/></comments><resources minRequiredRevision=\"1.0\"><stringTable/></resources></policyComments>";
        var doc = CmtxDocument.Parse(Encoding.UTF8.GetBytes(odd));
        CollectionAssert.AreEqual(WithBom(ProbeText), doc.ToBytes(), "no BOM in, BOM out; layout normalized");
    }

    [TestMethod]
    public void TextWithMarkupAndNewlines_IsEscapedAndSurvivesAParse()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        const string value = "a < b & \"c\" > d\r\nline2\tend";
        doc.SetComment(Uri, "P", value);
        var text = Text(doc.ToBytes());
        StringAssert.Contains(text, "a &lt; b &amp; \"c\" &gt; d&#xD;\nline2");
        Assert.AreEqual(value, CmtxDocument.Parse(doc.ToBytes()).GetComment(Uri, "P"));
    }

    [TestMethod]
    public void PolicyNameWithMarkupCharacters_IsEscapedInAttributes()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.SetComment(Uri, "A&B\"C", "x");
        Assert.AreEqual("x", CmtxDocument.Parse(doc.ToBytes()).GetComment(Uri, "A&B\"C"));
    }

    [TestMethod]
    public void InvalidXmlCharacter_IsRefused_AndTheModelIsUnchanged()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetComment(Uri, "P", "bad\u0001text"));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetComment(Uri, "", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetComment("", "P", "x"));
        Assert.IsFalse(doc.HasChanges);
    }

    [TestMethod]
    public void EntryWithoutAString_ReadsAsNoComment()
    {
        var text = Doc(
            UsingProbe,
            "\r\n      <comment policyRef=\"ns0:P\" commentText=\"$(resource.ns0_P)\"></comment>\r\n    ",
            "");
        var doc = CmtxDocument.Parse(WithBom(text));
        Assert.IsNull(doc.GetComment(Uri, "P"));
        CollectionAssert.AreEqual(WithBom(text), doc.ToBytes());
    }

    [TestMethod]
    public void TwoEntriesForOnePolicy_FirstWins()
    {
        var text = Doc(
            UsingProbe,
            "\r\n      <comment policyRef=\"ns0:P\" commentText=\"$(resource.ns0_P)\"></comment>\r\n      <comment policyRef=\"ns0:P\" commentText=\"$(resource.ns0_P2)\"></comment>\r\n    ",
            "\r\n      <string id=\"ns0_P\">one</string>\r\n      <string id=\"ns0_P2\">two</string>\r\n    ");
        var doc = CmtxDocument.Parse(WithBom(text));
        Assert.AreEqual("one", doc.GetComment(Uri, "P"));
        doc.SetComment(Uri, "P", "edited");
        var after = Text(doc.ToBytes());
        StringAssert.Contains(after, "<string id=\"ns0_P\">edited</string>");
        StringAssert.Contains(after, "<string id=\"ns0_P2\">two</string>");
    }

    [TestMethod]
    public void EntryWithAnUndeclaredPrefix_IsForeign_AndNeverMatched()
    {
        var text = Doc(
            UsingProbe,
            "\r\n      <comment policyRef=\"zz:P\" commentText=\"$(resource.zz_P)\"></comment>\r\n    ",
            "\r\n      <string id=\"zz_P\">orphan</string>\r\n    ");
        var doc = CmtxDocument.Parse(WithBom(text));
        Assert.AreEqual(0, doc.Comments.Count);
        Assert.IsNull(doc.GetComment(Uri, "P"));
        CollectionAssert.AreEqual(WithBom(text), doc.ToBytes());
    }

    [TestMethod]
    public void RestrictNamespacesTo_KeepsTheEditedNamespaceAndAnyInUse()
    {
        const string three =
            "\r\n    <using prefix=\"ns0\" namespace=\"A\"></using>\r\n    <using prefix=\"ns1\" namespace=\"B\"></using>\r\n    <using prefix=\"ns2\" namespace=\"C\"></using>\r\n  ";
        var doc = CmtxDocument.Parse(WithBom(Doc(three, "", "")));
        doc.SetComment("B", "P", "x");
        doc.RestrictNamespacesTo("C");
        CollectionAssert.AreEqual(new[] { "B", "C" }, doc.Namespaces.Select(n => n.Namespace).ToArray());

        doc.RestrictNamespacesTo("D");
        CollectionAssert.AreEqual(new[] { "B", "D" }, doc.Namespaces.Select(n => n.Namespace).ToArray());
        Assert.AreEqual("x", doc.GetComment("B", "P"));
    }

    [TestMethod]
    public void RestrictNamespacesTo_WhenAlreadyRestricted_IsNoChange()
    {
        var doc = CmtxDocument.Parse(WithBom(ProbeText));
        doc.RestrictNamespacesTo(Uri);
        Assert.IsFalse(doc.HasChanges);
    }

    [TestMethod]
    public void HostileInput_IsRefusedWithATypedFailure()
    {
        var dtd = "<?xml version='1.0' encoding='utf-8'?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>" + Root.Replace("\">", "\"><a>&e;</a>") + "</policyComments>";
        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["bom only"] = [0xEF, 0xBB, 0xBF],
            ["not xml"] = Encoding.UTF8.GetBytes("hello"),
            ["truncated"] = WithBom(ProbeText[..60]),
            ["dtd"] = WithBom(dtd),
            ["wrong root"] = Encoding.UTF8.GetBytes("<other/>"),
            ["wrong namespace"] = Encoding.UTF8.GetBytes("<policyComments xmlns=\"urn:x\"></policyComments>"),
            ["oversize"] = new byte[CmtxDocument.MaxBytes + 1],
        };

        foreach (var (name, bytes) in cases)
        {
            Assert.ThrowsExactly<CmtxFormatException>(() => CmtxDocument.Parse(bytes), name);
        }
    }

    [TestMethod]
    public void Utf16File_IsReadAndWrittenBackAsUtf8WithBom()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(ProbeText.Replace("'utf-8'", "'utf-16'"))).ToArray();
        var doc = CmtxDocument.Parse(bytes);
        Assert.AreEqual(1, doc.Namespaces.Count);
        Assert.AreEqual(0xEF, doc.ToBytes()[0]);
    }

    [TestMethod]
    public void MissingSections_AreAddedWhenAnEditNeedsThem()
    {
        var minimal = Declaration + "\r\n" + Root + "\r\n</policyComments>";
        var doc = CmtxDocument.Parse(Encoding.UTF8.GetBytes(minimal));
        doc.SetComment(Uri, "P", "x");
        var again = CmtxDocument.Parse(doc.ToBytes());
        Assert.AreEqual("x", again.GetComment(Uri, "P"));
    }
}
