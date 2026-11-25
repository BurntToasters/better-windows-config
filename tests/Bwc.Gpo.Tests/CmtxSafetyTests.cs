using System.Text;
using Bwc.Gpo.Comments;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>
/// AF6 comment.cmtx cases (docs/failure-modes/gpo-store.md, D5, D14, D15): a file nested far deeper than any real one is
/// refused before it can overflow the stack, a new comment never takes the string id of another policy, and a lone
/// surrogate is refused like any other character XML cannot carry. This file holds the cmtx namespace URL, so it holds no
/// write API (scripts/check-policy-writer.js).
/// </summary>
[TestClass]
public sealed class CmtxSafetyTests
{
    private const string Root =
        "<policyComments xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
        "revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"http://www.microsoft.com/GroupPolicy/CommentDefinitions\">";

    private const string Declaration = "<?xml version='1.0' encoding='utf-8'?>";
    private const string Uri = "BWC.Policies.Probe";
    private const string OtherUri = "BWC.Policies.Other";

    // The nesting cap the contract names: a real file is about 4 levels deep.
    private const int Cap = 64;

    private const string TwoNamespaces =
        "\r\n    <using prefix=\"ns0\" namespace=\"" + Uri + "\"></using>\r\n    <using prefix=\"ns1\" namespace=\"" + OtherUri + "\"></using>\r\n  ";

    private static string Doc(string namespaces, string admTemplate, string strings) =>
        Declaration + "\r\n" + Root + "\r\n" +
        "  <policyNamespaces>" + namespaces + "</policyNamespaces>\r\n" +
        "  <comments>\r\n    <admTemplate>" + admTemplate + "</admTemplate>\r\n  </comments>\r\n" +
        "  <resources minRequiredRevision=\"1.0\">\r\n    <stringTable>" + strings + "</stringTable>\r\n  </resources>\r\n" +
        "</policyComments>";

    private static string Entry(string prefix, string name, string id) =>
        "<comment policyRef=\"" + prefix + ":" + name + "\" commentText=\"$(resource." + id + ")\"></comment>";

    private static string Str(string id, string text) => "<string id=\"" + id + "\">" + text + "</string>";

    private static CmtxDocument Parse(string text) => CmtxDocument.Parse(Encoding.UTF8.GetBytes(text));

    private static string Nested(int levels)
    {
        var text = new StringBuilder(Declaration);
        text.Append(Root).Append("<policyNamespaces></policyNamespaces>");
        for (var i = 0; i < levels; i++)
        {
            text.Append("<a>");
        }

        for (var i = 0; i < levels; i++)
        {
            text.Append("</a>");
        }

        return text.Append("</policyComments>").ToString();
    }

    /// <summary>The bytes of a comment file nested <paramref name="levels"/> deep, for the store tests.</summary>
    internal static byte[] DeepFile(int levels) => Encoding.UTF8.GetBytes(Nested(levels));

    private static string Serialized(CmtxDocument document) => Encoding.UTF8.GetString(document.ToBytes());

    [TestMethod]
    public void Parse_NestedTenThousandLevels_IsRefusedAsMalformed_AndDoesNotCrash()
    {
        var text = Nested(10_000);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) < CmtxDocument.MaxBytes, "the case must fit inside the size cap");

        var failure = Assert.ThrowsExactly<CmtxFormatException>(() => Parse(text));

        StringAssert.Contains(failure.Message, "deep");
    }

    [TestMethod]
    public void Parse_NestingAtTheCap_ParsesAndKeepsTheForeignElements()
    {
        var document = Parse(Nested(Cap));

        var text = Serialized(document);
        Assert.AreEqual(Cap, text.Split("<a>").Length - 1);
        Assert.IsFalse(document.HasChanges);
    }

    [TestMethod]
    public void Parse_NestingOneLevelPastTheCap_IsRefused()
    {
        Assert.ThrowsExactly<CmtxFormatException>(() => Parse(Nested(Cap + 1)));
    }

    [TestMethod]
    public void Parse_ARealFile_IsFarUnderTheCap()
    {
        var document = Parse(Doc(TwoNamespaces, Entry("ns0", "A", "ns0_A"), Str("ns0_A", "x")));

        Assert.AreEqual("x", document.GetComment(Uri, "A"));
    }

    [TestMethod]
    public void SetComment_NewCommentNeverTakesAnotherPoliciesStringId()
    {
        var document = Parse(Doc(TwoNamespaces, Entry("ns1", "Other", "ns0_Mine"), Str("ns0_Mine", "other text")));

        document.SetComment(Uri, "Mine", "mine text");

        Assert.AreEqual("other text", document.GetComment(OtherUri, "Other"), "the other policy's text must not change");
        Assert.AreEqual("mine text", document.GetComment(Uri, "Mine"));
        var text = Serialized(document);
        StringAssert.Contains(text, Entry("ns0", "Mine", "ns0_Mine_2"));
        StringAssert.Contains(text, Str("ns0_Mine", "other text"));
        StringAssert.Contains(text, Str("ns0_Mine_2", "mine text"));
        Assert.AreEqual(2, document.Comments.Count);
    }

    [TestMethod]
    public void SetComment_SkipsEveryTakenSuffix()
    {
        var document = Parse(Doc(
            TwoNamespaces,
            Entry("ns1", "First", "ns0_Mine") + Entry("ns1", "Second", "ns0_Mine_2") + Entry("ns1", "Dangling", "ns0_Mine_3"),
            Str("ns0_Mine", "first") + Str("ns0_Mine_2", "second")));

        document.SetComment(Uri, "Mine", "mine text");

        Assert.AreEqual("first", document.GetComment(OtherUri, "First"));
        Assert.AreEqual("second", document.GetComment(OtherUri, "Second"));
        StringAssert.Contains(Serialized(document), Entry("ns0", "Mine", "ns0_Mine_4"), "an id that an entry still references is taken even when its string is missing");
        Assert.AreEqual("mine text", document.GetComment(Uri, "Mine"));
    }

    [TestMethod]
    public void SetComment_AnOrphanStringWithTheDefaultId_IsNotOverwritten()
    {
        var document = Parse(Doc(TwoNamespaces, string.Empty, Str("ns0_Mine", "left by another tool")));

        document.SetComment(Uri, "Mine", "mine text");

        var text = Serialized(document);
        StringAssert.Contains(text, Str("ns0_Mine", "left by another tool"));
        StringAssert.Contains(text, Str("ns0_Mine_2", "mine text"));
        Assert.AreEqual("mine text", document.GetComment(Uri, "Mine"));
    }

    [TestMethod]
    public void SetComment_EditingAnEntryThatKeepsItsOwnId_ChangesThatTextInPlace()
    {
        var document = Parse(Doc(TwoNamespaces, Entry("ns0", "Mine", "ns0_Mine"), Str("ns0_Mine", "old")));

        document.SetComment(Uri, "Mine", "new");

        var text = Serialized(document);
        StringAssert.Contains(text, Str("ns0_Mine", "new"));
        Assert.IsFalse(text.Contains("ns0_Mine_2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SetComment_EditingAnEntryThatSharesItsStringWithAnother_LeavesTheOtherAlone()
    {
        var document = Parse(Doc(
            TwoNamespaces,
            Entry("ns0", "A", "ns0_Shared") + Entry("ns1", "B", "ns0_Shared"),
            Str("ns0_Shared", "shared")));

        document.SetComment(Uri, "A", "changed");

        Assert.AreEqual("changed", document.GetComment(Uri, "A"));
        Assert.AreEqual("shared", document.GetComment(OtherUri, "B"), "the other entry keeps its text");
        StringAssert.Contains(Serialized(document), Str("ns0_Shared", "shared"));
    }

    [TestMethod]
    public void SetComment_ARemovedNeighbourFreesItsId()
    {
        var document = Parse(Doc(TwoNamespaces, string.Empty, string.Empty));
        document.SetComment(Uri, "Mine", "one");
        document.SetComment(Uri, "Mine", null);

        document.SetComment(Uri, "Mine", "two");

        var text = Serialized(document);
        StringAssert.Contains(text, Str("ns0_Mine", "two"));
        Assert.IsFalse(text.Contains("ns0_Mine_2", StringComparison.Ordinal));
    }

    // A plain loop, not DataRow: the test runner would replace a lone surrogate in a data row with U+FFFD.
    [TestMethod]
    public void SetComment_ALoneSurrogateInTheText_IsRefused_AndTheModelIsUntouched()
    {
        string[] cases =
        [
            "a\uD800b",
            "a\uDC00b",
            "\uD800",
            "ends with a high surrogate \uDBFF",
            "reversed pair \uDE00\uD83D",
        ];

        foreach (var text in cases)
        {
            var document = Parse(Doc(TwoNamespaces, string.Empty, string.Empty));

            Assert.ThrowsExactly<ArgumentException>(() => document.SetComment(Uri, "P", text));

            Assert.IsFalse(document.HasChanges);
            Assert.IsNull(document.GetComment(Uri, "P"));
        }
    }

    [TestMethod]
    public void SetComment_ALoneSurrogateInAPolicyNameOrNamespace_IsRefused()
    {
        var document = Parse(Doc(TwoNamespaces, string.Empty, string.Empty));

        Assert.ThrowsExactly<ArgumentException>(() => document.SetComment(Uri, "P\uD800", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => document.SetComment("ns\uDC00", "P", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => document.RestrictNamespacesTo("ns\uD800"));
        Assert.IsFalse(document.HasChanges);
    }

    [TestMethod]
    public void SetComment_AValidSurrogatePair_IsKept()
    {
        var document = Parse(Doc(TwoNamespaces, string.Empty, string.Empty));
        const string emoji = "note 😀 end";

        document.SetComment(Uri, "P", emoji);

        var reparsed = CmtxDocument.Parse(document.ToBytes());
        Assert.AreEqual(emoji, reparsed.GetComment(Uri, "P"));
    }
}
