using System.Xml.Linq;

namespace Bwc.Policy.Admx;

/// <summary>What an ADML provides: strings and presentations.</summary>
internal sealed class ParsedAdml
{
    internal string? DisplayName { get; init; }

    internal string? Description { get; init; }

    internal Dictionary<string, string> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, PolicyPresentation> Presentations { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reads an ADML element tree: the string table and the presentation table.</summary>
internal static class AdmlParser
{
    internal static ParsedAdml? Parse(XElement root, ParseContext context)
    {
        if (root.Name.LocalName != "policyDefinitionResources")
        {
            context.Fail(TemplateFailureCode.NotATemplate, root, "policyDefinitionResources");
            return null;
        }

        var adml = new ParsedAdml
        {
            DisplayName = root.Element("displayName")?.Value,
            Description = root.Element("description")?.Value,
        };
        var resources = root.Element("resources");
        ReadStrings(resources?.Element("stringTable"), adml, context);
        ReadPresentations(resources?.Element("presentationTable"), adml, context);
        return adml;
    }

    private static void ReadStrings(XElement? table, ParsedAdml adml, ParseContext context)
    {
        if (table is null)
        {
            return;
        }

        foreach (var entry in table.Elements("string"))
        {
            var id = ParseContext.Attr(entry, "id");
            if (string.IsNullOrEmpty(id))
            {
                context.Fail(TemplateFailureCode.MissingAttribute, entry, "id");
                continue;
            }

            var text = entry.Value;
            if (text.Length > AdmxLimits.MaxStringLength)
            {
                context.Fail(TemplateFailureCode.StringTooLong, entry, id);
                continue;
            }

            if (!adml.Strings.TryAdd(id, text))
            {
                context.Fail(TemplateFailureCode.DuplicateString, entry, id);
            }
        }
    }

    private static void ReadPresentations(XElement? table, ParsedAdml adml, ParseContext context)
    {
        if (table is null)
        {
            return;
        }

        foreach (var presentation in table.Elements("presentation"))
        {
            var id = ParseContext.Attr(presentation, "id");
            if (string.IsNullOrEmpty(id))
            {
                context.Fail(TemplateFailureCode.MissingAttribute, presentation, "id");
                continue;
            }

            var controls = new List<PresentationControl>();
            foreach (var child in presentation.Elements())
            {
                var control = ReadControl(child, context);
                if (control is not null)
                {
                    controls.Add(control);
                }
            }

            if (!adml.Presentations.TryAdd(id, new PolicyPresentation(id, controls)))
            {
                context.Fail(TemplateFailureCode.DuplicatePresentation, presentation, id);
            }
        }
    }

    private static PresentationControl? ReadControl(XElement element, ParseContext context)
    {
        PresentationControlKind kind;
        switch (element.Name.LocalName)
        {
            case "text":
                kind = PresentationControlKind.Text;
                break;
            case "checkBox":
                kind = PresentationControlKind.CheckBox;
                break;
            case "textBox":
                kind = PresentationControlKind.TextBox;
                break;
            case "decimalTextBox":
                kind = PresentationControlKind.DecimalTextBox;
                break;
            case "longDecimalTextBox":
                kind = PresentationControlKind.LongDecimalTextBox;
                break;
            case "comboBox":
                kind = PresentationControlKind.ComboBox;
                break;
            case "dropdownList":
                kind = PresentationControlKind.DropdownList;
                break;
            case "listBox":
                kind = PresentationControlKind.ListBox;
                break;
            case "multiTextBox":
                kind = PresentationControlKind.MultiTextBox;
                break;
            default:
                return null;
        }

        var labelChild = element.Element("label");
        var label = labelChild?.Value ?? (element.HasElements || element.Value.Length == 0 ? null : element.Value);
        var defaultValue = ParseContext.Attr(element, "defaultValue")
            ?? element.Element("defaultValue")?.Value
            ?? element.Element("default")?.Value;
        var numeric = kind is PresentationControlKind.DecimalTextBox or PresentationControlKind.LongDecimalTextBox;
        var sortable = kind is PresentationControlKind.ComboBox or PresentationControlKind.DropdownList;
        return new PresentationControl(kind, ParseContext.Attr(element, "refId"))
        {
            Label = label,
            DefaultValue = defaultValue,
            DefaultChecked = kind == PresentationControlKind.CheckBox ? context.OptionalBool(element, "defaultChecked") : null,
            Spin = numeric ? context.OptionalBool(element, "spin") : null,
            SpinStep = numeric ? context.OptionalNumber(element, "spinStep") : null,
            NoSort = sortable ? context.OptionalBool(element, "noSort") : null,
            DefaultItem = kind == PresentationControlKind.DropdownList ? context.OptionalNumber(element, "defaultItem") : null,
            Required = kind == PresentationControlKind.ListBox ? context.OptionalBool(element, "required") : null,
            DefaultHeight = kind == PresentationControlKind.MultiTextBox ? context.OptionalNumber(element, "defaultHeight") : null,
            Suggestions = element.Elements("suggestion").Select(s => s.Value).ToArray(),
        };
    }
}
