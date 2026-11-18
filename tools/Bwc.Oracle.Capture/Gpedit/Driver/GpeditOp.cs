using System.Text.Json;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// One value to put into an option control of the setting dialog. The control is found by its visible label (or by Index among the
// controls, labels not counted) and filled according to the kind classic shows. Exactly one value field is given, except Rows with
// ClearRows.
internal sealed record OptionSpec
{
    public string? Label { get; init; }

    public int? Index { get; init; }

    public string? ExpectKind { get; init; }

    public string? Text { get; init; }

    public bool? Checked { get; init; }

    public string? Item { get; init; }

    public int? ItemIndex { get; init; }

    public List<string>? Lines { get; init; }

    public List<List<string>>? Rows { get; init; }

    public bool? ClearRows { get; init; }

    public bool? Typed { get; init; }

    internal string Describe() => (Label ?? $"#{Index}") + " = " + (Text is not null ? (Typed is true ? "typed " : string.Empty) + $"text '{Text}'"
        : Checked is not null ? $"checked {Checked}"
        : Item is not null ? $"item '{Item}'"
        : ItemIndex is not null ? $"item #{ItemIndex}"
        : Lines is not null ? $"{Lines.Count} lines"
        : Rows is not null ? $"{Rows.Count} rows"
        : "clear rows");
}

// View > Filter Options values. A null field is left as it is. Combos take the text of an item as classic shows it.
internal sealed record FilterSpec
{
    public string? Managed { get; init; }

    public string? Configured { get; init; }

    public string? Commented { get; init; }

    public bool? KeywordsEnabled { get; init; }

    public string? Keywords { get; init; }

    public string? Match { get; init; }

    public bool? InTitle { get; init; }

    public bool? InHelp { get; init; }

    public bool? InComment { get; init; }

    public bool? RequirementsEnabled { get; init; }

    public string? Platforms { get; init; }
}

// One driven operation: a kind and its parameters. The runner executes a list of these; the matrix runner builds them.
internal sealed record GpeditOp
{
    public string Kind { get; init; } = string.Empty;

    public string? Label { get; init; }

    public string? Root { get; init; }

    public List<string>? Path { get; init; }

    public string? Policy { get; init; }

    public int? Occurrence { get; init; }

    public string? State { get; init; }

    public string? Comment { get; init; }

    public List<OptionSpec>? Options { get; init; }

    public List<string>? Press { get; init; }

    public string? MessageBoxAnswer { get; init; }

    public bool? ReadLists { get; init; }

    public string? Menu { get; init; }

    public string? Tab { get; init; }

    public string? Column { get; init; }

    public FilterSpec? Filter { get; init; }

    public bool? FilterOn { get; init; }

    public int? MaxRows { get; init; }

    public bool? NamesOnly { get; init; }

    public bool? Screenshot { get; init; }

    public string? Name { get; init; }

    public int? WaitMs { get; init; }

    // pressKey: key names from GpeditDriver.KeySpecs; Name is the focus target ("tree" or "list").
    public List<string>? Keys { get; init; }

    internal static readonly string[] States = { "Not Configured", "Enabled", "Disabled" };
    internal static readonly string[] Buttons = { "Apply", "OK", "Cancel", "Previous", "Next" };
    internal static readonly string[] Menus = { "File", "Action", "View", "Help", "Context" };
    internal static readonly string[] Tabs = { "Extended", "Standard" };
    internal static readonly string[] Roots = { "Computer", "User" };
    internal static readonly string[] OptionKinds =
    {
        "textBox", "decimalTextBox", "longDecimalTextBox", "spinner", "checkBox", "dropdownList", "comboBox", "multiText", "list",
    };

    private static readonly string[] CommonFields = { "label", "screenshot" };
    private static readonly string[] NodeFields = { "root", "path" };

    private static readonly Dictionary<string, (string[] Required, string[] Allowed)> Rules = new(StringComparer.Ordinal)
    {
        ["selectNode"] = (new[] { "root", "path" }, Array.Empty<string>()),
        ["treeChildren"] = (new[] { "root", "path" }, Array.Empty<string>()),
        ["readList"] = (Array.Empty<string>(), new[] { "root", "path", "maxRows", "namesOnly" }),
        ["readStatus"] = (Array.Empty<string>(), NodeFields),
        ["selectTab"] = (new[] { "tab" }, Array.Empty<string>()),
        ["editPolicy"] = (
            new[] { "policy" },
            new[] { "root", "path", "occurrence", "state", "comment", "options", "press", "messageBoxAnswer", "readLists" }),
        ["dumpMenu"] = (new[] { "menu" }, new[] { "root", "path", "policy", "occurrence" }),
        ["setFilter"] = (new[] { "filter" }, new[] { "root", "path", "press", "messageBoxAnswer" }),
        ["filterOn"] = (new[] { "filterOn" }, NodeFields),
        ["clearFilter"] = (Array.Empty<string>(), NodeFields),
        ["sortColumn"] = (new[] { "column" }, NodeFields),
        ["snapshot"] = (Array.Empty<string>(), Array.Empty<string>()),
        ["screenshot"] = (Array.Empty<string>(), new[] { "name" }),
        ["wait"] = (new[] { "waitMs" }, Array.Empty<string>()),
        ["dismissDialogs"] = (Array.Empty<string>(), Array.Empty<string>()),
        ["readLayout"] = (Array.Empty<string>(), NodeFields),
        ["dialogLayout"] = (new[] { "policy" }, new[] { "root", "path", "occurrence", "state" }),
        ["pressKey"] = (new[] { "keys" }, new[] { "root", "path", "policy", "occurrence", "name" }),
    };

    internal static IReadOnlyCollection<string> KindNames => Rules.Keys;

    // Kinds that can make classic write the GPO files, so the runner waits for the files to settle after them.
    internal bool CanWrite => Kind == "editPolicy";

    internal string Describe()
    {
        string where = Root is not null && Path is not null ? $" at {Root}/{string.Join("/", Path)}" : string.Empty;
        string detail = Kind switch
        {
            "editPolicy" => $" '{Policy}'" + (State is not null ? $" state {State}" : string.Empty)
                + (Comment is not null ? " comment" : string.Empty)
                + (Options is { Count: > 0 } ? $" options [{string.Join("; ", Options.Select(o => o.Describe()))}]" : string.Empty)
                + $" press [{string.Join(", ", EffectivePress())}]",
            "dumpMenu" => $" {Menu}" + (Policy is not null ? $" on '{Policy}'" : string.Empty),
            "selectTab" => " " + Tab,
            "sortColumn" => " " + Column,
            "filterOn" => " " + FilterOn,
            "wait" => $" {WaitMs} ms",
            "dialogLayout" => $" '{Policy}'" + (State is not null ? $" state {State}" : string.Empty),
            "pressKey" => $" [{string.Join(", ", Keys ?? new List<string>())}] in {Name ?? "tree"}" + (Policy is not null ? $" on '{Policy}'" : string.Empty),
            _ => string.Empty,
        };
        return Kind + detail + where + (Label is not null ? $"  [{Label}]" : string.Empty);
    }

    // Default presses: OK when something is set, Cancel when the dialog is only read.
    internal List<string> EffectivePress() =>
        Press is { Count: > 0 } ? Press : new List<string> { State is not null || Comment is not null || Options is { Count: > 0 } ? "OK" : "Cancel" };

    // Every problem with this operation, as text. An empty list means it can run.
    internal List<string> Validate()
    {
        var problems = new List<string>();
        if (!Rules.TryGetValue(Kind, out (string[] Required, string[] Allowed) rule))
        {
            problems.Add($"unknown kind '{Kind}' (known: {string.Join(", ", Rules.Keys)})");
            return problems;
        }

        HashSet<string> set = SetFields();
        foreach (string required in rule.Required)
        {
            if (!set.Contains(required))
            {
                problems.Add($"missing '{required}'");
            }
        }

        foreach (string field in set)
        {
            if (!rule.Required.Contains(field) && !rule.Allowed.Contains(field) && !CommonFields.Contains(field) && field != "kind")
            {
                problems.Add($"'{field}' is not used by {Kind}");
            }
        }

        if (set.Contains("root") != set.Contains("path"))
        {
            problems.Add("'root' and 'path' go together");
        }

        if (Root is not null && !Roots.Contains(Root, StringComparer.Ordinal))
        {
            problems.Add($"root '{Root}' must be one of {string.Join(", ", Roots)}");
        }

        if (Path is not null && Path.Any(string.IsNullOrEmpty))
        {
            problems.Add("path has an empty segment");
        }

        if (State is not null && !States.Contains(State, StringComparer.Ordinal))
        {
            problems.Add($"state '{State}' must be one of {string.Join(", ", States)}");
        }

        if (Menu is not null && !Menus.Contains(Menu, StringComparer.Ordinal))
        {
            problems.Add($"menu '{Menu}' must be one of {string.Join(", ", Menus)}");
        }

        if (Tab is not null && !Tabs.Contains(Tab, StringComparer.Ordinal))
        {
            problems.Add($"tab '{Tab}' must be one of {string.Join(", ", Tabs)}");
        }

        if (Menu is not null && Menu != "Context" && (set.Contains("root") || set.Contains("policy")))
        {
            problems.Add("'root', 'path' and 'policy' on dumpMenu are for the Context menu only");
        }

        if (Kind == "pressKey")
        {
            foreach (string key in Keys ?? new List<string>())
            {
                if (!GpeditDriver.KeySpecs.ContainsKey(key))
                {
                    problems.Add($"key '{key}' is not allowed (allowed: {string.Join(", ", GpeditDriver.KeySpecs.Keys)})");
                }
            }

            if (Keys is { Count: 0 })
            {
                problems.Add("keys is empty");
            }

            if (Name is not null && Name is not ("tree" or "list"))
            {
                problems.Add("name (the focus target) must be tree or list");
            }
        }

        if (Occurrence is < 0)
        {
            problems.Add("occurrence must be zero or more");
        }

        if (MaxRows is <= 0)
        {
            problems.Add("maxRows must be positive");
        }

        if (WaitMs is < 0 or > 600000)
        {
            problems.Add("waitMs must be 0 to 600000");
        }

        ValidatePress(problems);
        if (Options is not null)
        {
            for (int i = 0; i < Options.Count; i++)
            {
                ValidateOption(Options[i], i, problems);
            }
        }

        if (Kind == "editPolicy" && Options is { Count: > 0 } && State is not null && State != "Enabled")
        {
            problems.Add("options can only be set when State is Enabled");
        }

        if (Kind == "setFilter" && Filter is not null)
        {
            ValidateFilter(Filter, problems);
        }

        return problems;
    }

    private void ValidatePress(List<string> problems)
    {
        if (Press is null)
        {
            return;
        }

        if (Press.Count == 0)
        {
            problems.Add("press is empty (leave it out for the default)");
        }

        foreach (string button in Press)
        {
            bool filterButton = Kind == "setFilter" && (button == "OK" || button == "Cancel");
            if (!Buttons.Contains(button, StringComparer.Ordinal) || (Kind == "setFilter" && !filterButton))
            {
                problems.Add($"press '{button}' is not allowed here ({(Kind == "setFilter" ? "OK, Cancel" : string.Join(", ", Buttons))})");
            }
        }

        if (Kind == "setFilter" && Press.Count != 1)
        {
            problems.Add("setFilter takes one press");
        }

        for (int i = 0; i < Press.Count - 1; i++)
        {
            if (Press[i] is "OK" or "Cancel")
            {
                problems.Add($"press '{Press[i]}' closes the dialog, so it must be the last press");
            }
        }
    }

    private static void ValidateOption(OptionSpec option, int index, List<string> problems)
    {
        string at = $"options[{index}]";
        if (option.Label is null && option.Index is null)
        {
            problems.Add($"{at} needs 'label' or 'index'");
        }

        if (option.ExpectKind is not null && !OptionKinds.Contains(option.ExpectKind, StringComparer.Ordinal))
        {
            problems.Add($"{at} expectKind '{option.ExpectKind}' must be one of {string.Join(", ", OptionKinds)}");
        }

        int values = new[] { option.Text is not null, option.Checked is not null, option.Item is not null, option.ItemIndex is not null, option.Lines is not null }.Count(b => b)
            + (option.Rows is not null || option.ClearRows is true ? 1 : 0);
        if (values != 1)
        {
            problems.Add($"{at} needs exactly one of text, checked, item, itemIndex, lines, rows/clearRows (found {values})");
        }

        if (option.Typed is true && option.Text is null && option.Lines is null)
        {
            problems.Add($"{at} typed needs text or lines");
        }

        if (option.ItemIndex is < 0)
        {
            problems.Add($"{at} itemIndex must be zero or more");
        }

        if (option.Rows is not null && option.Rows.Any(r => r.Count is < 1 or > 2))
        {
            problems.Add($"{at} rows must have one or two cells each");
        }
    }

    private static void ValidateFilter(FilterSpec filter, List<string> problems)
    {
        if (filter.Platforms is not null && filter.Platforms is not ("all" or "none"))
        {
            problems.Add("filter.platforms must be 'all' or 'none'");
        }
    }

    private HashSet<string> SetFields()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, bool present)
        {
            if (present)
            {
                set.Add(name);
            }
        }

        Add("label", Label is not null);
        Add("root", Root is not null);
        Add("path", Path is not null);
        Add("policy", Policy is not null);
        Add("occurrence", Occurrence is not null);
        Add("state", State is not null);
        Add("comment", Comment is not null);
        Add("options", Options is not null);
        Add("press", Press is not null);
        Add("messageBoxAnswer", MessageBoxAnswer is not null);
        Add("readLists", ReadLists is not null);
        Add("menu", Menu is not null);
        Add("tab", Tab is not null);
        Add("column", Column is not null);
        Add("filter", Filter is not null);
        Add("filterOn", FilterOn is not null);
        Add("maxRows", MaxRows is not null);
        Add("namesOnly", NamesOnly is not null);
        Add("screenshot", Screenshot is not null);
        Add("name", Name is not null);
        Add("waitMs", WaitMs is not null);
        Add("keys", Keys is not null);
        return set;
    }
}

// The file `gpedit ops` reads: {"name": "...", "description": "...", "ops": [ ... ]}. Unknown properties are errors.
internal sealed record OpsFile
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public List<GpeditOp> Ops { get; init; } = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Reads and validates. Every problem is returned with the operation index; a file that cannot be parsed is one problem.
    internal static (OpsFile? File, List<string> Problems) Load(string path)
    {
        var problems = new List<string>();
        OpsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<OpsFile>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: {ex.Message}");
            return (null, problems);
        }

        if (file is null)
        {
            problems.Add($"{path}: the file is empty");
            return (null, problems);
        }

        if (file.Ops.Count == 0)
        {
            problems.Add("the file has no operations");
        }

        for (int i = 0; i < file.Ops.Count; i++)
        {
            foreach (string problem in file.Ops[i].Validate())
            {
                problems.Add($"op[{i}] {file.Ops[i].Kind}: {problem}");
            }
        }

        return (file, problems);
    }
}
