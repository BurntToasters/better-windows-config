using Bwc.Oracle.Capture.Gpedit.Driver;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// What every group builder gets: the fixture as data and the repo paths.
internal sealed record MatrixContext(RepoPaths Repo, FixtureModel Fixture);

// Small constructors for driver operations, so the group builders read as the scenario they describe.
internal static class MatrixOps
{
    internal static readonly string[] Computer = { "Computer" };

    internal static GpeditOp Edit(
        PolicyInfo policy,
        string root,
        string? state = null,
        string? comment = null,
        List<OptionSpec>? options = null,
        string[]? press = null,
        string? label = null,
        string? answer = null,
        bool screenshot = false) => new()
        {
            Kind = "editPolicy",
            Root = root,
            Path = policy.NodePath,
            Policy = policy.DisplayName,
            PolicyId = policy.Namespace.Length > 0 ? policy.Name : null,
            PolicyNamespace = policy.Namespace.Length > 0 ? policy.Namespace : null,
            State = state,
            Comment = comment,
            Options = options,
            Press = press?.ToList(),
            Label = label,
            MessageBoxAnswer = answer,
            Screenshot = screenshot ? true : null,
        };

    internal static GpeditOp Select(string root, IEnumerable<string> path, string? label = null) =>
        new() { Kind = "selectNode", Root = root, Path = path.ToList(), Label = label };

    internal static GpeditOp Children(string root, IEnumerable<string> path, string? label = null) =>
        new() { Kind = "treeChildren", Root = root, Path = path.ToList(), Label = label };

    internal static GpeditOp ReadList(string root, IEnumerable<string> path, bool namesOnly = false, int? maxRows = null, string? label = null) =>
        new() { Kind = "readList", Root = root, Path = path.ToList(), NamesOnly = namesOnly ? true : null, MaxRows = maxRows, Label = label };

    internal static GpeditOp Status(string root, IEnumerable<string> path, string? label = null) =>
        new() { Kind = "readStatus", Root = root, Path = path.ToList(), Label = label };

    internal static GpeditOp Wait(int ms, string? label = null) => new() { Kind = "wait", WaitMs = ms, Label = label };

    internal static GpeditOp Plain(string kind, string? label = null) => new() { Kind = kind, Label = label };

    internal static GpeditOp Menu(string menu, string? root = null, IEnumerable<string>? path = null, string? policy = null, string? label = null) =>
        new() { Kind = "dumpMenu", Menu = menu, Root = root, Path = path?.ToList(), Policy = policy, Label = label };

    internal static GpeditOp Shot(string name) => new() { Kind = "screenshot", Name = name };

    // A text option found by its label; the element id comes from the policy's element with that label.
    internal static OptionSpec Text(PolicyInfo policy, string label, string text, string kind, bool typed = false) =>
        new()
        {
            Label = label,
            Text = text,
            ExpectKind = kind,
            Typed = typed ? true : null,
            ElementId = policy.Elements.SingleOrDefault(e => string.Equals(e.Label, label, StringComparison.Ordinal))?.Id
                ?? throw new InvalidOperationException($"{policy.Name} has no single element labelled '{label}'"),
        };
}
