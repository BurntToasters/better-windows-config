namespace Bwc.Policy.Admx;

/// <summary>A using declaration of a template file: a prefix for another namespace.</summary>
/// <param name="Prefix">The prefix used in references.</param>
/// <param name="Namespace">The namespace it stands for.</param>
public sealed record NamespaceUsing(string Prefix, string Namespace);

/// <summary>One loaded template: an ADMX and the ADML that carries its strings.</summary>
public sealed class TemplateFile
{
    internal TemplateFile(string admxPath, string ns, string prefix, IReadOnlyList<NamespaceUsing> usings)
    {
        AdmxPath = admxPath;
        Namespace = ns;
        Prefix = prefix;
        Usings = usings;
    }

    /// <summary>The full path of the ADMX file.</summary>
    public string AdmxPath { get; }

    /// <summary>The full path of the ADML file that was read.</summary>
    public string AdmlPath { get; internal set; } = string.Empty;

    /// <summary>The language folder the ADML came from (for example en-US).</summary>
    public string Culture { get; internal set; } = string.Empty;

    /// <summary>The target namespace.</summary>
    public string Namespace { get; }

    /// <summary>The prefix that the file gives its own namespace.</summary>
    public string Prefix { get; }

    /// <summary>The using declarations in document order.</summary>
    public IReadOnlyList<NamespaceUsing> Usings { get; }

    /// <summary>The index of the source folder in the order the folders were passed.</summary>
    public int SourceIndex { get; internal set; }

    /// <summary>The index of the file in the loaded set (source order, then file name order).</summary>
    public int Order { get; internal set; }

    /// <summary>The display name from the ADML, or null.</summary>
    public string? DisplayName { get; internal set; }

    /// <summary>The description from the ADML, or null.</summary>
    public string? Description { get; internal set; }
}

/// <summary>A supported-on definition: the text that says which systems a policy applies to.</summary>
public sealed class SupportedOnDefinition
{
    internal SupportedOnDefinition(TemplateFile file, string name, string displayName, int line, int position)
    {
        File = file;
        Name = name;
        DisplayName = displayName;
        Line = line;
        Position = position;
    }

    /// <summary>The template file that defines it.</summary>
    public TemplateFile File { get; }

    /// <summary>The namespace of the defining file.</summary>
    public string Namespace => File.Namespace;

    /// <summary>The definition name, as written.</summary>
    public string Name { get; }

    /// <summary>The resolved display text.</summary>
    public string DisplayName { get; }

    /// <summary>The position of the definition in the source order of all definitions.</summary>
    public int SourceOrder { get; internal set; }

    internal int Line { get; }

    internal int Position { get; }
}

/// <summary>A node of the category tree.</summary>
public sealed class PolicyCategory
{
    private readonly List<PolicyCategory> children = [];
    private readonly List<PolicyDefinition> policies = [];

    internal PolicyCategory(
        TemplateFile file, string name, string displayName, string explainText, string? parentRef, int line, int position)
    {
        File = file;
        Name = name;
        DisplayName = displayName;
        ExplainText = explainText;
        ParentRef = parentRef;
        Line = line;
        Position = position;
    }

    /// <summary>The template file that defines it.</summary>
    public TemplateFile File { get; }

    /// <summary>The namespace of the defining file.</summary>
    public string Namespace => File.Namespace;

    /// <summary>The category name, as written.</summary>
    public string Name { get; }

    /// <summary>The resolved display name.</summary>
    public string DisplayName { get; }

    /// <summary>The resolved explain text, or an empty string.</summary>
    public string ExplainText { get; }

    /// <summary>The parent reference as written (for example prefix:name), or null for a root category.</summary>
    public string? ParentRef { get; }

    /// <summary>The parent category, or null for a root or a detached category.</summary>
    public PolicyCategory? Parent { get; internal set; }

    /// <summary>True when the category has no parent reference.</summary>
    public bool IsRoot => ParentRef is null;

    /// <summary>True when the category names a parent that could not be used (missing, or cut out of a cycle).</summary>
    public bool IsDetached => ParentRef is not null && Parent is null;

    /// <summary>The child categories in source order.</summary>
    public IReadOnlyList<PolicyCategory> Children => children;

    /// <summary>The policies directly in this category in source order. A class Both policy is listed once.</summary>
    public IReadOnlyList<PolicyDefinition> Policies => policies;

    /// <summary>The position of the category in the source order of all categories.</summary>
    public int SourceOrder { get; internal set; }

    internal int Line { get; }

    internal int Position { get; }

    internal void AddChild(PolicyCategory child) => children.Add(child);

    internal void AddPolicy(PolicyDefinition policy) => policies.Add(policy);
}

/// <summary>A policy of a template: its text, its registry facts, its elements and its presentation.</summary>
public sealed class PolicyDefinition
{
    internal PolicyDefinition(
        TemplateFile file, string name, PolicyClass policyClass, string key, string? categoryRef, string? supportedOnRef,
        int line, int position)
    {
        File = file;
        Name = name;
        Class = policyClass;
        Key = key;
        CategoryRef = categoryRef;
        SupportedOnRef = supportedOnRef;
        Line = line;
        Position = position;
    }

    /// <summary>The template file that defines it.</summary>
    public TemplateFile File { get; }

    /// <summary>The namespace of the defining file.</summary>
    public string Namespace => File.Namespace;

    /// <summary>The policy name, as written.</summary>
    public string Name { get; }

    /// <summary>The namespace and the name joined with a colon, the identity of the policy.</summary>
    public string FullName => Namespace + ":" + Name;

    /// <summary>Machine, User or Both.</summary>
    public PolicyClass Class { get; }

    /// <summary>True when the policy is shown under Computer Configuration.</summary>
    public bool AppliesToMachine => Class != PolicyClass.User;

    /// <summary>True when the policy is shown under User Configuration.</summary>
    public bool AppliesToUser => Class != PolicyClass.Machine;

    /// <summary>The resolved display name (the policy name when the string is missing).</summary>
    public string DisplayName { get; internal init; } = string.Empty;

    /// <summary>The resolved explain text, or an empty string.</summary>
    public string ExplainText { get; internal init; } = string.Empty;

    /// <summary>The registry key path.</summary>
    public string Key { get; }

    /// <summary>The registry value name, or null when the policy has none.</summary>
    public string? ValueName { get; internal init; }

    /// <summary>The value written when the policy is Enabled, or null.</summary>
    public PolicyValue? EnabledValue { get; internal init; }

    /// <summary>The value written when the policy is Disabled, or null.</summary>
    public PolicyValue? DisabledValue { get; internal init; }

    /// <summary>The values written when the policy is Enabled, or null.</summary>
    public PolicyValueList? EnabledList { get; internal init; }

    /// <summary>The values written when the policy is Disabled, or null.</summary>
    public PolicyValueList? DisabledList { get; internal init; }

    /// <summary>The elements in document order.</summary>
    public IReadOnlyList<PolicyElement> Elements { get; internal init; } = [];

    /// <summary>The clientExtension attribute, or null.</summary>
    public string? ClientExtension { get; internal init; }

    /// <summary>The presentation, with each control linked to its element, or null.</summary>
    public PolicyPresentation? Presentation { get; internal set; }

    /// <summary>The category reference as written, or null when the policy has none.</summary>
    public string? CategoryRef { get; }

    /// <summary>The category, or null when it is missing or undefined.</summary>
    public PolicyCategory? Category { get; internal set; }

    /// <summary>The supported-on reference as written, or null.</summary>
    public string? SupportedOnRef { get; }

    /// <summary>The supported-on definition, or null.</summary>
    public SupportedOnDefinition? SupportedOn { get; internal set; }

    /// <summary>The text of the supported-on definition, or an empty string.</summary>
    public string SupportedOnText => SupportedOn?.DisplayName ?? string.Empty;

    /// <summary>The position of the policy in the source order of all policies.</summary>
    public int SourceOrder { get; internal set; }

    internal string? PresentationId { get; init; }

    internal int Line { get; }

    internal int Position { get; }
}
