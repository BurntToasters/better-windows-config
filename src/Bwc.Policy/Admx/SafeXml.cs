using System.Xml;
using System.Xml.Linq;

namespace Bwc.Policy.Admx;

/// <summary>
/// Collects load failures. One sink belongs to one thread of work. A sink made with a cap keeps that many failures, then
/// one <see cref="TemplateFailureCode.TooManyFailures"/> entry, and drops the rest.
/// </summary>
internal sealed class FailureSink(int cap = int.MaxValue)
{
    private bool capped;

    internal List<TemplateFailure> Items { get; } = [];

    /// <summary>True once the cap is reached: the next failure ends the list and nothing after it is kept.</summary>
    internal bool IsFull => capped || Items.Count >= cap;

    internal void Add(TemplateFailureCode code, string path, string detail = "", int line = 0, int position = 0)
    {
        if (capped)
        {
            return;
        }

        if (Items.Count >= cap)
        {
            capped = true;
            Items.Add(new TemplateFailure(
                TemplateFailureCode.TooManyFailures, path, 0, 0, cap.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        Items.Add(new TemplateFailure(code, path, line, position, detail));
    }
}

/// <summary>The source position of an element, kept as an annotation on the element.</summary>
internal sealed record SourcePosition(int Line, int Position);

/// <summary>
/// The only way a template file is read: size cap, reparse points refused, DTD prohibited, no resolver, depth cap.
/// The file becomes a namespace-free element tree (shipped files differ in whether they declare the namespace) with
/// the source position on each element.
/// </summary>
internal static class SafeXml
{
    internal static bool IsReparsePoint(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    internal static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = AdmxLimits.MaxFileBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    /// <summary>
    /// True when the path is a reparse point. A path that does not exist (file or directory not found) is not one, so it
    /// stays missing. Attributes that exist but cannot be read count as a link, as in the other path checks of the
    /// solution: a path that cannot be inspected cannot be shown not to be a link.
    /// </summary>
    internal static bool IsReparsePointPath(string path) => IsReparsePointPath(path, File.GetAttributes);

    /// <summary>The same check with the attribute read as a seam, for tests that cannot make a path unreadable.</summary>
    internal static bool IsReparsePointPath(string path, Func<string, FileAttributes> readAttributes)
    {
        try
        {
            return IsReparsePoint(readAttributes(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads a template file as text, for the one place that needs the text itself (the position of a start tag). It
    /// applies the same checks as <see cref="Load"/> before it opens the file: name length, reparse point and size cap.
    /// Returns null when the file is refused or cannot be read; the failure for that is already recorded by
    /// <see cref="Load"/>.
    /// </summary>
    internal static string? ReadText(string path) => ReadText(path, File.GetAttributes);

    /// <summary>The same read with the attribute read as a seam.</summary>
    internal static string? ReadText(string path, Func<string, FileAttributes> readAttributes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Name.Length > AdmxLimits.MaxFileNameLength
                || IsReparsePointPath(path, readAttributes)
                || (readAttributes(path) & FileAttributes.Directory) != 0
                || info.Length > AdmxLimits.MaxFileBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            // A file that grows after the size check is cut off at the same cap as the XML reader's.
            var text = new System.Text.StringBuilder();
            var buffer = new char[64 * 1024];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                text.Append(buffer, 0, count);
                if (text.Length > AdmxLimits.MaxFileBytes)
                {
                    return null;
                }
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads one template file. Returns null and records a failure when the file cannot be used.</summary>
    internal static XElement? Load(string path, FailureSink sink)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Name.Length > AdmxLimits.MaxFileNameLength)
            {
                sink.Add(TemplateFailureCode.FileNameTooLong, path);
                return null;
            }

            if (IsReparsePoint(File.GetAttributes(path)))
            {
                sink.Add(TemplateFailureCode.ReparsePoint, path);
                return null;
            }

            if (info.Length > AdmxLimits.MaxFileBytes)
            {
                sink.Add(TemplateFailureCode.FileTooLarge, path);
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using var reader = XmlReader.Create(stream, CreateSettings());
                return Build(reader);
            }
            catch (XmlException ex)
            {
                if (ex.Message.StartsWith(DtdRefusedMessage, StringComparison.Ordinal))
                {
                    FindDocumentType(path, out var line, out var position);
                    sink.Add(TemplateFailureCode.DtdProhibited, path, string.Empty, line, position);
                }
                else
                {
                    sink.Add(TemplateFailureCode.MalformedXml, path, string.Empty, ex.LineNumber, ex.LinePosition);
                }

                return null;
            }
            catch (XmlTooDeepException ex)
            {
                sink.Add(TemplateFailureCode.XmlTooDeep, path, string.Empty, ex.Line, ex.Position);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            sink.Add(TemplateFailureCode.Unreadable, path, "0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            return null;
        }
    }

    /// <summary>
    /// The text the XML reader uses when it refuses a DOCTYPE, learned from the runtime so it is right in any language.
    /// An error with this text is a DTD refusal; every other XML error is malformed XML.
    /// </summary>
    private static readonly string DtdRefusedMessage = LearnDtdRefusedMessage();

    private static string LearnDtdRefusedMessage()
    {
        try
        {
            using var text = new StringReader("<!DOCTYPE a><a/>");
            using var reader = XmlReader.Create(text, CreateSettings());
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            return ex.Message;
        }

        return "no-message";
    }

    /// <summary>Finds the line and position of the DOCTYPE in the first 64 KiB of the file, or 0 and 0.</summary>
    private static void FindDocumentType(string path, out int line, out int position)
    {
        line = 0;
        position = 0;
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[64 * 1024];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, count);
            var index = text.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
            if (index < 0)
            {
                return;
            }

            line = 1;
            var lineStart = 0;
            for (var i = 0; i < index; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            position = index - lineStart + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            line = 0;
            position = 0;
        }
    }

    private static XElement Build(XmlReader reader)
    {
        var info = reader as IXmlLineInfo;
        var stack = new Stack<XElement>();
        XElement? root = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth >= AdmxLimits.MaxXmlDepth)
                    {
                        throw new XmlTooDeepException((info?.LineNumber ?? 0), (info?.LinePosition ?? 0));
                    }

                    var element = new XElement(reader.LocalName);
                    element.AddAnnotation(new SourcePosition((info?.LineNumber ?? 0), (info?.LinePosition ?? 0)));
                    var empty = reader.IsEmptyElement;
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (reader.Prefix.Length == 0 && reader.LocalName != "xmlns")
                            {
                                element.SetAttributeValue(reader.LocalName, reader.Value);
                            }
                        }

                        reader.MoveToElement();
                    }

                    if (stack.Count > 0)
                    {
                        stack.Peek().Add(element);
                    }
                    else
                    {
                        root = element;
                    }

                    if (!empty)
                    {
                        stack.Push(element);
                    }

                    break;
                case XmlNodeType.EndElement:
                    stack.Pop();
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (stack.Count > 0)
                    {
                        stack.Peek().Add(new XText(reader.Value));
                    }

                    break;
            }
        }

        return root ?? throw new XmlException("No root element.");
    }

    private sealed class XmlTooDeepException(int line, int position) : Exception
    {
        internal int Line { get; } = line;

        internal int Position { get; } = position;
    }
}
