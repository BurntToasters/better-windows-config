namespace Bwc.Policy.Admx;

/// <summary>
/// The immutable, linked result of loading template folders: files, categories, policies, supported-on definitions
/// and the failures. Every list is in source order (folders as given, files by name, then document order). Nothing is
/// sorted for display; the UI decides display order.
/// </summary>
public sealed class TemplateSet
{
    private readonly Dictionary<string, TemplateFile> filesByNamespace;
    private readonly Dictionary<string, PolicyCategory> categoriesByName;
    private readonly Dictionary<string, PolicyDefinition> policiesByName;
    private readonly Dictionary<string, SupportedOnDefinition> supportedOnByName;

    internal TemplateSet(
        string culture,
        IReadOnlyList<TemplateFile> files,
        IReadOnlyList<SupportedOnDefinition> supportedOn,
        IReadOnlyList<PolicyCategory> categories,
        IReadOnlyList<PolicyDefinition> policies,
        IReadOnlyList<TemplateFailure> failures)
    {
        Culture = culture;
        Files = files;
        SupportedOn = supportedOn;
        Categories = categories;
        Policies = policies;
        Failures = failures;
        RootCategories = categories.Where(c => c.IsRoot).ToArray();
        filesByNamespace = Index(files, f => f.Namespace);
        categoriesByName = Index(categories, c => Key(c.Namespace, c.Name));
        policiesByName = Index(policies, p => Key(p.Namespace, p.Name));
        supportedOnByName = Index(supportedOn, s => Key(s.Namespace, s.Name));
    }

    /// <summary>An empty set with no failures.</summary>
    public static TemplateSet Empty { get; } = new("en-US", [], [], [], [], []);

    /// <summary>The UI culture the load asked for.</summary>
    public string Culture { get; }

    /// <summary>The loaded template files.</summary>
    public IReadOnlyList<TemplateFile> Files { get; }

    /// <summary>All supported-on definitions.</summary>
    public IReadOnlyList<SupportedOnDefinition> SupportedOn { get; }

    /// <summary>All categories, including detached ones.</summary>
    public IReadOnlyList<PolicyCategory> Categories { get; }

    /// <summary>The categories with no parent reference.</summary>
    public IReadOnlyList<PolicyCategory> RootCategories { get; }

    /// <summary>All policies, including those with no category.</summary>
    public IReadOnlyList<PolicyDefinition> Policies { get; }

    /// <summary>Everything that could not be loaded, in the order it was found.</summary>
    public IReadOnlyList<TemplateFailure> Failures { get; }

    /// <summary>Finds a loaded file by its namespace (ignoring case).</summary>
    public TemplateFile? FindFile(string ns) => filesByNamespace.GetValueOrDefault(ns);

    /// <summary>Finds a category by namespace and name (ignoring case).</summary>
    public PolicyCategory? FindCategory(string ns, string name) => categoriesByName.GetValueOrDefault(Key(ns, name));

    /// <summary>Finds a policy by namespace and name (ignoring case).</summary>
    public PolicyDefinition? FindPolicy(string ns, string name) => policiesByName.GetValueOrDefault(Key(ns, name));

    /// <summary>Finds a supported-on definition by namespace and name (ignoring case).</summary>
    public SupportedOnDefinition? FindSupportedOn(string ns, string name) => supportedOnByName.GetValueOrDefault(Key(ns, name));

    internal static string Key(string ns, string name) => ns + "\u0001" + name;

    private static Dictionary<string, T> Index<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            map.TryAdd(key(item), item);
        }

        return map;
    }
}
