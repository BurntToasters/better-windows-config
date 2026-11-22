using System.Globalization;
using System.Text.Json.Nodes;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The state matrix of group D (piece E6c, docs/failure-modes/policy-save.md sections M, X and F, and
/// policy-state.md
/// section 17). Each crafted start file is opened by the store in a sandbox and read by <see
/// cref="PolicyStateReader"/>
/// for every policy classic's dialog read; the state and the values the dialog showed must equal what the engine
/// reads.
/// The one unrelated save of each case is compared byte for byte by <see cref="PolicyGoldenReplayDETests"/>; the
/// tests
/// here ask the narrower questions: the state, the shown values, the six refused starts and the records that no
/// policy
/// owns.
/// </summary>
[TestClass]
public sealed class PolicyStateMatrixTests
{
    private static readonly string[] ForeignCasesThatMustBeFound =
    [
        "Foreign_KeyOutsideRoots", "Foreign_KeyUnderRoot", "Foreign_OnlyUnknownOutside", "Foreign_OnlyUnknownUnderPolicies",
        "Foreign_ValueInPolicyKey", "Foreign_ValueOnly", "Instruction_UnknownAlone", "Del_OtherName",
    ];

    /// <summary>The D cases that opened (every case but the six refused starts), one row each.</summary>
    public static IEnumerable<string[]> OpenedCases() =>
        PolicyGoldenReplayDETests.GroupD.Where(c => !PolicyGoldenReplayDETests.RefusedStarts.Contains(c)).Select(c => new[] { c });

    public static IEnumerable<string[]> RefusedCases() => PolicyGoldenReplayDETests.RefusedStarts.Select(c => new[] { c });

    private static GpoSide SideOf(string? root) =>
        root switch
        {
            "Computer" => GpoSide.Machine,
            "User" => GpoSide.User,
            _ => throw new AssertFailedException($"unknown root '{root}'"),
        };

    private static string StateName(PolicyState state) =>
        state switch
        {
            PolicyState.Enabled => "Enabled",
            PolicyState.Disabled => "Disabled",
            _ => "Not Configured",
        };

    private static void SeedStart(GoldenCase golden, GoldenSandbox sandbox)
    {
        foreach (var name in golden.StartFiles.Keys)
        {
            sandbox.SeedFrom(name, Path.Combine(golden.StartFolder, name));
        }
    }

    /// <summary>The read operations of a case: an editPolicy operation that sets no state and no comment, with the
    /// state the dialog showed.</summary>
    private static IEnumerable<(GoldenOp Op, PolicyDefinition Policy, GpoSide Side, string Shown)> Reads(GoldenCase golden)
    {
        foreach (var op in golden.Ops.Where(o => o.Kind == "editPolicy" && o.Text("state") is null && o.Text("comment") is null))
        {
            var shown = op.Data?["before"]?["state"]?.GetValue<string>();
            if (shown is null)
            {
                continue;
            }

            var ns = op.Text("policyNamespace") ?? throw new AssertFailedException($"{golden.Id} operation {op.Index}: no policyNamespace");
            var id = op.Text("policyId") ?? throw new AssertFailedException($"{golden.Id} operation {op.Index}: no policyId");
            var policy = GoldenReplay.LoadedTemplates.FindPolicy(ns, id)
                ?? throw new AssertFailedException($"{golden.Id} operation {op.Index}: policy {ns}:{id} is not in the fixture templates");
            yield return (op, policy, SideOf(op.Text("root")), shown);
        }
    }

    [TestMethod]
    [DynamicData(nameof(OpenedCases))]
    public void M1_EngineReadsTheStateTheDialogShowed(string name)
    {
        var golden = GoldenCase.Load("D/" + name);
        using var sandbox = new GoldenSandbox();
        SeedStart(golden, sandbox);
        var snapshot = sandbox.Store.Open();
        var problems = new List<string>();
        var reads = Reads(golden).ToList();
        foreach (var (op, policy, side, shown) in reads)
        {
            var pol = snapshot.Side(side).Pol;
            if (pol is null)
            {
                problems.Add($"operation {op.Index} ({policy.Name} under {op.Text("root")}): the engine cannot read the file classic read: {snapshot.Side(side).PolFailure?.Message}");
                continue;
            }

            var read = PolicyStateReader.Read(policy, pol);
            if (StateName(read.State) != shown)
            {
                problems.Add($"operation {op.Index} ({policy.Name} under {op.Text("root")}): the dialog showed {shown}, the engine reads {StateName(read.State)}");
            }
        }

        Assert.AreEqual(0, problems.Count, $"D/{name}: {string.Join(" | ", problems)}");
    }

    [TestMethod]
    public void M3_TheMatrixComparesAtLeastOneHundredCasesAndOneHundredTenStates()
    {
        var compared = 0;
        var cases = 0;
        foreach (var row in OpenedCases())
        {
            var reads = Reads(GoldenCase.Load("D/" + row[0])).Count();
            compared += reads;
            cases += reads > 0 ? 1 : 0;
        }

        Assert.IsTrue(cases >= 100, $"only {cases} opened D cases read a state; the matrix must not pass empty.");
        Assert.IsTrue(compared >= 110, $"only {compared} dialog states were found in group D; the matrix must not pass empty.");
    }

    [TestMethod]
    [DynamicData(nameof(OpenedCases))]
    public void M5_EngineReadsTheValuesTheDialogShowedForAnEnabledPolicy(string name)
    {
        var golden = GoldenCase.Load("D/" + name);
        using var sandbox = new GoldenSandbox();
        SeedStart(golden, sandbox);
        var snapshot = sandbox.Store.Open();
        var problems = new List<string>();
        foreach (var (op, policy, side, shown) in Reads(golden).Where(r => r.Shown == "Enabled"))
        {
            var pol = snapshot.Side(side).Pol;
            if (pol is null || op.Data?["before"]?["options"] is not JsonArray options)
            {
                continue;
            }

            var controls = policy.Presentation?.Controls ?? [];
            if (controls.Count != options.Count)
            {
                problems.Add($"operation {op.Index}: the dialog showed {options.Count} controls, the presentation of {policy.Name} has {controls.Count}");
                continue;
            }

            var read = PolicyStateReader.Read(policy, pol);
            var defaults = PolicyDefaults.For(policy);
            for (var i = 0; i < controls.Count; i++)
            {
                if (controls[i].Element is not { } element || options[i] is not JsonObject option)
                {
                    continue;
                }

                var value = read.Elements.TryGetValue(element.Id, out var stored) ? stored : defaults.GetValueOrDefault(element.Id);
                if (Mismatch(element, option, value) is { } why)
                {
                    problems.Add($"operation {op.Index} ({policy.Name}, {element.Id}): {why}");
                }
            }
        }

        Assert.AreEqual(0, problems.Count, $"D/{name}: {string.Join(" | ", problems)}");
    }

    /// <summary>What differs between a control of the dialog and the engine's value, or null. Lists and multi-line
    /// boxes are not compared:
    /// the capture holds their contents as a hash of a text format the engine has no counterpart for.</summary>
    private static string? Mismatch(PolicyElement element, JsonObject option, ElementValue? value)
    {
        switch (element)
        {
            case BooleanElement:
                var state = option["checkState"]?.GetValue<int>();
                return value is BooleanValue b && state is 0 or 1 && b.Checked == (state == 1) ? null : $"the check box showed {state}, the engine has {value}";
            case DecimalElement number:
                var digits = option["text"]?.GetValue<string>() ?? string.Empty;
                if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var shownNumber) || value is not NumberValue held)
                {
                    return $"the box showed '{digits}', the engine has {value}";
                }

                // The box shows the stored number held inside the minimum and maximum of the element.
                var ceiling = number.Kind == PolicyElementKind.Decimal ? uint.MaxValue : ulong.MaxValue;
                var min = number.MinValue ?? 0UL;
                var max = Math.Min(number.MaxValue ?? ceiling, ceiling);
                var shownByEngine = Math.Clamp(held.Number, min, max);
                return shownByEngine == shownNumber ? null : $"the box showed {shownNumber}, the engine has {held.Number} (held to {shownByEngine})";
            case TextElement:
                var text = option["text"]?.GetValue<string>() ?? string.Empty;
                return value is TextValue t && CapturedDefaults.TextMatches(text, t.Text) ? null : $"the box showed '{text}', the engine has {value}";
            case EnumElement choice:
                var selected = option["selectedIndex"]?.GetValue<int>() ?? -1;
                if (selected < 0)
                {
                    return value is ChoiceValue ? $"nothing was selected, the engine has {value}" : null;
                }

                var shownItem = option["selectedText"]?.GetValue<string>() ?? string.Empty;
                var at = Enumerable.Range(0, choice.Items.Count).Where(n => CapturedDefaults.TextMatches(shownItem, choice.Items[n].DisplayName)).ToArray();
                return value is ChoiceValue c && at.Length == 1 && c.ItemIndex == at[0] ? null : $"the item '{shownItem}' was selected, the engine has {value}";
            default:
                return null;
        }
    }

    [TestMethod]
    public void M5_TheValueMatrixCoversAtLeastTenEnabledCases()
    {
        var enabledWithOptions = OpenedCases().Count(row =>
            Reads(GoldenCase.Load("D/" + row[0])).Any(r => r.Shown == "Enabled" && r.Op.Data?["before"]?["options"] is JsonArray { Count: > 0 }));
        Assert.IsTrue(enabledWithOptions >= 10, $"only {enabledWithOptions} D cases show an Enabled policy with controls; the value matrix must not pass empty.");
    }

    [TestMethod]
    [DynamicData(nameof(RefusedCases))]
    public void X_RefusedStart_StoreRefusesToOpenAndWritesNothing(string name)
    {
        var golden = GoldenCase.Load("D/" + name);
        Assert.IsTrue(golden.StartRefused, $"D/{name} is not a refused start in the capture.");
        using var sandbox = new GoldenSandbox();
        SeedStart(golden, sandbox);
        var before = GoldenFiles.Names.ToDictionary(n => n, sandbox.Read);

        var snapshot = sandbox.Store.Open();
        Assert.IsNull(snapshot.Machine.Pol, $"D/{name}: classic refused this file, the engine opened it.");
        Assert.IsNotNull(snapshot.Machine.PolFailure, $"D/{name}: the engine reports no failure for a file classic refused.");

        var policy = GoldenReplay.LoadedTemplates.FindPolicy("BWC.Policies.Oracle", "Pol_Sort_Zulu")!;
        var ex = Assert.Throws<GpoStoreException>(() => PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, PolicyEdit.Enabled(), string.Empty));
        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason, $"D/{name}: a save over a refused file must fail as malformed.");
        var comment = Assert.Throws<GpoStoreException>(() => PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, null, "a comment"));
        Assert.AreEqual(GpoStoreFailure.Malformed, comment.Reason, $"D/{name}: a comment-only save over a refused file must fail as malformed.");

        foreach (var file in GoldenFiles.Names)
        {
            var after = sandbox.Read(file);
            Assert.AreEqual(before[file] is null, after is null, $"D/{name}: {file} appeared or vanished.");
            if (before[file] is not null)
            {
                CollectionAssert.AreEqual(before[file], after, $"D/{name}: {file} changed.");
            }
        }

        var known = GoldenFiles.Names.Where(n => before[n] is not null).Select(n => Path.GetRelativePath(sandbox.GpoRoot, sandbox.PathOf(n)).Replace('\\', '/')).Order(StringComparer.Ordinal);
        CollectionAssert.AreEqual(known.ToArray(), sandbox.FilesOnDisk(), $"D/{name}: the GPO folder holds other files than the start files.");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count, $"D/{name}: a refused save refreshed the policy.");
        var backups = Directory.Exists(sandbox.Policy.BackupRoot)
            ? Directory.GetFileSystemEntries(sandbox.Policy.BackupRoot).Select(Path.GetFileName).Where(n => n != "write.lock").ToArray()
            : [];
        Assert.AreEqual(0, backups.Length, $"D/{name}: a refused save made a backup: {string.Join(", ", backups)}");
    }

    private static HashSet<(string Key, string Name)> OwnedBySomePolicy(PolDocument document)
    {
        var owned = new HashSet<(string, string)>();
        foreach (var policy in GoldenReplay.LoadedTemplates.Policies)
        {
            foreach (var record in PolicyStateReader.Read(policy, document).OwnedRecords)
            {
                owned.Add((record.Key, record.ValueName));
            }
        }

        return owned;
    }

    /// <summary>The records of a document that no policy of the fixture owns, as the hex of their raw bytes in
    /// document order.</summary>
    private static List<string> ForeignRecords(PolDocument document)
    {
        var owned = OwnedBySomePolicy(document);
        return document.EnumerateRecords()
            .Where(r => !owned.Contains((r.Key, r.ValueName)))
            .Select(r => Convert.ToHexString(r.RawBytes.Span))
            .ToList();
    }

    private static readonly string[] SideFiles = [GoldenFiles.Names[0], GoldenFiles.Names[3]];

    /// <summary>The D cases (not refused) that carry at least one record no policy owns, with the foreign records of
    /// each side file.</summary>
    private static List<(string Name, string File, List<string> Records)> CasesWithForeignRecords()
    {
        var found = new List<(string, string, List<string>)>();
        foreach (var row in OpenedCases())
        {
            var golden = GoldenCase.Load("D/" + row[0]);
            foreach (var file in SideFiles.Where(golden.StartFiles.ContainsKey))
            {
                if (!PolDocument.TryParse(File.ReadAllBytes(Path.Combine(golden.StartFolder, file)), out var document, out _))
                {
                    // A file the engine cannot read is reported by M1 and by the replay row of the case.
                    continue;
                }

                var foreign = ForeignRecords(document);
                if (foreign.Count > 0)
                {
                    found.Add((row[0], file, foreign));
                }
            }
        }

        return found;
    }

    public static IEnumerable<string[]> ForeignRows() => CasesWithForeignRecords().Select(f => f.Name).Distinct().Select(n => new[] { n });

    [TestMethod]
    public void F5_TheEightNamedForeignCasesAreFound()
    {
        var found = CasesWithForeignRecords().Select(f => f.Name).Distinct().ToHashSet(StringComparer.Ordinal);
        var missing = ForeignCasesThatMustBeFound.Where(c => !found.Contains(c)).ToArray();
        Assert.AreEqual(0, missing.Length, $"these D cases should carry records no policy owns and the engine found none: {string.Join(", ", missing)}");
    }

    [TestMethod]
    [DynamicData(nameof(ForeignRows))]
    public void F4_ClassicKeptTheForeignRecordsAtEveryStep(string name)
    {
        var golden = GoldenCase.Load("D/" + name);
        foreach (var file in SideFiles.Where(golden.StartFiles.ContainsKey))
        {
            var start = ForeignRecords(PolDocument.Parse(File.ReadAllBytes(Path.Combine(golden.StartFolder, file))));
            foreach (var step in golden.Steps.Skip(1))
            {
                var kept = ForeignRecords(PolDocument.Parse(File.ReadAllBytes(step.PathOf(file))));
                CollectionAssert.AreEqual(start, kept, $"D/{name} step {step.Number} '{step.Label}': classic did not keep the foreign records of {file} as they were.");
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(ForeignRows))]
    public void F1_F2_ForeignRecordsSurviveEverySaveByteForByteInOrder(string name)
    {
        var golden = GoldenCase.Load("D/" + name);
        using var sandbox = new GoldenSandbox();
        SeedStart(golden, sandbox);
        var expected = SideFiles.Where(golden.StartFiles.ContainsKey)
            .ToDictionary(f => f, f => ForeignRecords(PolDocument.Parse(File.ReadAllBytes(Path.Combine(golden.StartFolder, f)))));
        var saves = 0;
        foreach (var op in golden.Ops.Where(o => o.Kind == "editPolicy" && o.Text("state") is not null))
        {
            Assert.IsFalse(op.Op["options"] is JsonArray { Count: > 0 }, $"D/{name} operation {op.Index}: the unrelated save of group D has no options.");
            var policy = GoldenReplay.LoadedTemplates.FindPolicy(op.Text("policyNamespace")!, op.Text("policyId")!)!;
            var side = SideOf(op.Text("root"));
            var edit = op.Text("state") switch
            {
                "Enabled" => PolicyEdit.Enabled(),
                "Disabled" => PolicyEdit.Disabled,
                _ => PolicyEdit.NotConfigured,
            };
            var outcome = PolicySave.Save(sandbox.Store, side, policy, edit, string.Empty);
            Assert.IsTrue(outcome.IsValid, $"D/{name} operation {op.Index}: the unrelated save reports problems.");
            saves++;
            foreach (var (file, records) in expected)
            {
                var after = ForeignRecords(PolDocument.Parse(sandbox.Read(file)!));
                CollectionAssert.AreEqual(records, after, $"D/{name} after operation {op.Index}: the foreign records of {file} changed.");
            }
        }

        Assert.IsTrue(saves >= 2, $"D/{name}: the case has {saves} saving operations; the unrelated save and its clearing were expected.");
    }
}
