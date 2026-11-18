using System.Text;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests.Blind;

/// <summary>
/// Helpers for the blind tests. They write well formed ADMX and ADML text with the real namespace and read only public
/// members of the template model.
/// </summary>
internal static class BlindKit
{
    internal const string Xmlns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";
    internal const string Key = @"Software\Policies\BlindT";

    internal static string AdmxText(string ns, string prefix, string body, string usings = "") =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        $"<policyDefinitions revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"{Xmlns}\">\n" +
        "  <policyNamespaces>\n" +
        $"    <target prefix=\"{prefix}\" namespace=\"{ns}\" />\n" +
        usings +
        "  </policyNamespaces>\n" +
        "  <resources minRequiredRevision=\"1.0\" />\n" +
        body + "\n" +
        "</policyDefinitions>\n";

    internal static string AdmlText(string strings, string presentations = "") =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        $"<policyDefinitionResources revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"{Xmlns}\">\n" +
        "  <displayName>Blind</displayName>\n" +
        "  <description>Blind test file</description>\n" +
        "  <resources>\n" +
        $"    <stringTable>{strings}</stringTable>\n" +
        $"    <presentationTable>{presentations}</presentationTable>\n" +
        "  </resources>\n" +
        "</policyDefinitionResources>\n";

    /// <summary>One string table entry per id; the text is the id unless a pair is given.</summary>
    internal static string Strings(params string[] ids) =>
        string.Concat(ids.Select(id => $"<string id=\"{id}\">{id} text</string>"));

    internal static string Using(string prefix, string ns) => $"    <using prefix=\"{prefix}\" namespace=\"{ns}\" />\n";

    /// <summary>
    /// A policy. A null argument drops the attribute (or the element). The display name refers to the string with the
    /// policy name.
    /// </summary>
    internal static string P(string name, string inner = "", string? cls = "Machine", string? key = Key,
        string? valueName = "V", string? cat = "Root", string? sup = "SUP", string? attrs = null, string? presentation = null)
    {
        var sb = new StringBuilder("<policy");
        if (name is not null)
        {
            sb.Append($" name=\"{name}\"");
        }

        if (cls is not null)
        {
            sb.Append($" class=\"{cls}\"");
        }

        sb.Append($" displayName=\"$(string.{name})\"");
        if (key is not null)
        {
            sb.Append($" key=\"{key}\"");
        }

        if (valueName is not null)
        {
            sb.Append($" valueName=\"{valueName}\"");
        }

        if (presentation is not null)
        {
            sb.Append($" presentation=\"{presentation}\"");
        }

        if (attrs is not null)
        {
            sb.Append(' ').Append(attrs);
        }

        sb.Append('>');
        if (cat is not null)
        {
            sb.Append($"<parentCategory ref=\"{cat}\" />");
        }

        if (sup is not null)
        {
            sb.Append($"<supportedOn ref=\"{sup}\" />");
        }

        sb.Append(inner).Append("</policy>");
        return sb.ToString();
    }

    /// <summary>
    /// Writes a pair (name.admx and culture\name.adml) with a root category "Root" and a definition "SUP". The string
    /// table defines Root, SUP and every id in <paramref name="ids"/>.
    /// </summary>
    internal static void Std(TempTemplates t, string file, string ns, string policies, string[]? ids = null,
        string categories = "", string presentations = "", string usings = "", string extraStrings = "",
        string culture = "en-US", string prefix = "t")
    {
        var body =
            "<supportedOn><definitions><definition name=\"SUP\" displayName=\"$(string.SUP)\" /></definitions></supportedOn>\n" +
            $"<categories><category name=\"Root\" displayName=\"$(string.Root)\" />{categories}</categories>\n" +
            $"<policies>{policies}</policies>";
        t.Write(file + ".admx", AdmxText(ns, prefix, body, usings));
        var strings = Strings(["Root", "SUP", .. (ids ?? [])]) + extraStrings;
        t.Write(Path.Combine(culture, file + ".adml"), AdmlText(strings, presentations));
    }

    internal static List<TemplateFailure> Of(TemplateSet set, TemplateFailureCode code) =>
        set.Failures.Where(f => f.Code == code).ToList();

    internal static TemplateFailure One(TemplateSet set, TemplateFailureCode code)
    {
        var all = Of(set, code);
        Assert.AreEqual(1, all.Count, $"Expected exactly one {code} in: {FailureAssert.Describe(set)}");
        return all[0];
    }

    /// <summary>One-based line and column of the closing angle bracket of the start tag at <paramref name="marker"/>.</summary>
    internal static (int Line, int Column) ClosePosition(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "marker not found: " + marker);
        var gt = text.IndexOf('>', start);
        var line = 1 + text.Take(gt).Count(c => c == '\n');
        var lastNewline = text.LastIndexOf('\n', gt);
        return (line, gt - lastNewline);
    }

    internal static string Raw(string path) => File.ReadAllText(path);
}
