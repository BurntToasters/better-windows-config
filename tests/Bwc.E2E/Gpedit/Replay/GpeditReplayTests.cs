using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.Policy.Tests.Golden;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// Replays the captures of classic <c>gpedit.msc</c> through the window of the editor (piece G4, dispatch T): the
/// policy is
/// opened, the state, the options and the comment are set and OK, Apply or Cancel is pressed as the capture's structured
/// operation records, in a fresh sandbox policy object seeded with the case's start files. After every recorded
/// snapshot the
/// five files of the sandbox must equal the capture's step folder byte for byte, and a file that classic did not
/// write must
/// not exist. The failure modes are in docs/failure-modes/gpedit-dialog.md, section 15. Each test writes
/// <c>artifacts/e2e/visual/gpedit/gpedit-replay-&lt;name&gt;.json</c>: the size and SHA-256 of every file of every
/// step, as
/// captured and as saved. The cases are read with the case reader of the policy tests (linked, not referenced),
/// which checks
/// the golden files against case.json first.
/// </summary>
[TestClass]
public sealed class GpeditReplayTests
{
    /// <summary>One case per element kind of group B, none of them in awaiting-recapture.txt (CAP-4).</summary>
    private static readonly string[] ElementKindCases =
    [
        "B/Pol_Bool_StringValues",
        "B/Pol_Text_Plain",
        "B/Pol_Text_ComboBoxSuggestions",
        "B/Pol_Decimal_Required",
        "B/Pol_LongDecimal_RequiredSoftText",
        "B/Pol_Enum_StringItems",
        "B/Pol_MultiText_Basic",
        "B/Pol_List_Plain",
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Replay_GroupA()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var folders = Directory.GetDirectories(Path.Combine(GoldenRepo.Goldens, "A")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.IsTrue(folders.Length >= 19, $"group A holds {folders.Length} cases; the replay must not pass on an empty or damaged capture folder.");
        ReplayCases("groupa", folders.Select(f => "A/" + f).ToArray());
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Replay_GroupB_ElementKinds()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var awaiting = File.ReadAllLines(GoldenRepo.AwaitingList)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
        var stale = ElementKindCases.Where(awaiting.Contains).ToArray();
        Assert.AreEqual(0, stale.Length, $"These replay cases wait for a recapture and must not be replayed: {string.Join(", ", stale)}.");
        ReplayCases("groupb-kinds", ElementKindCases);
    }

    private static void ReplayCases(string name, string[] caseIds)
    {
        var checks = new GpeditChecks("replay", name);
        var report = new ReplayReport(name);
        foreach (var caseId in caseIds)
        {
            var golden = GoldenCase.Load(caseId);
            var scope = ReplaySandbox.ScopeOf("replay", caseId);
            var sandbox = ReplaySandbox.Create(scope);
            sandbox.SeedStart(golden);
            try
            {
                using var run = GpeditRun.Open(scope, sandbox.Sandbox);
                var replayed = new ReplayDriver(run, sandbox, checks, report).Run(golden);
                checks.Record($"{caseId}.replayed", replayed, replayed ? "every snapshot equals the capture." : "a snapshot differs from the capture; the replay of this case stopped there.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                checks.Record($"{caseId}.replayed", false, $"the replay stopped with {ex.GetType().Name}: {ex.Message}");
            }
        }

        _ = report.Write();
        checks.AssertAllPassed();
    }
}
