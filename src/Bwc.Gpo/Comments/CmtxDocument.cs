using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Bwc.Gpo.Comments;

/// <summary>
/// The model of one comment.cmtx file: policy namespaces with prefixes, a comment per policy, and the string table the
/// comments point into. Anything the model does not understand stays in the tree and is written back in place, so a
/// comment edited here loses nothing another tool put there. Layout is normalized to the classic shape on write.
/// In memory only; the file is written by <c>GpoFileWriter</c>.
/// </summary>
public sealed class CmtxDocument
{
    /// <summary>The size cap; a larger file is refused.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The deepest element nesting accepted (the root is level 0). A real file is about 4 levels deep; a file nested far
    /// deeper fits in the size cap and would overflow the stack of the recursive serializer.
    /// </summary>
    public const int MaxDepth = 64;

    private static readonly Regex PrefixNumber = new(@"^ns(\d+)$", RegexOptions.CultureInvariant);

    private readonly XDocument _document;
    private readonly HashSet<string> _baselineNamespaces;
    private readonly byte[] _baseline;

    private CmtxDocument(XDocument document)
    {
        _document = document;
        _baselineNamespaces = new HashSet<string>(UsingElements().Select(u => (string?)u.Attribute("namespace") ?? string.Empty), StringComparer.Ordinal);
        _baseline = ToBytes();
    }

    /// <summary>The namespaces in file order.</summary>
    public IReadOnlyList<CmtxNamespace> Namespaces =>
        UsingElements()
            .Select(u => new CmtxNamespace((string?)u.Attribute("prefix") ?? string.Empty, (string?)u.Attribute("namespace") ?? string.Empty))
            .ToArray();

    /// <summary>The comments the model understands, in file order. Entries it cannot resolve stay in the file.</summary>
    public IReadOnlyList<CmtxComment> Comments
    {
        get
        {
            var list = new List<CmtxComment>();
            foreach (var entry in EntryElements())
            {
                var (ns, name) = Resolve(entry);
                var text = ns is null ? null : TextOf(entry);
                if (ns is not null && name is not null && text is not null && !list.Any(c => c.Namespace == ns && c.PolicyName == name))
                {
                    list.Add(new CmtxComment(ns, name, text));
                }
            }

            return list;
        }
    }

    /// <summary>True when the bytes differ from what was parsed.</summary>
    public bool HasChanges => !ToBytes().AsSpan().SequenceEqual(_baseline);

    /// <summary>A document with no comments and no namespaces, in the classic skeleton.</summary>
    /// <returns>A new model.</returns>
    public static CmtxDocument CreateEmpty() => Parse(Encoding.UTF8.GetBytes(CmtxFormat.EmptyDocument));

    /// <summary>Parses a comment.cmtx file. DTDs are refused and nothing is resolved.</summary>
    /// <param name="bytes">The complete file (UTF-8 with or without a byte order mark, or UTF-16).</param>
    /// <returns>The model.</returns>
    /// <exception cref="CmtxFormatException">Not well-formed XML, a DTD, a foreign root, or too large.</exception>
    public static CmtxDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            throw new CmtxFormatException($"comment.cmtx is larger than {MaxBytes} bytes.");
        }

        if (bytes.Length == 0)
        {
            throw new CmtxFormatException("comment.cmtx is empty.");
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                IgnoreWhitespace = false,
            };
            var buffer = bytes.ToArray();

            // A flat read first: no tree and no recursion exist until the nesting is known to be sane.
            using (var probeStream = new MemoryStream(buffer, false))
            using (var probe = XmlReader.Create(probeStream, settings))
            {
                while (probe.Read())
                {
                    if (probe.Depth > MaxDepth)
                    {
                        throw new CmtxFormatException($"comment.cmtx is nested deeper than {MaxDepth} levels.");
                    }
                }
            }

            using var stream = new MemoryStream(buffer, false);
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException)
        {
            throw new CmtxFormatException("comment.cmtx is not well-formed XML: " + e.Message, e);
        }

        if (document.Root is null || document.Root.Name != CmtxFormat.Root)
        {
            throw new CmtxFormatException("comment.cmtx does not have a policyComments root in the comment definitions namespace.");
        }

        StripLayout(document);
        return new CmtxDocument(document);
    }

    /// <summary>The comment on a policy.</summary>
    /// <param name="namespaceUri">The policy namespace.</param>
    /// <param name="policyName">The policy name.</param>
    /// <returns>The text, or null when there is none.</returns>
    public string? GetComment(string namespaceUri, string policyName)
    {
        var prefix = PrefixOf(namespaceUri);
        var entry = prefix is null ? null : FindEntry(prefix, policyName);
        return entry is null ? null : TextOf(entry);
    }

    /// <summary>Sets, edits or (with null or empty text) removes the comment on a policy.</summary>
    /// <param name="namespaceUri">The policy namespace.</param>
    /// <param name="policyName">The policy name.</param>
    /// <param name="text">The text; null or empty removes the comment.</param>
    /// <exception cref="ArgumentException">A name is empty or the text has a character XML 1.0 cannot carry.</exception>
    public void SetComment(string namespaceUri, string policyName, string? text)
    {
        ArgumentException.ThrowIfNullOrEmpty(namespaceUri);
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        if (!IsXmlText(namespaceUri) || !IsXmlText(policyName) || (text is not null && !IsXmlText(text)))
        {
            throw new ArgumentException("A name or comment holds a character that XML 1.0 cannot carry.", nameof(text));
        }

        if (string.IsNullOrEmpty(text))
        {
            Remove(namespaceUri, policyName);
            return;
        }

        EnsureSkeleton();
        var prefix = PrefixOf(namespaceUri) ?? AddNamespace(namespaceUri);
        var entry = FindEntry(prefix, policyName);
        var id = entry is null ? null : IdOf(entry);
        var table = StringTable()!;

        // An id that another entry shares, or a new id that a string or an entry already holds, is never reused: a comment
        // edit changes this policy's text only (CMTX-1).
        if (entry is null)
        {
            id = FreeId(table, prefix + "_" + policyName);
            AdmTemplate()!.Add(WriteComment(prefix, policyName, id));
        }
        else if (id is null || EntryElements().Any(e => !ReferenceEquals(e, entry) && IdOf(e) == id))
        {
            id = FreeId(table, prefix + "_" + policyName);
            entry.SetAttributeValue("commentText", "$(resource." + id + ")");
        }

        var existing = FindString(table, id!);
        if (existing is null)
        {
            existing = new XElement(CmtxFormat.String, new XAttribute("id", id!));
            table.Add(existing);
        }

        existing.RemoveNodes();
        existing.Add(new XText(text));
    }

    /// <summary>
    /// Keeps only the namespace of the edited policy and any namespace a comment still uses (adds it when absent). Verify
    /// against oracle (group C): classic rewrote the list this way on the first save of a session. The store does not
    /// call this; the policy layer does, inside its edit.
    /// </summary>
    /// <param name="namespaceUri">The namespace of the policy being edited.</param>
    public void RestrictNamespacesTo(string namespaceUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(namespaceUri);
        if (!IsXmlText(namespaceUri))
        {
            throw new ArgumentException("The namespace holds a character that XML 1.0 cannot carry.", nameof(namespaceUri));
        }

        EnsureSkeleton();
        var used = EntryElements().Select(e => PrefixOfRef(e)).Where(p => p is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var element in UsingElements().ToList())
        {
            var keep = (string?)element.Attribute("namespace") == namespaceUri || used.Contains((string?)element.Attribute("prefix"));
            if (!keep)
            {
                element.Remove();
            }
        }

        if (PrefixOf(namespaceUri) is null)
        {
            AddNamespace(namespaceUri);
        }
    }

    /// <summary>Serializes the file in the classic shape: UTF-8 with a byte order mark.</summary>
    /// <returns>A new array.</returns>
    public byte[] ToBytes() => CmtxFormat.Serialize(_document);

    /// <summary>True when every character is valid in XML 1.0. A surrogate counts only as half of a valid pair.</summary>
    private static bool IsXmlText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                {
                    return false;
                }

                i++;
            }
            else if (!XmlConvert.IsXmlChar(c))
            {
                // A low surrogate on its own is not an XML character.
                return false;
            }
        }

        return true;
    }

    /// <summary>The first of <c>id</c>, <c>id_2</c>, <c>id_3</c>... that no string or entry holds.</summary>
    private string FreeId(XElement table, string preferred)
    {
        var candidate = preferred;
        for (var n = 2; FindString(table, candidate) is not null || EntryElements().Any(e => IdOf(e) == candidate); n++)
        {
            candidate = preferred + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private static void StripLayout(XDocument document)
    {
        foreach (var element in document.Descendants().Prepend(document.Root!).ToList())
        {
            if (element.Elements().Any() || CmtxFormat.IsStructural(element.Name))
            {
                foreach (var text in element.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList())
                {
                    text.Remove();
                }
            }
        }

        foreach (var text in document.Nodes().OfType<XText>().ToList())
        {
            text.Remove();
        }
    }

    private IEnumerable<XElement> UsingElements() =>
        _document.Root!.Elements(CmtxFormat.PolicyNamespaces).Take(1).Elements(CmtxFormat.Using);

    private IEnumerable<XElement> EntryElements() =>
        _document.Root!.Elements(CmtxFormat.Comments).Elements(CmtxFormat.AdmTemplate).Elements(CmtxFormat.Comment);

    private XElement? AdmTemplate() => _document.Root!.Element(CmtxFormat.Comments)?.Element(CmtxFormat.AdmTemplate);

    private XElement? StringTable() => _document.Root!.Element(CmtxFormat.Resources)?.Element(CmtxFormat.StringTable);

    private string? PrefixOf(string namespaceUri) =>
        UsingElements().Where(u => (string?)u.Attribute("namespace") == namespaceUri).Select(u => (string?)u.Attribute("prefix")).FirstOrDefault();

    private static string? PrefixOfRef(XElement entry)
    {
        var reference = (string?)entry.Attribute("policyRef");
        var colon = reference?.IndexOf(':') ?? -1;
        return colon > 0 ? reference![..colon] : null;
    }

    private (string? Namespace, string? Name) Resolve(XElement entry)
    {
        var prefix = PrefixOfRef(entry);
        var reference = (string?)entry.Attribute("policyRef");
        if (prefix is null || reference is null)
        {
            return (null, null);
        }

        var ns = UsingElements().Where(u => (string?)u.Attribute("prefix") == prefix).Select(u => (string?)u.Attribute("namespace")).FirstOrDefault();
        return (ns, reference[(prefix.Length + 1)..]);
    }

    private XElement? FindEntry(string prefix, string policyName) =>
        EntryElements().FirstOrDefault(e => (string?)e.Attribute("policyRef") == prefix + ":" + policyName);

    private static string? IdOf(XElement entry)
    {
        var value = (string?)entry.Attribute("commentText");
        const string start = "$(resource.";
        return value is not null && value.StartsWith(start, StringComparison.Ordinal) && value.EndsWith(')') ? value[start.Length..^1] : null;
    }

    private static XElement? FindString(XElement table, string id) =>
        table.Elements(CmtxFormat.String).FirstOrDefault(s => (string?)s.Attribute("id") == id);

    private string? TextOf(XElement entry)
    {
        var id = IdOf(entry);
        var table = StringTable();
        if (id is null || table is null)
        {
            return null;
        }

        return FindString(table, id)?.Value;
    }

    /// <summary>The shape of one comment entry (C/Comment_Enabled_AddEditRemove, C/Comment_TwoPolicies).</summary>
    private static XElement WriteComment(string prefix, string policyName, string id) =>
        new(
            CmtxFormat.Comment,
            new XAttribute("policyRef", prefix + ":" + policyName),
            new XAttribute("commentText", "$(resource." + id + ")"));

    private string AddNamespace(string namespaceUri)
    {
        EnsureSkeleton();
        var next = UsingElements()
            .Select(u => PrefixNumber.Match((string?)u.Attribute("prefix") ?? string.Empty))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var prefix = "ns" + next.ToString(CultureInfo.InvariantCulture);
        _document.Root!.Element(CmtxFormat.PolicyNamespaces)!.Add(
            new XElement(CmtxFormat.Using, new XAttribute("prefix", prefix), new XAttribute("namespace", namespaceUri)));
        return prefix;
    }

    private void Remove(string namespaceUri, string policyName)
    {
        var prefix = PrefixOf(namespaceUri);
        var entry = prefix is null ? null : FindEntry(prefix, policyName);
        if (prefix is null || entry is null)
        {
            return;
        }

        var id = IdOf(entry);
        entry.Remove();
        var table = StringTable();
        if (id is not null && table is not null && !EntryElements().Any(e => IdOf(e) == id))
        {
            FindString(table, id)?.Remove();
        }

        var stillUsed = EntryElements().Any(e => PrefixOfRef(e) == prefix);
        if (!stillUsed && !_baselineNamespaces.Contains(namespaceUri))
        {
            UsingElements().FirstOrDefault(u => (string?)u.Attribute("namespace") == namespaceUri)?.Remove();
        }
    }

    private void EnsureSkeleton()
    {
        var root = _document.Root!;
        var namespaces = root.Element(CmtxFormat.PolicyNamespaces);
        if (namespaces is null)
        {
            namespaces = new XElement(CmtxFormat.PolicyNamespaces);
            root.AddFirst(namespaces);
        }

        var comments = root.Element(CmtxFormat.Comments);
        if (comments is null)
        {
            comments = new XElement(CmtxFormat.Comments);
            namespaces.AddAfterSelf(comments);
        }

        if (comments.Element(CmtxFormat.AdmTemplate) is null)
        {
            comments.AddFirst(new XElement(CmtxFormat.AdmTemplate));
        }

        var resources = root.Element(CmtxFormat.Resources);
        if (resources is null)
        {
            resources = new XElement(CmtxFormat.Resources, new XAttribute("minRequiredRevision", "1.0"));
            comments.AddAfterSelf(resources);
        }

        // Classic adds the minimum revision when it saves a file whose resources element lacks it (D6): captures
        // C/Cmtx_FirstSave_ThenUser and C/Cmtx_FirstSave_ForeignNamespaces.
        if (resources.Attribute("minRequiredRevision") is null)
        {
            resources.SetAttributeValue("minRequiredRevision", "1.0");
        }

        if (resources.Element(CmtxFormat.StringTable) is null)
        {
            resources.AddFirst(new XElement(CmtxFormat.StringTable));
        }
    }
}
