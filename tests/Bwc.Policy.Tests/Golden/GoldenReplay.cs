using System.Text;
using System.Text.Json.Nodes;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// Replays one capture case against the engine in a sandbox GPO folder: every structured editPolicy
/// operation becomes calls of <see cref="PolicySave.Save"/>, and after every recorded snapshot the five
/// files must equal the step folder byte for byte. The failure modes are listed in
/// docs/failure-modes/policy-save.md, section R.
/// </summary>
internal static class GoldenReplay
{
    private static readonly Lazy<TemplateSet> Templates = new(LoadTemplates);

    /// <summary>The fixture templates the captures were made with, including the edge fixtures.</summary>
    internal static TemplateSet LoadedTemplates => Templates.Value;

    private static TemplateSet LoadTemplates()
    {
        var folders = new List<string> { Fixture.Root };
        var edge = Path.Combine(Fixture.Root, "edge");
        if (Directory.Exists(edge))
        {
            folders.AddRange(Directory.GetDirectories(edge).Order(StringComparer.Ordinal));
        }

        return TemplateLoader.Load(folders, "en-US");
    }

    internal static void Run(GoldenCase golden)
    {
        if (UnnoticedEditRule.FindInCase(golden) is { } cap4)
        {
            Assert.Fail(cap4);
        }

        using var sandbox = new GoldenSandbox();
        if (golden.StartRefused)
        {
            RunRefusedStart(golden, sandbox);
            return;
        }

        SeedStart(golden, sandbox);
        var step = 0;
        foreach (var op in golden.Ops)
        {
            switch (op.Kind)
            {
                case "snapshot":
                    CompareStep(golden, sandbox, golden.Steps[step], $"after operation {op.Index}");
                    step++;
                    break;
                case "editPolicy":
                    ReplayEdit(golden, sandbox, op);
                    break;
                case "selectNode":
                case "readList":
                    break;
                default:
                    Assert.Fail($"{golden.Id} operation {op.Index}: the replay cannot run an operation of kind '{op.Kind}'.");
                    break;
            }
        }
    }

    private static void SeedStart(GoldenCase golden, GoldenSandbox sandbox)
    {
        if (golden.Ops[0].Kind == "snapshot")
        {
            foreach (var name in GoldenFiles.Names.Where(n => golden.Steps[0].Files[n] is not null))
            {
                sandbox.SeedFrom(name, golden.Steps[0].PathOf(name));
            }

            return;
        }

        foreach (var name in golden.StartFiles.Keys)
        {
            sandbox.SeedFrom(name, Path.Combine(golden.StartFolder, name));
        }

        // Classic creates a policy version file with an empty General section when it opens a GPO that has none. The start
        // snapshot of A/Machine_Cancel and A/Machine_NotConfigured holds exactly that, and B/Pol_Enum_Required,
        // B/Pol_Text_Required and B/Pol_MultiText_Limits still hold it after a refused first press. The engine only saves,
        // so the replay does the open.
        if (!golden.StartFiles.ContainsKey(GpoFileNames.GptIniFile))
        {
            sandbox.Seed(sandbox.PathOf(GpoFileNames.GptIniFile), Encoding.ASCII.GetBytes("[General]\r\n"));
        }
    }

    private static PolicyDefinition FindPolicy(GoldenCase golden, GoldenOp op)
    {
        var ns = op.Text("policyNamespace") ?? throw new AssertFailedException($"{golden.Id} operation {op.Index}: no policyNamespace.");
        var id = op.Text("policyId") ?? throw new AssertFailedException($"{golden.Id} operation {op.Index}: no policyId.");
        return Templates.Value.FindPolicy(ns, id)
            ?? throw new AssertFailedException(
                $"{golden.Id} operation {op.Index}: policy {ns}:{id} is not in the loaded templates. Load failures: "
                + string.Join("; ", Templates.Value.Failures.Select(f => $"{f.Code} {Path.GetFileName(f.FilePath)} {f.Detail}")));
    }

    private static GpoSide SideOf(GoldenCase golden, GoldenOp op) =>
        op.Text("root") switch
        {
            "Computer" => GpoSide.Machine,
            "User" => GpoSide.User,
            var other => throw new AssertFailedException($"{golden.Id} operation {op.Index}: unknown root '{other}'."),
        };

    private static string StateName(PolicyState state) =>
        state switch
        {
            PolicyState.Enabled => "Enabled",
            PolicyState.Disabled => "Disabled",
            _ => "Not Configured",
        };

    private static void ReplayEdit(GoldenCase golden, GoldenSandbox sandbox, GoldenOp op)
    {
        var ctx = $"{golden.Id} operation {op.Index}";
        var policy = FindPolicy(golden, op);
        var side = SideOf(golden, op);
        var data = op.Data ?? throw new AssertFailedException($"{ctx}: the capture holds no observed data for this operation.");

        var snapshot = sandbox.Store.Open();
        var pol = snapshot.Side(side).Pol
            ?? throw new AssertFailedException($"{ctx}: the engine cannot read the policy file that classic read: {snapshot.Side(side).PolFailure?.Message}");
        var current = PolicyStateReader.Read(policy, pol);
        if (data["before"]?["state"] is JsonValue shown && shown.TryGetValue(out string? shownState))
        {
            Assert.AreEqual(shownState, StateName(current.State), $"{ctx}: the state the engine reads differs from the state the dialog showed.");
        }

        var currentComment = snapshot.Side(side).Comments?.GetComment(policy.Namespace, policy.Name) ?? string.Empty;
        var comment = op.Text("comment") ?? currentComment;
        var (edit, dialogRefusal) = BuildEdit(golden, op, policy, current);

        var presses = (op.Op["press"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray() ?? ["OK"];
        var observed = (data["presses"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        for (var k = 0; k < presses.Length; k++)
        {
            if (presses[k] == "Cancel")
            {
                continue;
            }

            var press = k < observed.Count ? observed[k] : throw new AssertFailedException($"{ctx}: press {k} ({presses[k]}) was not observed.");
            if (press["pressed"] is JsonValue pressedFlag && pressedFlag.TryGetValue(out bool wasPressed) && !wasPressed)
            {
                // The button was disabled and the driver did not press it (C/NoChange_Apply): nothing is saved.
                continue;
            }

            if ((press["messageBoxes"] as JsonArray)?.Count > 0)
            {
                ExpectRefusal(sandbox, ctx, side, policy, edit, comment, dialogRefusal);
                continue;
            }

            Assert.IsNull(dialogRefusal, $"{ctx}: the engine side would refuse ({dialogRefusal}) but classic saved.");
            var outcome = PolicySave.Save(sandbox.Store, side, policy, edit, comment);
            Assert.IsTrue(outcome.IsValid, $"{ctx}: classic saved but the engine reports problems: {Describe(outcome.Problems)}");
            var expected = ((press["files"]?["changedFiles"] as JsonArray)
                ?? throw new AssertFailedException($"{ctx}: press {presses[k]} has no recorded change list"))
                .Select(n => n!.GetValue<string>().Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            var written = (outcome.Result?.WrittenFiles ?? [])
                .Select(f => Path.GetRelativePath(sandbox.GpoRoot, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(
                expected,
                written,
                $"{ctx}: press {presses[k]} wrote [{string.Join(", ", written)}] but classic changed [{string.Join(", ", expected)}].");
        }
    }

    private static (PolicyEdit? Edit, string? DialogRefusal) BuildEdit(GoldenCase golden, GoldenOp op, PolicyDefinition policy, PolicyStateSnapshot current)
    {
        var ctx = $"{golden.Id} operation {op.Index}";
        var options = op.Op["options"] as JsonArray;
        var state = op.Text("state") ?? (options is null ? null : "Enabled");
        GoldenOptions.RequireEnabledForOptions(golden.Id, op.Index, state, options);
        switch (state)
        {
            case null:
                return (null, null);
            case "Disabled":
                return (PolicyEdit.Disabled, null);
            case "Not Configured":
                return (PolicyEdit.NotConfigured, null);
            case "Enabled":
                // The dialog shows the stored values of an Enabled policy and the defaults of the presentation for the rest.
                var stored = current.State == PolicyState.Enabled ? current.Elements : new Dictionary<string, ElementValue>();
                var mapped = GoldenOptions.Map(golden.Id, op.Index, policy, options, stored);
                var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
                var defaults = new Lazy<IReadOnlyDictionary<string, ElementValue>>(() => PolicyDefaults.For(policy));
                foreach (var element in policy.Elements)
                {
                    if (mapped.Values.TryGetValue(element.Id, out var given))
                    {
                        values[element.Id] = given;
                    }
                    else if (stored.TryGetValue(element.Id, out var kept))
                    {
                        values[element.Id] = kept;
                    }
                    else if (mapped.DialogRefusal is null && defaults.Value.TryGetValue(element.Id, out var shown))
                    {
                        values[element.Id] = shown;
                    }
                }

                return (PolicyEdit.EnabledWith(values), mapped.DialogRefusal);
            default:
                throw new AssertFailedException($"{ctx}: unknown state '{state}'.");
        }
    }

    private static void ExpectRefusal(
        GoldenSandbox sandbox, string ctx, GpoSide side, PolicyDefinition policy, PolicyEdit? edit, string comment, string? dialogRefusal)
    {
        var before = ReadAll(sandbox);
        if (dialogRefusal is null)
        {
            var outcome = PolicySave.Save(sandbox.Store, side, policy, edit, comment);
            Assert.IsFalse(outcome.IsValid, $"{ctx}: classic refused with a message box but the engine found no problem.");
            Assert.IsNull(outcome.Result, $"{ctx}: a refused edit must not reach the store.");
            Assert.IsFalse(outcome.Changed, $"{ctx}: a refused edit must change nothing.");
        }

        var after = ReadAll(sandbox);
        foreach (var name in GoldenFiles.Names)
        {
            Assert.IsTrue(
                before[name] is null ? after[name] is null : after[name] is not null && before[name]!.AsSpan().SequenceEqual(after[name]),
                $"{ctx}: a refused edit changed {name}.");
        }
    }

    private static void RunRefusedStart(GoldenCase golden, GoldenSandbox sandbox)
    {
        foreach (var name in golden.StartFiles.Keys)
        {
            sandbox.SeedFrom(name, Path.Combine(golden.StartFolder, name));
        }

        var snapshot = sandbox.Store.Open();
        var reported = new[]
        {
            snapshot.Machine.PolFailure, snapshot.Machine.CommentsFailure, snapshot.User.PolFailure, snapshot.User.CommentsFailure, snapshot.GptIniFailure,
        };
        Assert.IsTrue(
            reported.Any(f => f is not null),
            $"{golden.Id}: classic refused to open this GPO; the engine must report a malformed-file failure on open and it reported none.");

        var op = golden.Ops.First(o => o.Kind == "editPolicy" && o.Text("state") is not null);
        var policy = FindPolicy(golden, op);
        var (edit, _) = BuildEdit(golden, op, policy, PolicyStateReader.Read(policy, Bwc.Gpo.Pol.PolDocument.Create()));
        var ex = Assert.Throws<GpoStoreException>(() => PolicySave.Save(sandbox.Store, SideOf(golden, op), policy, edit, string.Empty));
        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason, $"{golden.Id}: a save over a file classic refused must fail as malformed.");

        var after = ReadAll(sandbox);
        foreach (var name in GoldenFiles.Names)
        {
            if (golden.StartFiles.ContainsKey(name))
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(golden.StartFolder, name)), after[name], $"{golden.Id}: the refused start file {name} was changed.");
            }
            else
            {
                Assert.IsNull(after[name], $"{golden.Id}: the engine wrote {name} over a start classic refused.");
            }
        }
    }

    private static Dictionary<string, byte[]?> ReadAll(GoldenSandbox sandbox) => GoldenFiles.Names.ToDictionary(n => n, sandbox.Read);

    internal static void CompareStep(GoldenCase golden, GoldenSandbox sandbox, GoldenStep step, string moment)
    {
        var ctx = $"{golden.Id} {moment} (step {step.Number} '{step.Label}')";
        var problems = new List<string>();
        foreach (var name in GoldenFiles.Names)
        {
            var actual = sandbox.Read(name);
            if (step.Files[name] is null)
            {
                if (actual is not null)
                {
                    problems.Add($"{name} must be absent but the engine wrote {actual.Length} bytes");
                }

                continue;
            }

            var expected = File.ReadAllBytes(step.PathOf(name));
            if (actual is null)
            {
                problems.Add($"{name} is missing; classic wrote {expected.Length} bytes");
            }
            else if (!expected.AsSpan().SequenceEqual(actual))
            {
                problems.Add($"{name} differs: {Diff(expected, actual)}");
            }
        }

        var known = GoldenFiles.Names.Select(n => Path.GetRelativePath(sandbox.GpoRoot, sandbox.PathOf(n)).Replace('\\', '/')).ToArray();
        var extras = sandbox.FilesOnDisk().Where(f => !known.Contains(f, StringComparer.Ordinal));
        problems.AddRange(extras.Select(e => $"unexpected file {e} in the GPO folder"));
        Assert.AreEqual(0, problems.Count, $"{ctx}: {string.Join(" | ", problems)}");
    }

    private static string Diff(byte[] expected, byte[] actual)
    {
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        string Window(byte[] bytes) =>
            string.Concat(bytes.Skip(Math.Max(0, at - 8)).Take(32).Select(b => b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"\\x{b:X2}"));

        return $"expected {expected.Length} bytes, got {actual.Length}; first difference at offset {at}; expected \"{Window(expected)}\" got \"{Window(actual)}\"";
    }

    private static string Describe(IReadOnlyList<PolicyEditProblem> problems) =>
        string.Join(", ", problems.Select(p => $"{p.ElementId}:{p.Kind}"));
}
