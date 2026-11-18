using System.Globalization;
using System.Xml.Linq;

namespace Bwc.Policy.Admx;

/// <summary>Shared reading helpers: attribute access with failure recording for one file.</summary>
internal sealed class ParseContext(string path, FailureSink sink)
{
    internal string Path { get; } = path;

    internal static SourcePosition PositionOf(XElement element) =>
        element.Annotation<SourcePosition>() ?? new SourcePosition(0, 0);

    internal void Fail(TemplateFailureCode code, XElement? at, string detail = "")
    {
        var position = at is null ? new SourcePosition(0, 0) : PositionOf(at);
        sink.Add(code, Path, detail, position.Line, position.Position);
    }

    internal static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    /// <summary>Reads an optional boolean attribute. A value that is not true, false, 1 or 0 is recorded and ignored.</summary>
    internal bool? OptionalBool(XElement element, string name)
    {
        var text = Attr(element, name);
        if (text is null)
        {
            return null;
        }

        switch (text.Trim().ToLowerInvariant())
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
