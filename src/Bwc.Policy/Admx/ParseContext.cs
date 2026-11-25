using System.Globalization;
using System.Xml.Linq;

namespace Bwc.Policy.Admx;

/// <summary>Shared reading helpers: attribute access with failure recording for one file.</summary>
/// <param name="path">The file being read.</param>
/// <param name="sink">Where failures of this file go.</param>
/// <param name="readText">
/// Reads the file text for the start tag position (a seam for tests). Null means <see cref="SafeXml.ReadText(string)"/>.
/// It is called at most once per context.
/// </param>
internal sealed class ParseContext(string path, FailureSink sink, Func<string, string?>? readText = null)
{
    private bool textRead;
    private string? text;

    internal string Path { get; } = path;

    internal static SourcePosition PositionOf(XElement element) =>
        element.Annotation<SourcePosition>() ?? new SourcePosition(0, 0);

    internal void Fail(TemplateFailureCode code, XElement? at, string detail = "")
    {
        var position = at is null ? new SourcePosition(0, 0) : PositionOf(at);
        sink.Add(code, Path, detail, position.Line, position.Position);
    }

    /// <summary>
    /// Records a failure at the closing angle bracket of the element's start tag, which is the position classic reports
    /// for a missing attribute (a tag may span lines, and a self-closing tag ends at its own bracket). The position is
    /// found by scanning the file text from the element name, skipping quoted values. When the text cannot be read or
    /// the bracket is not found, the position of the element name is used.
    /// </summary>
    internal void FailAtStartTagEnd(TemplateFailureCode code, XElement element, string detail)
    {
        var start = PositionOf(element);

        // Past the cap the position is never used, so the text is not scanned.
        var end = sink.IsFull ? null : StartTagEnd(start);
        end ??= start;
        sink.Add(code, Path, detail, end.Line, end.Position);
    }

    /// <summary>The file text with line breaks as <c>\n</c>, read at most once; null when it cannot be read.</summary>
    private string? Text()
    {
        if (!textRead)
        {
            textRead = true;
            text = (readText ?? SafeXml.ReadText)(Path)?.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        return text;
    }

    private SourcePosition? StartTagEnd(SourcePosition start)
    {
        if (start.Line <= 0 || start.Position <= 0)
        {
            return null;
        }

        try
        {
            var text = Text();
            if (text is null)
            {
                return null;
            }

            var offset = 0;
            for (var line = 1; line < start.Line; line++)
            {
                var next = text.IndexOf('\n', offset);
                if (next < 0)
                {
                    return null;
                }

                offset = next + 1;
            }

            var lineNumber = start.Line;
            var lineStart = offset;
            var quote = '\0';
            for (var i = offset + start.Position - 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\n')
                {
                    lineNumber++;
                    lineStart = i + 1;
                }
                else if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return new SourcePosition(lineNumber, i - lineStart + 1);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    /// <summary>
    /// Reads a registry key attribute with its trailing backslashes removed: the registry layer refuses a key that ends in
    /// one and Windows ships keys that do (failure mode 46). A leading backslash or an empty middle segment stays.
    /// </summary>
    internal static string? KeyAttr(XElement element, string name) => Attr(element, name)?.TrimEnd('\\');

    /// <summary>
    /// Reads an optional boolean attribute. A value that is not true, false, 1 or 0 in lower case is recorded and ignored:
    /// classic refuses the whole template for required="TRUE" and soft="True" (H/BoolAttributeUpper, H/BoolAttributeMixed).
    /// </summary>
    internal bool? OptionalBool(XElement element, string name)
    {
        var text = Attr(element, name);
        if (text is null)
        {
            return null;
        }

        switch (text.Trim())
        {
            case "true" or "1":
                return true;
            case "false" or "0":
                return false;
            default:
                Fail(TemplateFailureCode.InvalidAttributeValue, element, name);
                return null;
        }
    }

    /// <summary>Reads an optional non-negative integer attribute. A value that is not one is recorded and ignored.</summary>
    internal ulong? OptionalNumber(XElement element, string name)
    {
        var text = Attr(element, name);
        if (text is null)
        {
            return null;
        }

        if (ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        Fail(TemplateFailureCode.InvalidAttributeValue, element, name);
        return null;
    }

    /// <summary>Checks a name against the length limit. Records NameTooLong and returns false when it is longer.</summary>
    internal bool CheckName(XElement element, string attribute, string value)
    {
        if (value.Length <= AdmxLimits.MaxNameLength)
        {
            return true;
        }

        Fail(TemplateFailureCode.NameTooLong, element, attribute);
        return false;
    }

    /// <summary>Checks a registry key path against the path and component limits.</summary>
    internal bool CheckKey(XElement element, string attribute, string value)
    {
        var ok = value.Length <= AdmxLimits.MaxKeyLength;
        if (ok)
        {
            foreach (var component in value.Split('\\'))
            {
                if (component.Length > AdmxLimits.MaxKeyComponentLength)
                {
                    ok = false;
                    break;
                }
            }
        }

        if (!ok)
        {
            Fail(TemplateFailureCode.NameTooLong, element, attribute);
        }

        return ok;
    }

    /// <summary>Checks a registry value name against its limit.</summary>
    internal bool CheckValueName(XElement element, string attribute, string value)
    {
        if (value.Length <= AdmxLimits.MaxValueNameLength)
        {
            return true;
        }

        Fail(TemplateFailureCode.NameTooLong, element, attribute);
        return false;
    }
}
