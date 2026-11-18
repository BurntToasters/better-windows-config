namespace Bwc.Policy.Admx;

/// <summary>
/// Links parsed files into one set: namespaces, using prefixes, cross-file category parents, supported-on references and
/// the category tree. It runs on one thread after every file is parsed, so its output order is deterministic.
/// </summary>
internal static class TemplateLinker
{
    internal static TemplateSet Link(string culture, IReadOnlyList<ParsedAdmx> parsed, FailureSink sink)
    {
        var files = new List<TemplateFile>();
        var accepted = new List<ParsedAdmx>();
        var byNamespace = new Dictionary<string, TemplateFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsed)
        {
            if (byNamespace.TryGetValue(item.File.Namespace, out var first))
            {
                var code = first.SourceIndex == item.File.SourceIndex
                    ? TemplateFailureCode.DuplicateNamespace
                    : TemplateFailureCode.NamespaceShadowed;
                sink.Add(code, item.File.AdmxPath, item.File.Namespace);
                continue;
            }

            item.File.Order = files.Count;
            byNamespace.Add(item.File.Namespace, item.File);
            files.Add(item.File);
            accepted.Add(item);
        }

        var supportedOn = new List<SupportedOnDefinition>();
        var categories = new List<PolicyCategory>();
        var policies = new List<PolicyDefinition>();
        var supportedOnByName = new Dictionary<string, SupportedOnDefinition>(StringComparer.OrdinalIgnoreCase);
        var categoriesByName = new Dictionary<string, PolicyCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in accepted)
        {
            foreach (var definition in item.SupportedOn)
            {
                definition.SourceOrder = supportedOn.Count;
                supportedOn.Add(definition);
                supportedOnByName[TemplateSet.Key(definition.Namespace, definition.Name)] = definition;
            }

            foreach (var category in item.Categories)
            {
                category.SourceOrder = categories.Count;
                categories.Add(category);
                categoriesByName[TemplateSet.Key(category.Namespace, category.Name)] = category;
            }

            foreach (var policy in item.Policies)
            {
                policy.SourceOrder = policies.Count;
                policies.Add(policy);
            }
        }

        var prefixes = BuildPrefixMaps(files, byNamespace, sink);
        LinkCategoryParents(categories, categoriesByName, prefixes, sink);
        CutCycles(categories, sink);
        foreach (var category in categories)
        {
            category.Parent?.AddChild(category);
        }

        foreach (var policy in policies)
        {
            LinkPolicy(policy, categoriesByName, supportedOnByName, prefixes, sink);
        }

        return new TemplateSet(culture, files, supportedOn, categories, policies, sink.Items);
    }

    private static Dictionary<string, string>[] BuildPrefixMaps(
        List<TemplateFile> files, Dictionary<string, TemplateFile> byNamespace, FailureSink sink)
    {
        var maps = new Dictionary<string, string>[files.Count];
        foreach (var file in files)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [file.Prefix] = file.Namespace };
            foreach (var use in file.Usings)
            {
                map.TryAdd(use.Prefix, use.Namespace);
                if (!byNamespace.ContainsKey(use.Namespace))
                {
                    sink.Add(TemplateFailureCode.UndefinedUsing, file.AdmxPath, use.Namespace);
                }
            }

            maps[file.Order] = map;
        }

        return maps;
    }

    private static bool TryResolve(
        TemplateFile file, Dictionary<string, string>[] prefixes, string reference, out string ns, out string name, out string prefix)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            ns = file.Namespace;
            name = reference;
            prefix = string.Empty;
            return true;
        }

        prefix = reference[..colon];
        name = reference[(colon + 1)..];
        if (prefixes[file.Order].TryGetValue(prefix, out var resolved))
        {
            ns = resolved;
            return true;
        }

        ns = string.Empty;
        return false;
    }

    private static T? Resolve<T>(
        TemplateFile file,
        Dictionary<string, string>[] prefixes,
        Dictionary<string, T> index,
        string reference,
        TemplateFailureCode missingCode,
        int line,
        int position,
        FailureSink sink)
        where T : class
    {
        if (!TryResolve(file, prefixes, reference, out var ns, out var name, out var prefix))
        {
            sink.Add(TemplateFailureCode.UndefinedPrefix, file.AdmxPath, prefix, line, position);
            return null;
        }

        if (index.TryGetValue(TemplateSet.Key(ns, name), out var found))
        {
            return found;
        }

        sink.Add(missingCode, file.AdmxPath, reference, line, position);
        return null;
    }

    private static void LinkCategoryParents(
        List<PolicyCategory> categories,
        Dictionary<string, PolicyCategory> index,
        Dictionary<string, string>[] prefixes,
        FailureSink sink)
    {
        foreach (var category in categories)
        {
            if (category.ParentRef is not null)
            {
                category.Parent = Resolve(
                    category.File, prefixes, index, category.ParentRef, TemplateFailureCode.UndefinedCategory,
                    category.Line, category.Position, sink);
            }
        }
    }

    private static void CutCycles(List<PolicyCategory> categories, FailureSink sink)
    {
        var state = new byte[categories.Count];
        var path = new List<PolicyCategory>();
        foreach (var start in categories)
        {
            if (state[start.SourceOrder] != 0)
            {
                continue;
            }

            path.Clear();
            var current = start;
            while (current is not null && state[current.SourceOrder] == 0)
            {
                state[current.SourceOrder] = 1;
                path.Add(current);
                current = current.Parent;
            }

            if (current is not null && state[current.SourceOrder] == 1)
            {
                var cut = path[path.IndexOf(current)..].MinBy(c => c.SourceOrder)!;
                sink.Add(TemplateFailureCode.CategoryCycle, cut.File.AdmxPath, cut.Name, cut.Line, cut.Position);
                cut.Parent = null;
            }

            foreach (var member in path)
            {
                state[member.SourceOrder] = 2;
            }
        }
    }

    private static void LinkPolicy(
        PolicyDefinition policy,
        Dictionary<string, PolicyCategory> categories,
        Dictionary<string, SupportedOnDefinition> supportedOn,
        Dictionary<string, string>[] prefixes,
        FailureSink sink)
    {
        if (policy.CategoryRef is null)
        {
            sink.Add(TemplateFailureCode.MissingCategory, policy.File.AdmxPath, policy.Name, policy.Line, policy.Position);
        }
        else
        {
            var category = Resolve(
                policy.File, prefixes, categories, policy.CategoryRef, TemplateFailureCode.UndefinedCategory,
                policy.Line, policy.Position, sink);
            if (category is not null)
            {
                policy.Category = category;
                category.AddPolicy(policy);
            }
        }

        if (policy.SupportedOnRef is not null)
        {
            policy.SupportedOn = Resolve(
                policy.File, prefixes, supportedOn, policy.SupportedOnRef, TemplateFailureCode.UndefinedSupportedOn,
                policy.Line, policy.Position, sink);
        }
    }
}
