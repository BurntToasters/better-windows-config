using Bwc.Oracle.Capture.Gpedit.Driver;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// Every case of every group, built from the fixture and checked before anything runs.
internal sealed class MatrixPlan
{
    internal static readonly string[] GroupNames = { "A", "B", "C", "D", "E", "F", "G", "H", "K" };

    internal static readonly Dictionary<string, string> GroupTitles = new()
    {
        ["A"] = "first saves into an empty folder",
        ["B"] = "every fixture policy: defaults, alternate values, Disabled, Not Configured",
        ["C"] = "sequences: order, re-apply, comments, clearing, version, shared lists, cmtx",
        ["D"] = "crafted Registry.pol and gpt.ini start states plus one unrelated save",
        ["E"] = "validation message boxes",
        ["F"] = "visuals: tree, lists, menus, toolbar, dialog rectangles, keys",
        ["G"] = "refresh timing of the live registry",
        ["H"] = "template load errors",
        ["K"] = "key edge cases: policy keys with a leading or trailing backslash",
    };

    internal List<MatrixCase> Cases { get; } = new();

    internal FixtureModel Fixture { get; private init; } = new();

    internal List<string> Problems { get; } = new();

    internal static MatrixPlan Build(RepoPaths repo)
    {
        FixtureModel fixture = FixtureModel.Load(repo);
        var plan = new MatrixPlan { Fixture = fixture };
        var context = new MatrixContext(repo, fixture);
        plan.Cases.AddRange(MatrixGroups.GroupA(context));
        plan.Cases.AddRange(MatrixGroups.GroupB(context));
        plan.Cases.AddRange(MatrixGroups.GroupC(context));
        plan.Cases.AddRange(MatrixGroups.GroupD(context));
        plan.Cases.AddRange(MatrixGroups.GroupE(context));
        plan.Cases.AddRange(MatrixGroups.GroupF(context));
        plan.Cases.AddRange(MatrixGroups.GroupG(context));
        plan.Cases.AddRange(MatrixGroups.GroupH(context));
        plan.Cases.AddRange(MatrixGroups.GroupK(context));
        plan.Check();
        return plan;
    }

    private void Check()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MatrixCase c in Cases)
        {
            if (!seen.Add(c.FullId))
            {
                Problems.Add($"{c.FullId}: the id is used twice in the group (folder names compare without case)");
            }

            if (c.Ops.Count == 0)
            {
                Problems.Add($"{c.FullId}: no operations");
            }

            for (int i = 0; i < c.Ops.Count; i++)
            {
                foreach (string problem in c.Ops[i].Validate())
                {
                    Problems.Add($"{c.FullId} op[{i}] {c.Ops[i].Kind}: {problem}");
                }
            }

            for (int i = 0; i < c.Ops.Count; i++)
            {
                CheckIds(c, i);
            }

            foreach (RecordedStep step in c.Steps)
            {
                if (step.SnapshotOp < 0 || step.SnapshotOp >= c.Ops.Count || c.Ops[step.SnapshotOp].Kind != "snapshot")
                {
                    Problems.Add($"{c.FullId}: step '{step.Label}' does not point at a snapshot operation");
                }
            }

            if (c.Steps.Count == 0)
            {
                Problems.Add($"{c.FullId}: no recorded step, so no golden bytes");
            }

            foreach (int index in c.Optional.Where(i => i < 0 || i >= c.Ops.Count))
            {
                Problems.Add($"{c.FullId}: optional index {index} is out of range");
            }

            foreach ((string source, _) in c.ExtraTemplates.Where(t => !File.Exists(t.Source)))
            {
                Problems.Add($"{c.FullId}: template source {source} is missing");
            }

            if (c.Description.Contains('\u2014'))
            {
                Problems.Add($"{c.FullId}: the description holds an em dash");
            }
        }
    }

    // The engine tests replay goldens from case.json, so every editPolicy operation names the ADMX policy and the namespace of its template,
    // and every option names its element. Group H edits a stand-in for a template that does not load and has no ids.
    private void CheckIds(MatrixCase c, int index)
    {
        GpeditOp op = c.Ops[index];
        if (op.Kind != "editPolicy" || c.Group == "H")
        {
            return;
        }

        if (string.IsNullOrEmpty(op.PolicyId) || string.IsNullOrEmpty(op.PolicyNamespace))
        {
            Problems.Add($"{c.FullId} op[{index}] editPolicy '{op.Policy}': policyId and policyNamespace must both be set");
        }

        for (int i = 0; i < (op.Options?.Count ?? 0); i++)
        {
            if (string.IsNullOrEmpty(op.Options![i].ElementId))
            {
                Problems.Add($"{c.FullId} op[{index}] editPolicy '{op.Policy}': options[{i}] has no elementId");
            }
        }
    }

    internal IEnumerable<MatrixCase> InGroup(string group) => Cases.Where(c => c.Group == group);

    internal static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : $"{span.Minutes} min {span.Seconds:00} s";
    }
}
