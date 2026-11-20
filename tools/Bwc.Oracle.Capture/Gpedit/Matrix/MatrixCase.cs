using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// A file the case places into the reset GroupPolicy folder before classic starts. Name is relative to the folder, as PolFiles.Names writes it.
internal sealed record StartFile(string Name, byte[] Bytes);

internal sealed record StartState(string Description, IReadOnlyList<StartFile> Files)
{
    internal static StartState Empty { get; } = new("empty folder", Array.Empty<StartFile>());
}

// One recorded point inside a case: the snapshot operation that follows the step's operation.
internal sealed record RecordedStep(string Label, int SnapshotOp);

// One matrix case. Ids are unique per group and safe as folder names.
internal sealed class MatrixCase
{
    // Version 2: the driver types multi-line text (the comment and multiText options) with key presses, because WM_SETTEXT was never noticed by
    // the dialog and the comment-only goldens of version 1 wrote nothing; case.json holds each operation as data (`op`); both configuration
    // roots are expanded before the first operation. A golden of version 1 is therefore not trusted by `--resume`: every case is captured again.
    internal const int PlanVersion = 2;

    // How an operation is written as data, in case.json and for the plan hash: camel case, nulls left out.
    internal static readonly JsonSerializerOptions OpJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal string Group { get; init; } = string.Empty;

    internal string Id { get; init; } = string.Empty;

    internal string Description { get; init; } = string.Empty;

    internal StartState Start { get; init; } = StartState.Empty;

    internal List<GpeditOp> Ops { get; init; } = new();

    internal List<RecordedStep> Steps { get; init; } = new();

    // Operations whose failure is an observation (a node that may not exist), not a failed case.
    internal HashSet<int> Optional { get; init; } = new();

    // Extra template files installed for this case only (group H). Relative names as TemplateSet wants them.
    internal List<(string Source, string Relative)> ExtraTemplates { get; init; } = new();

    // Strings this case types or names, which the scrubber keeps when classic echoes them.
    internal HashSet<string> Authored { get; init; } = new(StringComparer.Ordinal);

    // Registry content is part of the observed state (group G).
    internal bool KeepRegistry { get; init; }

    // The live registry under the BWC-Oracle keys must be empty at the start (always true after the reset; recorded for G).
    internal string FullId => Group + "/" + Id;

    internal string DirName => Id;

    // Rough seconds the case takes, used only for --list.
    internal double EstimatedSeconds
    {
        get
        {
            double total = 25 + 8;
            foreach (GpeditOp op in Ops)
            {
                total += op.Kind switch
                {
                    "editPolicy" => (op.State is not null || op.Comment is not null || op.Options is { Count: > 0 }) && op.EffectivePress().Any(p => p is "OK" or "Apply") ? 14 : 6,
                    "snapshot" => 0.5,
                    "wait" => (op.WaitMs ?? 0) / 1000.0,
                    "selectNode" or "readList" or "readStatus" or "treeChildren" => 3,
                    "dumpMenu" => 3,
                    "setFilter" or "filterOn" or "clearFilter" => 6,
                    "sortColumn" => 4,
                    "pressKey" => 3 + (op.Keys?.Count ?? 1) * 1.2,
                    "dialogLayout" => 8,
                    "readLayout" => 6,
                    _ => 2,
                };
            }

            return total;
        }
    }

    // A hash of everything that decides what the case does: the operations, the start files, the templates and the plan version. A change
    // makes `--resume` redo a case that was captured with another plan.
    internal string PlanHash()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => sha.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
        Add("v" + PlanVersion);
        Add(FullId);
        Add(Start.Description);
        foreach (StartFile file in Start.Files)
        {
            Add(file.Name);
            sha.AppendData(file.Bytes);
        }

        foreach (GpeditOp op in Ops)
        {
            Add(JsonSerializer.Serialize(op, OpJson));
        }

        foreach ((string source, string relative) in ExtraTemplates)
        {
            Add(relative);
            sha.AppendData(File.Exists(source) ? File.ReadAllBytes(source) : Array.Empty<byte>());
        }

        foreach (int index in Optional.OrderBy(i => i))
        {
            Add("optional " + index);
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    internal TemplateSet Templates(RepoPaths repo)
    {
        TemplateSet good = FixtureTemplates.Load(repo);
        if (ExtraTemplates.Count == 0)
        {
            return good;
        }

        return new TemplateSet(good.Items.Concat(ExtraTemplates.Select(t => new TemplateSet.Item(t.Source, t.Relative))).ToList());
    }

    internal static bool SafeId(string id) => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.StartsWith('.');
}

// Builds a case's operation list. Step() appends a snapshot operation and records it, so the golden holds the files after that point.
internal sealed class CaseBuilder
{
    private readonly string group;
    private readonly string id;
    private readonly string description;
    private readonly List<GpeditOp> ops = new();
    private readonly List<RecordedStep> steps = new();
    private readonly HashSet<int> optional = new();
    private readonly List<(string, string)> templates = new();
    private readonly HashSet<string> authored = new(StringComparer.Ordinal);
    private StartState start = StartState.Empty;
    private bool keepRegistry;

    internal CaseBuilder(string group, string id, string description)
    {
        this.group = group;
        this.id = id;
        this.description = description;
    }

    internal CaseBuilder StartWith(StartState state)
    {
        start = state;
        return this;
    }

    internal CaseBuilder RegistryObserved()
    {
        keepRegistry = true;
        return this;
    }

    internal CaseBuilder Allow(IEnumerable<string> strings)
    {
        foreach (string text in strings)
        {
            authored.Add(text);
        }

        return this;
    }

    internal CaseBuilder Template(string source, string relative)
    {
        templates.Add((source, relative));
        return this;
    }

    internal CaseBuilder Add(GpeditOp op, bool isOptional = false)
    {
        if (isOptional)
        {
            optional.Add(ops.Count);
        }

        ops.Add(op);
        Author(op);
        return this;
    }

    // The operation, then a snapshot of the five files recorded as a step.
    internal CaseBuilder Step(string label, GpeditOp op, bool isOptional = false)
    {
        Add(op, isOptional);
        ops.Add(new GpeditOp { Kind = "snapshot", Label = "step " + label });
        steps.Add(new RecordedStep(label, ops.Count - 1));
        return this;
    }

    // A snapshot of the files as they are now, recorded as a step (the start echo or a point after a read only operation).
    internal CaseBuilder Mark(string label)
    {
        ops.Add(new GpeditOp { Kind = "snapshot", Label = "step " + label });
        steps.Add(new RecordedStep(label, ops.Count - 1));
        return this;
    }

    internal MatrixCase Build()
    {
        if (!MatrixCase.SafeId(id))
        {
            throw new InvalidOperationException($"case id '{id}' of group {group} is not a safe folder name");
        }

        if (steps.Count == 0)
        {
            // A read only case still ends with the files as they are, which proves nothing was written.
            Mark("final");
        }

        return new MatrixCase
        {
            Group = group,
            Id = id,
            Description = description,
            Start = start,
            Ops = ops,
            Steps = steps,
            Optional = optional,
            ExtraTemplates = templates,
            Authored = authored,
            KeepRegistry = keepRegistry,
        };
    }

    private void Author(GpeditOp op)
    {
        void Add(string? text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                authored.Add(text);
            }
        }

        Add(op.Policy);
        Add(op.Comment);
        Add(op.Label);
        Add(op.Column);
        Add(op.Tab);
        Add(op.Name);
        foreach (string segment in op.Path ?? new List<string>())
        {
            Add(segment);
        }

        foreach (OptionSpec option in op.Options ?? new List<OptionSpec>())
        {
            Add(option.Label);
            Add(option.Text);
            Add(option.Item);
            foreach (string line in option.Lines ?? new List<string>())
            {
                Add(line);
            }

            foreach (List<string> row in option.Rows ?? new List<List<string>>())
            {
                foreach (string cell in row)
                {
                    Add(cell);
                }
            }
        }

        if (op.Filter is { } filter)
        {
            Add(filter.Keywords);
            Add(filter.Managed);
            Add(filter.Configured);
            Add(filter.Commented);
            Add(filter.Match);
        }
    }
}
