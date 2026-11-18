using System.Globalization;
using System.Xml.Linq;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// One element of a fixture policy, joined with its presentation control (the label and defaults the dialog shows).
internal sealed class ElementInfo
{
    internal string Id { get; init; } = string.Empty;

    // decimal, longDecimal, text, boolean, enum, list, multiText
    internal string Kind { get; init; } = string.Empty;

    // The presentation control: textBox, decimalTextBox, longDecimalTextBox, checkBox, comboBox, dropdownList, listBox, multiTextBox
    internal string Control { get; init; } = string.Empty;

    // Position among the presentation controls that are not plain text (the driver's option index).
    internal int ControlIndex { get; init; }

    internal string? Label { get; init; }

    internal string? DefaultText { get; init; }

    internal bool DefaultChecked { get; init; }

    internal List<string> Suggestions { get; init; } = new();

    internal long? Min { get; init; }

    internal long? Max { get; init; }

    internal bool Required { get; init; }

    internal bool Soft { get; init; }

    internal bool StoreAsText { get; init; }

    internal bool Expandable { get; init; }

    internal int? MaxLength { get; init; }

    internal int? MaxStrings { get; init; }

    internal bool Additive { get; init; }

    internal bool ExplicitValue { get; init; }

    internal string? ValuePrefix { get; init; }

    internal string? Key { get; init; }

    internal string? ValueName { get; init; }

    internal List<string> EnumItems { get; init; } = new();
}

internal sealed class PolicyInfo
{
    internal string Name { get; init; } = string.Empty;

    // Machine, User or Both
    internal string Class { get; init; } = string.Empty;

    internal string DisplayName { get; init; } = string.Empty;

    internal string Key { get; init; } = string.Empty;

    internal string? ValueName { get; init; }

    // Display names from the top category down to the policy's own category.
    internal List<string> CategoryPath { get; init; } = new();

    internal List<ElementInfo> Elements { get; init; } = new();

    // The tree path below the Administrative Templates node's root (root name is not part of it).
    internal List<string> NodePath => new List<string> { "Administrative Templates" }.Concat(CategoryPath).ToList();

    internal IEnumerable<string> Roots => Class switch
    {
        "Machine" => new[] { "Computer" },
        "User" => new[] { "User" },
        _ => new[] { "Computer", "User" },
    };

    // Case id: the policy name, with the root added for a class Both policy (it appears under two roots).
    internal string CaseId(string root) => Class == "Both" ? $"{Name}.{root}" : Name;
}

// The good fixture templates read as data: policies, categories, element definitions, presentation labels, and the set of every string the
// fixture contains (the scrubber keeps these in tracked files). Read with plain XML; no schema.
internal sealed class FixtureModel
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    internal List<PolicyInfo> Policies { get; } = new();

    internal HashSet<string> AllowedStrings { get; } = new(StringComparer.Ordinal);

    // Category display names in definition order (parent first) with their parent's name, to enumerate tree nodes.
    internal List<(string Name, string Display, string? Parent)> Categories { get; } = new();

    internal PolicyInfo Policy(string name) =>
        Policies.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException($"the fixture has no policy '{name}'");

    internal static string FixtureDir(RepoPaths repo) => Path.Combine(repo.Root, "docs", "parity", "oracle", "gpedit", "fixture");

    internal static FixtureModel Load(RepoPaths repo)
    {
        string dir = FixtureDir(repo);
        var model = new FixtureModel();
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var presentations = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (string name in new[] { "BwcOracleBase", "BwcOracle" })
        {
            XDocument adml = Read(Path.Combine(dir, "en-US", name + ".adml"));
            foreach (XElement s in adml.Descendants(Ns + "string"))
            {
                strings[(string?)s.Attribute("id") ?? string.Empty] = s.Value;
            }

            foreach (XElement p in adml.Descendants(Ns + "presentation"))
            {
                presentations[(string?)p.Attribute("id") ?? string.Empty] = p.Elements().ToList();
            }

            foreach (XElement e in adml.Descendants().Where(x => x.Name.LocalName is "displayName" or "description" && !x.HasElements))
            {
                model.AllowedStrings.Add(e.Value);
            }
        }

        foreach (string value in strings.Values)
        {
            model.AllowedStrings.Add(value);
        }

        string Resolve(string? reference, string kind)
        {
            if (reference is null)
            {
                return string.Empty;
            }

            string prefix = "$(" + kind + ".";
            string id = reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..^1] : reference;
            return strings.TryGetValue(id, out string? text) ? text : string.Empty;
        }

        var categories = new Dictionary<string, (string Display, string? Parent)>(StringComparer.Ordinal);
        var documents = new List<XDocument>();
        foreach (string name in new[] { "BwcOracleBase", "BwcOracle" })
        {
            XDocument admx = Read(Path.Combine(dir, name + ".admx"));
            documents.Add(admx);
            foreach (XAttribute attribute in admx.Descendants().SelectMany(e => e.Attributes()))
            {
                model.AllowedStrings.Add(attribute.Value);
            }

            foreach (XElement text in admx.Descendants().Where(e => !e.HasElements && e.Value.Length > 0))
            {
                model.AllowedStrings.Add(text.Value);
            }

            foreach (XElement category in admx.Descendants(Ns + "category"))
            {
                string catName = (string?)category.Attribute("name") ?? string.Empty;
                string? parent = (string?)category.Element(Ns + "parentCategory")?.Attribute("ref");
                if (parent is not null && parent.Contains(':'))
                {
                    parent = parent[(parent.IndexOf(':') + 1)..];
                }

                categories[catName] = (Resolve((string?)category.Attribute("displayName"), "string"), parent);
                model.Categories.Add((catName, categories[catName].Display, parent));
            }
        }

        List<string> CategoryPath(string name)
        {
            var path = new List<string>();
            string? current = name;
            while (current is not null && categories.TryGetValue(current, out var entry))
            {
                path.Insert(0, entry.Display);
                current = entry.Parent;
            }

            return path;
        }

        foreach (XDocument admx in documents)
        {
            foreach (XElement policy in admx.Descendants(Ns + "policy"))
            {
                string? parent = (string?)policy.Element(Ns + "parentCategory")?.Attribute("ref");
                if (parent is not null && parent.Contains(':'))
                {
                    parent = parent[(parent.IndexOf(':') + 1)..];
                }

                string? presentationRef = (string?)policy.Attribute("presentation");
                List<XElement> controls = presentationRef is null
                    ? new List<XElement>()
                    : presentations.GetValueOrDefault(presentationRef.Replace("$(presentation.", string.Empty, StringComparison.Ordinal).TrimEnd(')')) ?? new List<XElement>();
                model.Policies.Add(new PolicyInfo
                {
                    Name = (string?)policy.Attribute("name") ?? string.Empty,
                    Class = (string?)policy.Attribute("class") ?? string.Empty,
                    DisplayName = Resolve((string?)policy.Attribute("displayName"), "string"),
                    Key = (string?)policy.Attribute("key") ?? string.Empty,
                    ValueName = (string?)policy.Attribute("valueName"),
                    CategoryPath = parent is null ? new List<string>() : CategoryPath(parent),
                    Elements = ReadElements(policy, controls, Resolve),
                });
            }
        }

        return model;
    }

    private static XDocument Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"the oracle fixture file {path} was not found.");
        }

        return XDocument.Load(path);
    }

    private static List<ElementInfo> ReadElements(XElement policy, List<XElement> controls, Func<string?, string, string> resolve)
    {
        var result = new List<ElementInfo>();
        XElement? elements = policy.Element(Ns + "elements");
        if (elements is null)
        {
            return result;
        }

        List<XElement> real = controls.Where(c => c.Name.LocalName != "text").ToList();
        foreach (XElement element in elements.Elements())
        {
            string id = (string?)element.Attribute("id") ?? string.Empty;
            int index = real.FindIndex(c => (string?)c.Attribute("refId") == id);
            XElement? control = index >= 0 ? real[index] : null;
            string controlName = control?.Name.LocalName ?? string.Empty;
            string? label = control is null ? null
                : control.Element(Ns + "label") is { } labelElement ? labelElement.Value
                : control.Name.LocalName is "multiTextBox" ? null
                : control.Value.Trim().Length > 0 ? control.Value.Trim() : null;
            string? defaultText = control?.Element(Ns + "defaultValue")?.Value ?? control?.Element(Ns + "default")?.Value ?? (string?)control?.Attribute("defaultValue");
            result.Add(new ElementInfo
            {
                Id = id,
                Kind = element.Name.LocalName,
                Control = controlName,
                ControlIndex = index,
                Label = label,
                DefaultText = defaultText,
                DefaultChecked = string.Equals((string?)control?.Attribute("defaultChecked"), "true", StringComparison.OrdinalIgnoreCase),
                Suggestions = control?.Elements(Ns + "suggestion").Select(s => s.Value).ToList() ?? new List<string>(),
                Min = Long(element, "minValue"),
                Max = Long(element, "maxValue"),
                Required = Flag(element, "required"),
                Soft = Flag(element, "soft"),
                StoreAsText = Flag(element, "storeAsText"),
                Expandable = Flag(element, "expandable"),
                MaxLength = (int?)Long(element, "maxLength"),
                MaxStrings = (int?)Long(element, "maxStrings"),
                Additive = Flag(element, "additive"),
                ExplicitValue = Flag(element, "explicitValue"),
                ValuePrefix = (string?)element.Attribute("valuePrefix"),
                Key = (string?)element.Attribute("key"),
                ValueName = (string?)element.Attribute("valueName"),
                EnumItems = element.Elements(Ns + "item").Select(i => resolve((string?)i.Attribute("displayName"), "string")).ToList(),
            });
        }

        return result;
    }

    private static long? Long(XElement element, string name) =>
        long.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static bool Flag(XElement element, string name) => string.Equals((string?)element.Attribute(name), "true", StringComparison.OrdinalIgnoreCase);

    // Every category node of one root that holds a policy of the class (directly or below), as paths below the root, parent first.
    internal List<List<string>> NodePaths(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<List<string>>();
        foreach (PolicyInfo policy in Policies.Where(p => p.Roots.Contains(root)))
        {
            for (int depth = 1; depth <= policy.CategoryPath.Count; depth++)
            {
                List<string> node = new List<string> { "Administrative Templates" }.Concat(policy.CategoryPath.Take(depth)).ToList();
                if (seen.Add(string.Join("/", node)))
                {
                    paths.Add(node);
                }
            }
        }

        return paths;
    }
}
