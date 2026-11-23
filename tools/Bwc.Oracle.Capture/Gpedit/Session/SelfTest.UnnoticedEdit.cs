using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The edit the dialog never noticed (CAP-4): the pure decision, the judgement of a made-up result, the plan check, and the shape of the
// plan that came out of it. Failure mode 21 of the matrix document. These cases were written before the code they check. No classic is needed:
// the results are built like the ones the driver writes, with the keys the check reads.
internal static partial class SelfTest
{
    private static readonly string[] ClearedCases =
    {
        "E/Decimal_EdgeValues", "E/Decimal_NotNumber", "E/Decimal_Soft_OutOfRange", "E/Decimal_StoreAsText_OutOfRange", "E/LongDecimal_AboveMax",
    };

    // The data of an editPolicy operation: an optional state set, one number box set, Apply after the sets, and the presses.
    private static JsonObject EditData(string? stateBefore, string stateObserved, string textBefore, string textObserved, bool? applyAfter, int pressBoxes, int boxesBefore, bool withPresses = true)
    {
        var sets = new JsonArray();
        if (stateBefore is not null)
        {
            sets.Add(new JsonObject { ["set"] = "state", ["requested"] = "Enabled", ["before"] = stateBefore, ["observed"] = stateObserved, ["via"] = "click" });
        }

        sets.Add(new JsonObject
        {
            ["typed"] = false,
            ["via"] = "WM_SETTEXT",
            ["requested"] = textObserved,
            ["before"] = textBefore,
            ["immediate"] = textObserved,
            ["observed"] = textObserved,
            ["set"] = "option",
            ["label"] = "Number from 10 to 500:",
            ["kind"] = "decimalTextBox",
        });
        var press = new JsonObject { ["button"] = "OK", ["pressed"] = true, ["messageBoxes"] = new JsonArray(Enumerable.Range(0, pressBoxes).Select(_ => (JsonNode?)FakeBox()).ToArray()) };
        if (boxesBefore > 0)
        {
            press["boxesBeforePress"] = new JsonArray(Enumerable.Range(0, boxesBefore).Select(_ => (JsonNode?)FakeBox()).ToArray());
        }

        var data = new JsonObject
        {
            ["sets"] = sets,
            ["afterSet"] = new JsonObject { ["buttons"] = new JsonObject { ["Apply"] = new JsonObject { ["enabled"] = applyAfter } } },
        };
        if (withPresses)
        {
            data["presses"] = new JsonArray(press);
        }

        return data;
    }

    // A result for a case of the plan in which every operation is ok. dataFor gives the data of an editPolicy operation by its index.
    private static JsonObject UnnoticedResult(MatrixCase c, Func<int, JsonObject> dataFor, Func<int, bool> ok)
    {
        var ops = new JsonArray();
        for (int i = 0; i < c.Ops.Count; i++)
        {
            ops.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = c.Ops[i].Kind,
                ["ok"] = ok(i),
                ["data"] = c.Ops[i].Kind == "editPolicy" ? dataFor(i) : new JsonObject(),
            });
        }

        return new JsonObject { ["liveRegistryAtStart"] = new JsonObject(), ["ops"] = ops };
    }

    private static void UnnoticedEditCases(RepoPaths repo, Action<string, Action> Case)
    {
        MatrixPlan? plan = null;
        MatrixPlan Plan() => plan ??= MatrixPlan.Build(repo);

        Case("unnoticed edit: the decision needs all four facts", () =>
        {
            Expect(UnnoticedEdit.IsUnnoticed(textChanged: true, stateChanged: false, applyDisabledAfterSet: true, boxSeen: false), "the pattern was not found");
            Expect(!UnnoticedEdit.IsUnnoticed(textChanged: false, stateChanged: false, applyDisabledAfterSet: true, boxSeen: false), "a set that changed no text was taken as unnoticed");
            Expect(!UnnoticedEdit.IsUnnoticed(textChanged: true, stateChanged: true, applyDisabledAfterSet: true, boxSeen: false), "a state change was taken as unnoticed");
            Expect(!UnnoticedEdit.IsUnnoticed(textChanged: true, stateChanged: false, applyDisabledAfterSet: false, boxSeen: false), "an enabled Apply was taken as unnoticed");
            Expect(!UnnoticedEdit.IsUnnoticed(textChanged: true, stateChanged: false, applyDisabledAfterSet: true, boxSeen: true), "a refusal with a message box was taken as unnoticed");
        });

        Case("unnoticed edit: a result is judged on its keys, and a missing fact never fails it", () =>
        {
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "173", false, 0, 0)) is not null, "the Enabled to Enabled number set was not found");
            Expect(UnnoticedEdit.Find(EditData(null, string.Empty, "25", "173", false, 0, 0)) is not null, "a set with no state set was not found");
            Expect(UnnoticedEdit.Find(EditData("Not Configured", "Enabled", "10", "173", false, 0, 0)) is null, "a state change was judged");
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "25", false, 0, 0)) is null, "a set that left the text as it was was judged");
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "173", true, 0, 0)) is null, "an enabled Apply was judged");
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "173", null, 0, 0)) is null, "an Apply button that was not read was judged");
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "9999", false, 1, 0)) is null, "a box after the press was judged");
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "500", "501", false, 0, 1)) is null, "a box before the press was judged");
            Expect(UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "173", false, 0, 0, withPresses: false)) is null, "a result with no presses was judged");

            JsonObject noAfter = EditData("Enabled", "Enabled", "25", "173", false, 0, 0);
            noAfter.Remove("afterSet");
            Expect(UnnoticedEdit.Find(noAfter) is null, "a result with no afterSet was judged");

            JsonObject comment = EditData("Enabled", "Enabled", "25", "173", false, 0, 0);
            comment["sets"]!.AsArray()[1]!.AsObject()["set"] = "comment";
            Expect(UnnoticedEdit.Find(comment) is null, "a comment set was judged as a number set");

            string? text = UnnoticedEdit.Find(EditData("Enabled", "Enabled", "25", "173", false, 0, 0));
            Expect(text!.Contains("did not notice", StringComparison.Ordinal) && text.Contains("Number from 10 to 500:", StringComparison.Ordinal), "the failure text does not name the control");
        });

        Case("unnoticed edit: an unnoticed edit fails the case, optional or not, and a noticed one does not", () =>
        {
            MatrixCase c = Plan().Cases.Single(x => x.FullId == "E/Decimal_Soft_OutOfRange");
            int below = c.Ops.FindLastIndex(o => o.Kind == "editPolicy" && o.Options is { Count: > 0 });
            Expect(c.Optional.Contains(below), "the last step of the case is not optional, so the optional path is not tested");

            JsonObject Clean(int _) => EditData("Not Configured", "Enabled", "1", "1000", true, 0, 0);
            JsonObject Bad(int i) => i == below ? EditData("Enabled", "Enabled", "1000", "0", false, 0, 0) : Clean(i);

            Expect(CaseOutput.Evaluate(c, UnnoticedResult(c, Clean, _ => true), out _, out string? cleanWhy), "a result with every edit noticed failed: " + cleanWhy);
            Expect(!CaseOutput.Evaluate(c, UnnoticedResult(c, Bad, _ => true), out _, out string? why), "an unnoticed edit evaluated as complete");
            Expect(why!.Contains($"operation {below}", StringComparison.Ordinal) && why.Contains("did not notice", StringComparison.Ordinal), "the failure does not name the operation: " + why);
            Expect(!CaseOutput.Evaluate(c, UnnoticedResult(c, Bad, i => i != below), out _, out _), "an unnoticed edit in an optional operation that failed evaluated as complete");

            JsonObject WithBox(int i) => i == below ? EditData("Enabled", "Enabled", "1000", "0", false, 1, 0) : Clean(i);
            Expect(CaseOutput.Evaluate(c, UnnoticedResult(c, WithBox, _ => true), out _, out string? boxWhy), "an edit that classic refused with a box failed: " + boxWhy);

            MatrixCase normal = Plan().Cases.Single(x => x.FullId == "B/Pol_Decimal_MinMax");
            int plain = normal.Ops.FindIndex(o => o.Kind == "editPolicy");
            Expect(!normal.Optional.Contains(plain), "the first step of a B case is optional");
            JsonObject BadFirst(int i) => i == plain ? EditData("Enabled", "Enabled", "25", "173", false, 0, 0) : Clean(i);
            Expect(!CaseOutput.Evaluate(normal, UnnoticedResult(normal, BadFirst, _ => true), out _, out _), "an unnoticed edit in a required operation evaluated as complete");
        });

        Case("unnoticed edit: the plan check finds a ranged number set without keys on an Enabled policy", () =>
        {
            PolicyInfo ranged = Plan().Fixture.Policy("Pol_Decimal_MinMax");
            PolicyInfo plain = Plan().Fixture.Policy("Pol_Text_Plain");
            ElementInfo element = ranged.Elements[0];
            Expect(UnnoticedEdit.NeedsKeys(element), "a number with both limits was not found to need keys");
            Expect(!UnnoticedEdit.NeedsKeys(Plan().Fixture.Policy("Pol_Decimal_Required").Elements[0]), "a number with no range was found to need keys");

            List<OptionSpec> Number(bool typed) => new() { OptionChoices.Base(ranged, element) with { Text = "173", Typed = typed ? true : null } };
            MatrixCase Make(bool typed, bool clearBetween, string state2 = "Enabled")
            {
                var b = new CaseBuilder("E", "PlanCheck", "made up").Step("one", MatrixOps.Edit(ranged, "Computer", "Enabled"));
                if (clearBetween)
                {
                    b.Step("clear", MatrixOps.Edit(ranged, "Computer", "Not Configured", press: new[] { "OK" }));
                }

                return b.Step("two", MatrixOps.Edit(ranged, "Computer", state2, options: Number(typed))).Build();
            }

            Expect(UnnoticedEdit.PlanProblems(Make(typed: false, clearBetween: false), Plan().Fixture).Count == 1, "the unkeyed ranged set on an Enabled policy was not a problem");
            Expect(UnnoticedEdit.PlanProblems(Make(typed: true, clearBetween: false), Plan().Fixture).Count == 0, "a typed set was a problem");
            Expect(UnnoticedEdit.PlanProblems(Make(typed: false, clearBetween: true), Plan().Fixture).Count == 0, "a set after Not Configured was a problem");

            var other = new CaseBuilder("E", "PlanCheck", "made up")
                .Step("one", MatrixOps.Edit(plain, "Computer", "Enabled"))
                .Step("two", MatrixOps.Edit(plain, "Computer", "Enabled", options: new List<OptionSpec> { OptionChoices.Base(plain, plain.Elements[0]) with { Text = "x" } }))
                .Build();
            Expect(UnnoticedEdit.PlanProblems(other, Plan().Fixture).Count == 0, "a text box set was a problem");

            var firstOnly = new CaseBuilder("E", "PlanCheck", "made up").Step("one", MatrixOps.Edit(ranged, "Computer", "Enabled", options: Number(typed: false))).Build();
            Expect(UnnoticedEdit.PlanProblems(firstOnly, Plan().Fixture).Count == 0, "the first step, which changes the state, was a problem");

            var noEarlierOp = new CaseBuilder("E", "PlanCheck", "made up").Step("one", MatrixOps.Edit(ranged, "Computer", options: Number(typed: false))).Build();
            Expect(UnnoticedEdit.PlanProblems(noEarlierOp, Plan().Fixture).Count == 0, "a policy with no earlier operation was judged");
        });

        Case("unnoticed edit: a Not Configured step on a soft decimal policy leaves it Enabled, so a later unkeyed set is a problem", () =>
        {
            PolicyInfo soft = Plan().Fixture.Policy("Pol_Decimal_Soft");
            ElementInfo softNumber = soft.Elements.Single(e => e.Soft && e.Kind == "decimal");
            Expect(UnnoticedEdit.NeedsKeys(softNumber), "the soft decimal has no range, so the plan check cannot apply to it");

            List<OptionSpec> Number(bool typed) => new() { OptionChoices.Base(soft, softNumber) with { Text = "50", Typed = typed ? true : null } };
            MatrixCase Make(bool clear, string? state2, bool typed)
            {
                var b = new CaseBuilder("E", "PlanCheck", "made up").Step("one", MatrixOps.Edit(soft, "Computer", "Enabled"));
                if (clear)
                {
                    b.Step("clear", MatrixOps.Edit(soft, "Computer", "Not Configured", press: new[] { "OK" }));
                }

                return b.Step("two", MatrixOps.Edit(soft, "Computer", state2, options: Number(typed))).Build();
            }

            Expect(UnnoticedEdit.PlanProblems(Make(clear: true, state2: "Enabled", typed: false), Plan().Fixture).Count == 1,
                "a Not Configured step on a soft decimal policy cleared the state, so a later unkeyed set on Enabled was not a problem");
            Expect(UnnoticedEdit.PlanProblems(Make(clear: true, state2: null, typed: false), Plan().Fixture).Count == 1,
                "a later unkeyed set with no state requested, after a Not Configured step on a soft decimal policy, was not a problem");
            Expect(UnnoticedEdit.PlanProblems(Make(clear: true, state2: "Enabled", typed: true), Plan().Fixture).Count == 0,
                "a typed set after a Not Configured step on a soft decimal policy was a problem");
            Expect(UnnoticedEdit.PlanProblems(Make(clear: false, state2: "Enabled", typed: false), Plan().Fixture).Count == 1,
                "the unkeyed set without the clear step was not a problem");
        });

        Case("unnoticed edit: the soft decimal case clears with Disabled, so its plan has no problem", () =>
        {
            MatrixCase c = Plan().Cases.Single(x => x.FullId == "E/Decimal_Soft_OutOfRange");
            Expect(!Plan().Problems.Any(p => p.StartsWith("E/Decimal_Soft_OutOfRange", StringComparison.Ordinal)), "the plan of the soft case has a problem");

            List<GpeditOp> edits = c.Ops.Where(o => o.Kind == "editPolicy").ToList();
            Expect(edits.Count == 3, $"the soft case has {edits.Count} edit steps, wanted 3 (above, the clear step, below)");
            Expect(edits[1].State == "Disabled", $"the step before 'below' is '{edits[1].State}', wanted Disabled");
            Expect(edits[1].Options is null, "the clear step before 'below' sets a value");
        });

        Case("unnoticed edit: the plan holds no such step, group B types the ranged numbers and five E cases clear between steps", () =>
        {
            Expect(Plan().Problems.Count == 0, "the plan has problems: " + string.Join("; ", Plan().Problems.Take(5)));

            int typedSteps = 0;
            foreach (MatrixCase c in Plan().Cases.Where(x => x.Group == "B"))
            {
                foreach (GpeditOp op in c.Ops.Where(o => o.Kind == "editPolicy" && o.Options is { Count: > 0 }))
                {
                    PolicyInfo policy = Plan().Fixture.Policies.Single(p => p.Name == op.PolicyId);
                    foreach (OptionSpec option in op.Options!)
                    {
                        ElementInfo element = policy.Elements.Single(e => e.Id == option.ElementId);
                        bool ranged = UnnoticedEdit.NeedsKeys(element);
                        Expect(ranged == (option.Typed is true && option.Text is not null && element.Kind is "decimal" or "longDecimal"), $"{c.FullId}: option '{option.Label}' is {(ranged ? "a ranged number that is not typed" : "typed without a range")}");
                        typedSteps += ranged ? 1 : 0;
                    }
                }
            }

            Expect(typedSteps == 6, $"{typedSteps} typed number steps in group B, wanted 6 (two class Both cases, MinMax, Soft, StoreAsText, LongDecimal)");

            foreach (string id in ClearedCases)
            {
                MatrixCase c = Plan().Cases.Single(x => x.FullId == id);
                List<GpeditOp> edits = c.Ops.Where(o => o.Kind == "editPolicy").ToList();
                int clears = edits.Count(o => o.State is "Not Configured" or "Disabled");
                Expect(clears == (edits.Count - clears) - 1, $"{id}: {clears} clear steps for {edits.Count - clears} value steps");
                Expect(edits.Where(o => o.Options is { Count: > 0 }).All(o => o.Options!.All(x => x.Typed is not true)), $"{id}: an out-of-range step is typed");
                for (int i = 1; i < edits.Count; i += 2)
                {
                    Expect(edits[i].State is "Not Configured" or "Disabled", $"{id}: the step before value step {(i / 2) + 2} is neither Not Configured nor Disabled");
                }
            }

            Expect(Plan().Cases.Single(x => x.FullId == "E/Decimal_TypedAboveMax").Ops.Count(o => o.Kind == "editPolicy") == 1, "the typed case changed");
        });
    }
}
