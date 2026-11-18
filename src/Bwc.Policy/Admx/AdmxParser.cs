using System.Xml.Linq;

namespace Bwc.Policy.Admx;

/// <summary>What one ADMX provides before linking. References to other files are still raw text.</summary>
internal sealed class ParsedAdmx(TemplateFile file)
{
    internal TemplateFile File { get; } = file;

    internal List<SupportedOnDefinition> SupportedOn { get; } = [];

    internal List<PolicyCategory> Categories { get; } = [];

    internal List<PolicyDefinition> Policies { get; } = [];
}

/// <summary>
/// Reads an ADMX element tree. It never validates against the schema: it reads what it needs and ignores the rest.
/// An item with a structural fault is dropped and recorded; a text or reference fault is recorded and the item stays.
/// </summary>
internal static class AdmxParser
{
    private const string StringPrefix = "$(string.";
    private const string PresentationPrefix = "$(presentation.";

    /// <summary>Reads the target namespace, the prefix and the using declarations.</summary>
    internal static TemplateFile? ReadHeader(XElement root, string admxPath, ParseContext context)
    {
        if (root.Name.LocalName != "policyDefinitions")
        {
            context.Fail(TemplateFailureCode.NotATemplate, root, "policyDefinitions");
            return null;
        }

        var namespaces = root.Element("policyNamespaces");
        var target = namespaces?.Element("target");
        var ns = target is null ? null : ParseContext.Attr(target, "namespace");
        var prefix = target is null ? null : ParseContext.Attr(target, "prefix");
        if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(prefix))
        {
            context.Fail(TemplateFailureCode.NotATemplate, target ?? root, string.IsNullOrEmpty(ns) ? "namespace" : "prefix");
            return null;
        }

        if (!context.CheckName(target!, "namespace", ns) || !context.CheckName(target!, "prefix", prefix))
        {
            return null;
        }

        var usings = new List<NamespaceUsing>();
        foreach (var use in namespaces!.Elements("using"))
        {
            var usePrefix = ParseContext.Attr(use, "prefix");
            var useNamespace = ParseContext.Attr(use, "namespace");
            if (string.IsNullOrEmpty(usePrefix) || string.IsNullOrEmpty(useNamespace))
            {
                context.Fail(TemplateFailureCode.MissingAttribute, use, string.IsNullOrEmpty(usePrefix) ? "prefix" : "namespace");
                continue;
            }

            if (context.CheckName(use, "prefix", usePrefix) && context.CheckName(use, "namespace", useNamespace))
            {
                usings.Add(new NamespaceUsing(usePrefix, useNamespace));
            }
        }

        return new TemplateFile(admxPath, ns, prefix, usings);
    }

    /// <summary>Reads the supported-on definitions, the categories and the policies.</summary>
    internal static ParsedAdmx ParseBody(XElement root, TemplateFile file, ParsedAdml adml, ParseContext context)
    {
        var reader = new BodyReader(file, adml, context);
        var parsed = new ParsedAdmx(file);
        reader.ReadSupportedOn(root, parsed);
        reader.ReadCategories(root, parsed);
        reader.ReadPolicies(root, parsed);
        return parsed;
    }

    private sealed class BodyReader(TemplateFile file, ParsedAdml adml, ParseContext context)
    {
        internal void ReadSupportedOn(XElement root, ParsedAdmx parsed)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitions = root.Element("supportedOn")?.Element("definitions");
            if (definitions is null)
            {
                return;
            }

            foreach (var definition in definitions.Elements("definition"))
            {
                var name = ParseContext.Attr(definition, "name");
                if (string.IsNullOrEmpty(name))
                {
                    context.Fail(TemplateFailureCode.MissingAttribute, definition, "name");
                    continue;
                }

                if (!context.CheckName(definition, "name", name))
                {
                    continue;
                }

                if (!names.Add(name))
                {
                    context.Fail(TemplateFailureCode.DuplicateSupportedOn, definition, name);
                    continue;
                }

                var position = ParseContext.PositionOf(definition);
                parsed.SupportedOn.Add(new SupportedOnDefinition(
                    file, name, DisplayText(definition, name), position.Line, position.Position));
            }
        }

        internal void ReadCategories(XElement root, ParsedAdmx parsed)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var categories = root.Element("categories");
            if (categories is null)
            {
                return;
            }

            foreach (var element in categories.Elements("category"))
            {
                var name = ParseContext.Attr(element, "name");
                if (string.IsNullOrEmpty(name))
                {
                    context.Fail(TemplateFailureCode.MissingAttribute, element, "name");
                    continue;
                }

                if (!context.CheckName(element, "name", name))
                {
                    continue;
                }

                if (!names.Add(name))
                {
                    context.Fail(TemplateFailureCode.DuplicateCategory, element, name);
                    continue;
                }

                var position = ParseContext.PositionOf(element);
                parsed.Categories.Add(new PolicyCategory(
                    file,
                    name,
                    DisplayText(element, name),
                    Text(element, "explainText") ?? string.Empty,
                    element.Element("parentCategory") is { } parent ? ParseContext.Attr(parent, "ref") ?? string.Empty : null,
                    position.Line,
                    position.Position));
            }
        }

        internal void ReadPolicies(XElement root, ParsedAdmx parsed)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var policies = root.Element("policies");
            if (policies is null)
            {
                return;
            }

            foreach (var element in policies.Elements("policy"))
            {
                var name = ParseContext.Attr(element, "name");
                if (string.IsNullOrEmpty(name))
                {
                    context.Fail(TemplateFailureCode.MissingAttribute, element, "name");
                    continue;
                }

                if (!context.CheckName(element, "name", name))
                {
                    continue;
                }

                if (!names.Add(name))
                {
                    context.Fail(TemplateFailureCode.DuplicatePolicy, element, name);
                    continue;
                }

                var policy = ReadPolicy(element, name);
                if (policy is not null)
                {
                    parsed.Policies.Add(policy);
                }
            }
        }

        private PolicyDefinition? ReadPolicy(XElement element, string name)
        {
            var fatal = false;
            var policyClass = PolicyClass.Machine;
            var classText = ParseContext.Attr(element, "class");
            if (classText is null)
            {
                context.Fail(TemplateFailureCode.MissingAttribute, element, "class");
                fatal = true;
            }
            else if (!TryReadClass(classText, out policyClass))
            {
                context.Fail(TemplateFailureCode.InvalidAttributeValue, element, "class");
                fatal = true;
            }

            var key = ParseContext.Attr(element, "key");
            if (string.IsNullOrEmpty(key))
            {
                context.Fail(TemplateFailureCode.MissingAttribute, element, "key");
                fatal = true;
                key = string.Empty;
            }
            else if (!context.CheckKey(element, "key", key))
            {
                fatal = true;
            }

            var valueName = ParseContext.Attr(element, "valueName");
            if (valueName is not null && !context.CheckValueName(element, "valueName", valueName))
            {
                fatal = true;
            }

            var elements = new List<PolicyElement>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var elementsNode = element.Element("elements");
            if (elementsNode is not null)
            {
                foreach (var child in elementsNode.Elements())
                {
                    var read = ReadElement(child, key, ref fatal);
                    if (read is null)
                    {
                        continue;
                    }

                    if (!ids.Add(read.Id))
                    {
                        context.Fail(TemplateFailureCode.DuplicateElement, child, read.Id);
                        fatal = true;
                        continue;
                    }

                    elements.Add(read);
                }
            }

            var enabledList = ReadList(element.Element("enabledList"), key, ref fatal);
            var disabledList = ReadList(element.Element("disabledList"), key, ref fatal);
            if (fatal)
            {
                return null;
            }

            var position = ParseContext.PositionOf(element);
            var presentationId = ReadPresentationId(element);
            var policy = new PolicyDefinition(
                file,
                name,
                policyClass,
                key,
                element.Element("parentCategory") is { } category ? ParseContext.Attr(category, "ref") : null,
                element.Element("supportedOn") is { } supported ? ParseContext.Attr(supported, "ref") : null,
                position.Line,
                position.Position)
            {
                DisplayName = DisplayText(element, name),
                ExplainText = Text(element, "explainText") ?? string.Empty,
                ValueName = valueName,
                EnabledValue = ReadLiteral(element.Element("enabledValue")),
                DisabledValue = ReadLiteral(element.Element("disabledValue")),
                EnabledList = enabledList,
                DisabledList = disabledList,
                Elements = elements,
                ClientExtension = ParseContext.Attr(element, "clientExtension"),
                PresentationId = presentationId,
            };
            if (presentationId is not null)
            {
                policy.Presentation = BindPresentation(element, presentationId, elements);
            }

            return policy;
        }

        private string? ReadPresentationId(XElement element)
        {
            var raw = ParseContext.Attr(element, "presentation");
            if (raw is null)
            {
                return null;
            }

            if (raw.StartsWith(PresentationPrefix, StringComparison.Ordinal) && raw.EndsWith(')') && raw.Length > PresentationPrefix.Length + 1)
            {
                return raw[PresentationPrefix.Length..^1];
            }

            context.Fail(TemplateFailureCode.UndefinedPresentation, element, raw);
            return null;
        }

        private PolicyPresentation? BindPresentation(XElement element, string id, List<PolicyElement> elements)
        {
            if (!adml.Presentations.TryGetValue(id, out var presentation))
            {
                context.Fail(TemplateFailureCode.UndefinedPresentation, element, id);
                return null;
            }

            var controls = new List<PresentationControl>(presentation.Controls.Count);
            foreach (var control in presentation.Controls)
            {
                PolicyElement? target = null;
                if (control.RefId is not null)
                {
                    target = elements.Find(e => string.Equals(e.Id, control.RefId, StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                    {
                        context.Fail(TemplateFailureCode.UndefinedPresentationRef, element, control.RefId);
                    }
                }

                controls.Add(control.WithElement(target));
            }

            return new PolicyPresentation(presentation.Id, controls);
        }

        private PolicyElement? ReadElement(XElement element, string policyKey, ref bool fatal)
        {
            var kind = element.Name.LocalName;
            if (kind is not ("boolean" or "decimal" or "longDecimal" or "text" or "multiText" or "enum" or "list"))
            {
                return null;
            }

            var id = ParseContext.Attr(element, "id");
            if (string.IsNullOrEmpty(id))
            {
                context.Fail(TemplateFailureCode.MissingAttribute, element, "id");
                fatal = true;
                return null;
            }

            if (!context.CheckName(element, "id", id))
            {
                fatal = true;
                return null;
            }

            var key = ParseContext.Attr(element, "key");
            if (key is not null && !context.CheckKey(element, "key", key))
            {
                fatal = true;
            }

            var valueName = ParseContext.Attr(element, "valueName");
            if (valueName is not null && !context.CheckValueName(element, "valueName", valueName))
            {
                fatal = true;
            }

            var effectiveKey = key ?? policyKey;
            var required = context.OptionalBool(element, "required") ?? false;
            var clientExtension = ParseContext.Attr(element, "clientExtension");
            switch (kind)
            {
                case "boolean":
                    return new BooleanElement(id, key, effectiveKey, valueName)
                    {
                        Required = required,
                        ClientExtension = clientExtension,
                        TrueValue = ReadLiteral(element.Element("trueValue")),
                        FalseValue = ReadLiteral(element.Element("falseValue")),
                        TrueList = ReadList(element.Element("trueList"), effectiveKey, ref fatal),
                        FalseList = ReadList(element.Element("falseList"), effectiveKey, ref fatal),
                    };
                case "decimal" or "longDecimal":
                    return new DecimalElement(
                        kind == "decimal" ? PolicyElementKind.Decimal : PolicyElementKind.LongDecimal,
                        id,
                        key,
                        effectiveKey,
                        valueName)
                    {
                        Required = required,
                        ClientExtension = clientExtension,
                        MinValue = context.OptionalNumber(element, "minValue"),
                        MaxValue = context.OptionalNumber(element, "maxValue"),
                        Soft = context.OptionalBool(element, "soft") ?? false,
                        StoreAsText = context.OptionalBool(element, "storeAsText") ?? false,
                    };
                case "text":
                    return new TextElement(id, key, effectiveKey, valueName)
                    {
                        Required = required,
                        ClientExtension = clientExtension,
                        Expandable = context.OptionalBool(element, "expandable") ?? false,
                        MaxLength = context.OptionalNumber(element, "maxLength"),
                    };
                case "multiText":
                    return new MultiTextElement(id, key, effectiveKey, valueName)
                    {
                        Required = required,
                        ClientExtension = clientExtension,
                        MaxLength = context.OptionalNumber(element, "maxLength"),
                        MaxStrings = context.OptionalNumber(element, "maxStrings"),
                    };
                case "enum":
                    return new EnumElement(id, key, effectiveKey, valueName, ReadEnumItems(element, effectiveKey, ref fatal))
                    {
                        Required = required,
                        ClientExtension = clientExtension,
                    };
                default:
                    return new ListElement(id, key, effectiveKey, valueName)
                    {
                        Required = required,
                        ClientExtension = clientExtension,
                        Additive = context.OptionalBool(element, "additive") ?? false,
                        ExplicitValue = context.OptionalBool(element, "explicitValue") ?? false,
                        Expandable = context.OptionalBool(element, "expandable") ?? false,
                        ValuePrefix = ParseContext.Attr(element, "valuePrefix"),
                    };
            }
        }

        private List<EnumItem> ReadEnumItems(XElement element, string effectiveKey, ref bool fatal)
        {
            var items = new List<EnumItem>();
            foreach (var item in element.Elements("item"))
            {
                var displayName = DisplayText(item, string.Empty);
                items.Add(new EnumItem(
                    displayName,
                    ReadLiteral(item.Element("value")),
                    ReadList(item.Element("valueList"), effectiveKey, ref fatal)));
            }

            return items;
        }

        private PolicyValueList? ReadList(XElement? holder, string fallbackKey, ref bool fatal)
        {
            if (holder is null)
            {
                return null;
            }

            var defaultKey = ParseContext.Attr(holder, "defaultKey");
            if (defaultKey is not null && !context.CheckKey(holder, "defaultKey", defaultKey))
            {
                fatal = true;
            }

            var items = new List<PolicyValueListItem>();
            foreach (var item in holder.Elements("item"))
            {
                var key = ParseContext.Attr(item, "key");
                if (key is not null && !context.CheckKey(item, "key", key))
                {
                    fatal = true;
                }

                var valueName = ParseContext.Attr(item, "valueName");
                if (valueName is null)
                {
                    context.Fail(TemplateFailureCode.MissingAttribute, item, "valueName");
                    fatal = true;
                    continue;
                }

                if (!context.CheckValueName(item, "valueName", valueName))
                {
                    fatal = true;
                    continue;
                }

                items.Add(new PolicyValueListItem(key, key ?? defaultKey ?? fallbackKey, valueName, ReadLiteral(item.Element("value"))));
            }

            return new PolicyValueList(defaultKey, items);
        }

        private PolicyValue? ReadLiteral(XElement? holder)
        {
            var literal = holder?.Elements().FirstOrDefault();
            if (literal is null)
            {
                return null;
            }

            switch (literal.Name.LocalName)
            {
                case "decimal":
                    return ReadNumber(literal, PolicyValueKind.Decimal);
                case "longDecimal":
                    return ReadNumber(literal, PolicyValueKind.LongDecimal);
                case "string":
                    return new PolicyValue(PolicyValueKind.String, 0, literal.Value);
                case "delete":
                    return new PolicyValue(PolicyValueKind.Delete, 0, null);
                default:
                    return null;
            }
        }

        private PolicyValue? ReadNumber(XElement literal, PolicyValueKind kind)
        {
            if (ParseContext.Attr(literal, "value") is null)
            {
                context.Fail(TemplateFailureCode.MissingAttribute, literal, "value");
                return null;
            }

            var number = context.OptionalNumber(literal, "value");
            return number is null ? null : new PolicyValue(kind, number.Value, null);
        }

        private string? Text(XElement element, string attribute)
        {
            var raw = ParseContext.Attr(element, attribute);
            if (raw is null)
            {
                return null;
            }

            if (raw.StartsWith(StringPrefix, StringComparison.Ordinal) && raw.EndsWith(')') && raw.Length > StringPrefix.Length + 1)
            {
                var id = raw[StringPrefix.Length..^1];
                if (adml.Strings.TryGetValue(id, out var text))
                {
                    return text;
                }

                context.Fail(TemplateFailureCode.UndefinedString, element, id);
                return null;
            }

            return raw;
        }

        private string DisplayText(XElement element, string fallback)
        {
            if (ParseContext.Attr(element, "displayName") is null)
            {
                context.Fail(TemplateFailureCode.MissingAttribute, element, "displayName");
                return fallback;
            }

            return Text(element, "displayName") ?? fallback;
        }

        private static bool TryReadClass(string text, out PolicyClass policyClass)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "machine":
                    policyClass = PolicyClass.Machine;
                    return true;
                case "user":
                    policyClass = PolicyClass.User;
                    return true;
                case "both":
                    policyClass = PolicyClass.Both;
                    return true;
                default:
                    policyClass = PolicyClass.Machine;
                    return false;
            }
        }
    }
}
