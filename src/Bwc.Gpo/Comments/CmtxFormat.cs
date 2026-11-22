using System.Text;
using System.Xml.Linq;

namespace Bwc.Gpo.Comments;

/// <summary>The comment.cmtx content cannot be read.</summary>
public sealed class CmtxFormatException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="inner">The XML failure, if any.</param>
    public CmtxFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A prefix and the policy namespace it stands for.</summary>
/// <param name="Prefix">The prefix, for example <c>ns0</c>.</param>
/// <param name="Namespace">The policy namespace, for example <c>Microsoft.Policies.WindowsUpdate</c>.</param>
public sealed record CmtxNamespace(string Prefix, string Namespace);

/// <summary>A comment on one policy.</summary>
/// <param name="Namespace">The policy namespace.</param>
/// <param name="PolicyName">The policy name inside the namespace.</param>
/// <param name="Text">The comment text.</param>
public sealed record CmtxComment(string Namespace, string PolicyName, string Text);

/// <summary>
/// Names, namespaces and layout of comment.cmtx. The classic layout comes from the 2026-11-18 probe: UTF-8 with a
/// byte order mark, CRLF, two-space indent, a single-quoted declaration, empty elements as open and close tags, no
/// final line ending. The shape of a comment entry is settled by group C (C/Comment_*) and lives in
/// <see cref="CmtxDocument"/> next to the names below.
/// </summary>
internal static class CmtxFormat
{
    internal static readonly XNamespace Ns = "http://www.microsoft.com/GroupPolicy/CommentDefinitions";

    internal static readonly XName Root = Ns + "policyComments";
    internal static readonly XName PolicyNamespaces = Ns + "policyNamespaces";
    internal static readonly XName Using = Ns + "using";
    internal static readonly XName Comments = Ns + "comments";
    internal static readonly XName AdmTemplate = Ns + "admTemplate";
    internal static readonly XName Comment = Ns + "comment";
    internal static readonly XName Resources = Ns + "resources";
    internal static readonly XName StringTable = Ns + "stringTable";
    internal static readonly XName String = Ns + "string";

    internal const string Declaration = "<?xml version='1.0' encoding='utf-8'?>";
    internal const string NewLine = "\r\n";
    internal const string Indent = "  ";

    /// <summary>The classic skeleton of a file with no comments and no namespaces (C/Cmtx_NoStartFile).</summary>
    internal static readonly string EmptyDocument =
        Declaration + NewLine +
        "<policyComments xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
        "revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"http://www.microsoft.com/GroupPolicy/CommentDefinitions\">" + NewLine +
        "  <policyNamespaces></policyNamespaces>" + NewLine +
        "  <comments>" + NewLine +
        "    <admTemplate></admTemplate>" + NewLine +
        "  </comments>" + NewLine +
        "  <resources minRequiredRevision=\"1.0\">" + NewLine +
        "    <stringTable></stringTable>" + NewLine +
        "  </resources>" + NewLine +
        "</policyComments>";

    /// <summary>Elements whose whitespace-only text is layout, never content.</summary>
    internal static bool IsStructural(XName name) =>
        name == Root || name == PolicyNamespaces || name == Comments || name == AdmTemplate || name == Resources || name == StringTable;

    /// <summary>The bytes of a document: BOM, declaration, then the root in the classic layout.</summary>
    internal static byte[] Serialize(XDocument document)
    {
        var text = new StringBuilder(Declaration);
        foreach (var node in document.Nodes())
        {
            text.Append(NewLine);
            WriteNode(text, node, 0);
        }

        var body = Encoding.UTF8.GetBytes(text.ToString());
        var bytes = new byte[3 + body.Length];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);
        return bytes;
    }

    private static void WriteNode(StringBuilder text, XNode node, int depth)
    {
        switch (node)
        {
            case XElement element:
                WriteElement(text, element, depth);
                break;
            case XComment comment:
                text.Append("<!--").Append(comment.Value).Append("-->");
                break;
            case XProcessingInstruction instruction:
                text.Append("<?").Append(instruction.Target).Append(' ').Append(instruction.Data).Append("?>");
                break;
            case XText content:
                text.Append(EscapeText(content.Value));
                break;
        }
    }

    private static void WriteElement(StringBuilder text, XElement element, int depth)
    {
        var name = QualifiedName(element, element.Name);
        text.Append('<').Append(name);
        foreach (var attribute in element.Attributes())
        {
            string attributeName;
            if (attribute.IsNamespaceDeclaration)
            {
                attributeName = attribute.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + attribute.Name.LocalName;
            }
            else
            {
                attributeName = QualifiedName(element, attribute.Name);
            }

            text.Append(' ').Append(attributeName).Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
        }

        text.Append('>');
        var children = element.Nodes().ToList();
        if (children.Count > 0)
        {
            var textOnly = children.All(c => c is XText);
            if (textOnly)
            {
                text.Append(EscapeText(string.Concat(children.Cast<XText>().Select(t => t.Value))));
            }
            else
            {
                var padding = string.Concat(Enumerable.Repeat(Indent, depth + 1));
                foreach (var child in children)
                {
                    text.Append(NewLine).Append(padding);
                    WriteNode(text, child, depth + 1);
                }

                text.Append(NewLine).Append(string.Concat(Enumerable.Repeat(Indent, depth)));
            }
        }

        text.Append("</").Append(name).Append('>');
    }

    private static string QualifiedName(XElement scope, XName name)
    {
        if (name.Namespace == XNamespace.None)
        {
            return name.LocalName;
        }

        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
    }

    // Classic writes a carriage return in a comment as it is: capture C/Comment_MultiLine holds the raw CR LF pair inside the
    // string element and no character reference.
    private static string EscapeText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("\r", "&#xD;", StringComparison.Ordinal)
            .Replace("\n", "&#xA;", StringComparison.Ordinal)
            .Replace("\t", "&#x9;", StringComparison.Ordinal);
}
