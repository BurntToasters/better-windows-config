using System.Text.Json.Nodes;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// Every press that classic refused with a message box in group E (docs/failure-modes/policy-save.md, section V,
/// rows V1 to
/// V3, V13 and V14) must give a typed problem from <see cref="PolicySave"/> and write nothing. The replay rows
/// only call the
/// engine when the dialog itself would accept the value; this class asks the engine for every refusal, including
/// a required
/// number with no digits, which the dialog of the replay refuses by itself.
/// </summary>
[TestClass]
public sealed class PolicyEditRefusalTests
{
    private sealed record Refusal(string Case, int Op, string Element, PolicyProblemKind Kind, ulong? Limit = null, int? Entry = null);

    private static readonly Refusal[] Table =
    [
        new("Combo_Required_Empty", 0, "ComboNoSortText", PolicyProblemKind.Required),
        new("Combo_Required_Empty", 2, "ComboNoSortText", PolicyProblemKind.Required),
        new("Decimal_Required_Empty", 2, "RequiredNumber", PolicyProblemKind.Required),
        new("Text_Required_Empty", 0, "RequiredText", PolicyProblemKind.Required),
        new("Text_Required_Empty", 2, "RequiredText", PolicyProblemKind.Required),
        new("Enum_Required_Defaults", 0, "RequiredChoice", PolicyProblemKind.Required),
        new("LongDecimal_Required_Empty", 2, "LongTextNumber", PolicyProblemKind.Required),
        new("Ok_DefaultsOfRequired", 4, "RequiredText", PolicyProblemKind.Required),
        new("Ok_DefaultsOfRequired", 6, "ComboNoSortText", PolicyProblemKind.Required),
        new("Ok_DefaultsOfRequired", 8, "RequiredChoice", PolicyProblemKind.Required),
        new("Ok_DefaultsOfRequired", 10, "LimitedLines", PolicyProblemKind.Required),
        new("MultiText_Limits", 0, "LimitedLines", PolicyProblemKind.TooManyLines, 3),
        new("MultiText_Limits", 2, "LimitedLines", PolicyProblemKind.TooLong, 20),
        new("MultiText_Limits", 4, "LimitedLines", PolicyProblemKind.Required),
        new("Next_WithInvalid", 0, "RequiredNumber", PolicyProblemKind.Required),
        new("List_Plain_EmptyEntry", 0, "PlainList", PolicyProblemKind.EntryEmpty, null, 0),
        new("List_Plain_DuplicateEntries", 0, "PlainList", PolicyProblemKind.DuplicateEntry, null, 1),
        new("List_Explicit_DuplicateNames", 0, "ExplicitList", PolicyProblemKind.DuplicateEntry, null, 1),
        new("List_Explicit_EmptyName", 0, "ExplicitList", PolicyProblemKind.EntryNameMissing, null, 0),
    ];

    public static IEnumerable<object[]> Rows() => Table.Select(r => new object[] { r.Case, r.Op, r.Element, r.Kind.ToString() });

    private static IReadOnlyList<JsonObject> RefusedPresses(GoldenOp op) =>
        (op.Data?["presses"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(p => p["messageBoxes"] is JsonArray { Count: > 0 })
            .ToList();

    [TestMethod]
    public void V13_TheTableNamesEveryPressClassicRefusedAndNothingElse()
    {
        var refused = new List<string>();
        foreach (var name in PolicyGoldenReplayDETests.GroupE)
        {
            var golden = GoldenCase.Load("E/" + name);
            refused.AddRange(golden.Ops.Where(o => o.Kind == "editPolicy" && RefusedPresses(o).Count > 0).Select(o => $"{name}#{o.Index}"));
        }

        var listed = Table.Select(r => $"{r.Case}#{r.Op}").ToList();
        CollectionAssert.AreEquivalent(refused, listed, "The refused presses of group E and the table of this class differ (V13).");
    }

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void V14_ARefusedEditGivesTheTypedProblemAndWritesNothing(string name, int opIndex, string element, string kind)
    {
        var row = Table.Single(r => r.Case == name && r.Op == opIndex);
        Assert.AreEqual(kind, row.Kind.ToString());
        var golden = GoldenCase.Load("E/" + name);
        DialogControlRules.Apply(golden);
        var op = golden.Ops.Single(o => o.Index == opIndex);
        Assert.IsTrue(RefusedPresses(op).Count > 0, $"E/{name} operation {opIndex}: classic showed no message box, so the table is stale.");

        var policy = GoldenReplay.LoadedTemplates.FindPolicy(op.Text("policyNamespace")!, op.Text("policyId")!)!;
        var side = op.Text("root") == "User" ? GpoSide.User : GpoSide.Machine;
        var mapped = GoldenOptions.Map(golden.Id, op.Index, policy, op.Op["options"] as JsonArray);
        var defaults = PolicyDefaults.For(policy);
        var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in policy.Elements)
        {
            if (mapped.Values.TryGetValue(e.Id, out var given))
            {
                values[e.Id] = given;
            }
            else if (mapped.DialogRefusal is null && defaults.TryGetValue(e.Id, out var shown))
            {
                values[e.Id] = shown;
            }
        }

        using var sandbox = new GoldenSandbox();
        var outcome = PolicySave.Save(sandbox.Store, side, policy, PolicyEdit.EnabledWith(values), string.Empty);

        Assert.IsFalse(outcome.IsValid, $"E/{name} operation {opIndex}: classic refused the edit and the engine found no problem.");
        Assert.IsNull(outcome.Result, $"E/{name} operation {opIndex}: a refused edit reached the store.");
        Assert.IsFalse(outcome.Changed, $"E/{name} operation {opIndex}: a refused edit changed something.");
        var expected = new PolicyEditProblem(row.Element, row.Kind, row.Limit, row.Entry);
        CollectionAssert.Contains(outcome.Problems.ToList(), expected, $"E/{name} operation {opIndex}: the problems were [{string.Join(", ", outcome.Problems)}].");
        Assert.AreEqual(0, sandbox.FilesOnDisk().Length, $"E/{name} operation {opIndex}: a refused edit left files: {string.Join(", ", sandbox.FilesOnDisk())}");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count, $"E/{name} operation {opIndex}: a refused edit refreshed the policy.");
    }
}
