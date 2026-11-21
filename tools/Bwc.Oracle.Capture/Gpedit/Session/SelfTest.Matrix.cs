using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The matrix decisions that need no classic: how a refused start is judged, what a refused golden holds, that no timing or dialog text
// reaches a golden, and that --resume decides before any session. Failure modes 33 (driver document) and 20 (matrix document).
// These cases were written before the code they check.
internal static partial class SelfTest
{
    private const string Marker = "ZZ-MARKER-dialog-text-4711";

    private static void MatrixCases(RepoPaths repo, GpeditPaths paths, Action<string, Action> Case)
    {
        MatrixPlan? plan = null;
        MatrixPlan Plan() => plan ??= MatrixPlan.Build(repo);
        string goldens = Path.Combine(paths.OutputDir, "matrix-golden-test");

        Case("refusal: only a case with crafted start files allows it", () =>
        {
            MatrixCase refusable = Plan().Cases.Single(c => c.FullId == "K/KeyLeadingBackslash_ReadAsWritten");
            Expect(refusable.StartMayBeRefused, "a crafted read case of group K does not allow a refused start");
            Expect(Plan().Cases.Where(c => c.Group == "D").All(c => c.StartMayBeRefused), "a crafted case of group D does not allow a refused start");
            Expect(Plan().Cases.Where(c => c.Group == "K" && c.Id.EndsWith("_Enable", StringComparison.Ordinal)).All(c => !c.StartMayBeRefused), "an Enable case of group K allows a refused start");
            foreach (string group in new[] { "A", "B", "C", "E", "F", "G", "H" })
            {
                Expect(Plan().Cases.Where(c => c.Group == group).All(c => !c.StartMayBeRefused), $"a case of group {group} allows a refused start");
            }
        });

        Case("refusal: decided by class, a live process, no tree and a visible dialog", () =>
        {
            var visibleDialog = Win(1, "#32770", visible: true, width: 300);
            Expect(RefusedStart.Dialogs(true, true, false, new[] { visibleDialog }).Count == 1, "a visible #32770 with no tree and a live mmc is not a refusal");
            Expect(RefusedStart.Dialogs(false, true, false, new[] { visibleDialog }).Count == 0, "a refusal was found for a case that must start");
            Expect(RefusedStart.Dialogs(true, false, false, new[] { visibleDialog }).Count == 0, "a refusal was found with mmc gone");
            Expect(RefusedStart.Dialogs(true, true, true, new[] { visibleDialog }).Count == 0, "a refusal was found although the tree appeared");
            Expect(RefusedStart.Dialogs(true, true, false, Array.Empty<WinInfo>()).Count == 0, "a timeout with no window is a refusal");
            Expect(RefusedStart.Dialogs(true, true, false, new[] { Win(2, "MMCMainFrame", true, 800) }).Count == 0, "the main frame alone is a refusal");
            Expect(RefusedStart.Dialogs(true, true, false, new[] { Win(3, "#32770", visible: false, width: 300) }).Count == 0, "an invisible dialog is a refusal");
            Expect(RefusedStart.Dialogs(true, true, false, new[] { Win(4, "#32770", visible: true, width: 0) }).Count == 0, "an empty dialog is a refusal");
            Expect(RefusedStart.Dialogs(true, true, false, new[] { Win(5, "WindowsForms10.Window.8.app", true, 300) }).Count == 0, "a window that is not #32770 is a refusal");
            Expect(RefusedStart.Dialogs(true, true, false, new[] { Win(6, "MMCMainFrame", true, 800), visibleDialog }).Count == 1, "the dialog next to the main frame was not found");
        });

        Case("refusal: a crafted case is complete with its result, and its golden holds no classic text", () =>
        {
            MatrixCase c = Plan().Cases.Single(x => x.FullId == "K/KeyLeadingBackslash_ReadAsWritten");
            JsonObject result = RefusedResult(c);
            Expect(CaseOutput.Evaluate(c, result, out _, out string? why), "a refused crafted case did not evaluate as complete: " + why);

            ResetDir(goldens);
            var scrub = new Scrubber(Plan().Fixture.AllowedStrings, c.Authored, c.KeepRegistry);
            CaseOutcome outcome = CaseOutput.Write(goldens, c, result, scrub, 0);
            Expect(outcome.Complete, "the refused golden was not written: " + outcome.Failure);
            Expect(CaseOutput.IsComplete(goldens, c), "a refused golden is not complete, so --resume would retry it");

            string dir = CaseOutput.CaseDir(goldens, c);
            JsonNode caseJson = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "case.json")))!;
            Expect(caseJson["startRefused"]?.GetValue<bool>() == true, "case.json does not say that classic refused to start");
            Expect(caseJson["startFilesUnchanged"]?.GetValue<bool>() == true, "case.json does not say the files equal the start files");
            Expect(caseJson["observed"]!.AsArray().All(o => o?["skipped"]?.GetValue<bool>() == true && o["ok"]?.GetValue<bool>() == false), "an observed operation is not recorded as skipped");
            Expect(File.Exists(Path.Combine(dir, "start", "Machine.Registry.pol")), "the start file is not in the golden");
            Expect(File.Exists(Path.Combine(dir, "Machine.Registry.pol")), "the file after close is not in the golden");
            ExpectNoText(dir, Marker, "a dialog text reached the golden");
            ExpectNoText(dir, "startRefusal", "the untracked refusal object reached the golden");
            foreach (string key in new[] { "\"phases\"", "\"hopPhases\"", "\"timing\"", "findTreeMs" })
            {
                ExpectNoText(dir, key, "a timing field reached the golden");
            }

            // The files changed by classic: still complete, and the golden says so.
            JsonObject changed = RefusedResult(c);
            changed["filesAfterRefusal"] = Snapshot(new[] { new StartFile(c.Start.Files[0].Name, new byte[] { 1, 2, 3 }) });
            CaseOutput.Write(goldens, c, changed, scrub, 0);
            JsonNode again = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "case.json")))!;
            Expect(again["startFilesUnchanged"]?.GetValue<bool>() == false, "changed files are reported as unchanged");
            FileOps.DeleteTree(goldens);
        });

        Case("refusal: a failed start stays failed unless the case allows a refusal", () =>
        {
            MatrixCase normal = Plan().Cases.First(c => !c.StartMayBeRefused);
            MatrixCase crafted = Plan().Cases.Single(c => c.FullId == "K/KeyLeadingBackslash_ReadAsWritten");
            Expect(!CaseOutput.Evaluate(normal, RefusedResult(normal), out _, out _), "a refused start of a normal case evaluated as complete");

            JsonObject timeout = RefusedResult(crafted);
            timeout.Remove("startRefused");
            timeout["startError"] = "no console tree within 30 seconds";
            Expect(!CaseOutput.Evaluate(crafted, timeout, out _, out _), "a start error without a refusal evaluated as complete");

            JsonObject ran = RefusedResult(crafted);
            ran["ops"]!.AsArray()[0]!.AsObject()["skipped"] = false;
            ran["ops"]!.AsArray()[0]!.AsObject()["ok"] = true;
            Expect(!CaseOutput.Evaluate(crafted, ran, out _, out _), "a refused result with an operation that ran evaluated as complete");

            JsonObject withError = RefusedResult(crafted);
            withError["startError"] = "x";
            Expect(!CaseOutput.Evaluate(crafted, withError, out _, out _), "a refusal that also holds a start error evaluated as complete");
        });

        Case("timing never reaches a golden", () =>
        {
            MatrixCase c = Plan().Cases.First(x => x.Group == "A" && !x.StartMayBeRefused);
            JsonObject result = new()
            {
                ["liveRegistryAtStart"] = new JsonObject(),
                ["timing"] = new JsonObject { ["classicStartMs"] = 605, ["treeReadyMs"] = 8573, ["warmUpMs"] = 16545 },
                ["warmUp"] = new JsonObject { ["roots"] = new JsonArray(new JsonObject { ["resolve"] = Hop() }) },
                ["ops"] = new JsonArray(),
            };
            JsonArray ops = result["ops"]!.AsArray();
            for (int i = 0; i < c.Ops.Count; i++)
            {
                JsonNode data = c.Ops[i].Kind == "snapshot"
                    ? new JsonObject { ["snapshot"] = Snapshot(Array.Empty<StartFile>()), ["liveRegistry"] = new JsonObject() }
                    : new JsonObject { ["node"] = "n", ["hops"] = new JsonArray("a", "b"), ["hopPhases"] = new JsonArray(Hop()), ["phases"] = new JsonArray(Hop()), ["listStable"] = true };
                ops.Add(new JsonObject
                {
                    ["index"] = i,
                    ["kind"] = c.Ops[i].Kind,
                    ["ok"] = true,
                    ["ms"] = 7,
                    ["data"] = data,
                    ["phases"] = new JsonArray(new JsonObject { ["name"] = "nodeSelection", ["ms"] = 29910 }),
                    ["hopPhases"] = new JsonArray(Hop()),
                });
            }

            Expect(CaseOutput.Evaluate(c, result, out int optional, out string? why), "the fake result did not evaluate: " + why);
            ResetDir(goldens);
            var scrub = new Scrubber(Plan().Fixture.AllowedStrings, c.Authored, c.KeepRegistry);
            CaseOutcome outcome = CaseOutput.Write(goldens, c, result, scrub, optional);
            Expect(outcome.Complete, "the golden was not written: " + outcome.Failure);
            string dir = CaseOutput.CaseDir(goldens, c);
            foreach (string key in new[] { "\"phases\"", "\"hopPhases\"", "\"timing\"", "findTreeMs", "pollCount", "waitIdleMs" })
            {
                ExpectNoText(dir, key, "a timing field reached the golden");
            }

            FileOps.DeleteTree(goldens);
        });

        Case("hop timing: json shape, caps and a failure that keeps the partial hop", () =>
        {
            var hop = new HopTimes("Local Computer Policy/Computer Configuration");
            hop.FindTreeMs = 5;
            HopStep step = hop.NextHop("Administrative Templates");
            step.WaitIdleMs = 3;
            step.WaitIdleReturnedIdle = true;
            step.Expand.HasPattern = true;
            step.Expand.Called = true;
            for (int i = 0; i < 50; i++)
            {
                step.AddChildItems(i);
            }

            JsonObject json = hop.ToJson()!;
            JsonObject first = json["hops"]!.AsArray()[0]!.AsObject();
            Expect(first["segment"]!.GetValue<string>() == "Administrative Templates", "the hop does not name its segment");
            Expect(first["waitIdleReturnedIdle"]!.GetValue<bool>(), "the idle answer is missing");
            Expect(first["expandCalled"]!.GetValue<bool>(), "the expand answer is missing");
            Expect(first["childItemsCalls"]!.GetValue<int>() == 50, "the number of ChildItems calls is wrong");
            Expect(first["childItemsMs"]!.AsArray().Count == 20, "the list of ChildItems times is not capped");
            Expect(first["childItemsTotalMs"]!.GetValue<long>() == Enumerable.Range(0, 50).Sum(), "the total of the capped list is wrong");
            Expect(new HopTimes("n").ToJson() is not null, "a hop list with no hop does not give json");
        });

        Case("resume: a complete case opens no session, setup or lock", () =>
        {
            List<MatrixCase> selected = Plan().Cases.Where(c => c.Group is "A" or "H" or "K").ToList();
            int hCount = selected.Count(c => c.Group == "H");
            int kCount = selected.Count(c => c.Group == "K");

            (int sessions, int skipped, List<string> names) Run(bool resume, Func<MatrixCase, bool> complete)
            {
                var names = new List<string>();
                var runner = new MatrixRunner(repo, Plan(), resume) { CompleteCheck = complete, Log = _ => { } };
                runner.RunGroups(selected, (name, _, _) => names.Add(name));
                return (names.Count, runner.Skipped, names);
            }

            (int s, int skipped, _) = Run(true, _ => true);
            Expect(s == 0, $"{s} sessions were opened although every case is complete");
            Expect(skipped == selected.Count, $"{skipped} cases counted as skipped, wanted {selected.Count}");

            string pending = "K/KeyLeadingBackslash_ReadAsWritten";
            (int one, int skippedOne, List<string> namesOne) = Run(true, c => c.FullId != pending);
            Expect(one == 1 && namesOne[0] == "matrix-K-KeyLeadingBackslash_ReadAsWritten", "one pending case of K did not give exactly its own session: " + string.Join(", ", namesOne));
            Expect(skippedOne == selected.Count - 1, "the other cases were not all skipped");

            MatrixCase[] aCases = selected.Where(c => c.Group == "A").Take(2).ToArray();
            (int a, _, List<string> namesA) = Run(true, c => !aCases.Contains(c));
            Expect(a == 1 && namesA[0] == "matrix-A", "two pending cases of A did not share one session: " + string.Join(", ", namesA));

            (int all, int none, _) = Run(false, _ => true);
            Expect(all == 1 + hCount + kCount, $"without --resume {all} sessions were opened, wanted {1 + hCount + kCount}");
            Expect(none == 0, "cases were skipped without --resume");
        });
    }

    private static WinInfo Win(int id, string cls, bool visible, int width) =>
        new(new IntPtr(id), cls, "title", visible, IntPtr.Zero, new NativeRect { Left = 10, Top = 10, Right = 10 + width, Bottom = 10 + (width > 0 ? 200 : 0) });

    private static JsonObject Hop() => new()
    {
        ["segment"] = "x",
        ["findTreeMs"] = 1,
        ["waitIdleMs"] = 2,
        ["pollCount"] = 3,
    };

    // A snapshot as OpsRunner records it: the five files with bytes, the listing and the gpt.ini version.
    private static JsonObject Snapshot(IReadOnlyList<StartFile> files)
    {
        var node = new JsonObject();
        foreach (string name in PolFiles.Names)
        {
            StartFile? file = files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            node[name] = file is null
                ? new JsonObject { ["exists"] = false }
                : new JsonObject
                {
                    ["exists"] = true,
                    ["size"] = (long)file.Bytes.Length,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant(),
                    ["hex"] = Convert.ToHexString(file.Bytes).ToLowerInvariant(),
                };
        }

        return new JsonObject
        {
            ["files"] = node,
            ["listing"] = new JsonArray(files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Name, ["size"] = f.Bytes.Length }).ToArray()),
            ["gptVersion"] = null,
        };
    }

    // What OpsRunner writes for a start that classic refused: no startError, every operation skipped, the dialog texts in startRefusal.
    private static JsonObject RefusedResult(MatrixCase c)
    {
        var ops = new JsonArray();
        for (int i = 0; i < c.Ops.Count; i++)
        {
            ops.Add(new JsonObject { ["index"] = i, ["kind"] = c.Ops[i].Kind, ["label"] = c.Ops[i].Label, ["ok"] = false, ["skipped"] = true, ["reason"] = Marker });
        }

        return new JsonObject
        {
            ["startRefused"] = true,
            ["startRefusal"] = new JsonObject
            {
                ["dialogs"] = new JsonArray(new JsonObject
                {
                    ["class"] = "#32770",
                    ["title"] = Marker,
                    ["controls"] = new JsonArray(new JsonObject { ["class"] = "Static", ["text"] = Marker }),
                }),
            },
            ["liveRegistryAtStart"] = new JsonObject(),
            ["timing"] = new JsonObject { ["classicStartMs"] = 605, ["treeReadyMs"] = 8573 },
            ["filesAfterRefusal"] = Snapshot(c.Start.Files),
            ["ops"] = ops,
        };
    }

    private static void ResetDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            FileOps.DeleteTree(dir);
        }

        Directory.CreateDirectory(dir);
    }

    private static void ExpectNoText(string dir, string text, string message)
    {
        foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            Expect(!File.ReadAllText(file).Contains(text, StringComparison.Ordinal), $"{message} ({Path.GetRelativePath(dir, file)} holds '{text}')");
        }
    }
}
