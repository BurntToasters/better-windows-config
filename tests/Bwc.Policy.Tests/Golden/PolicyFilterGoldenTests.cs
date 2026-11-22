using System.Text.Json.Nodes;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Filter;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The filter results of <c>F/Filter_Options</c> and the All Settings captures against <see cref="PolicyFilter"/>
/// (docs/failure-modes/policy-filter.md, section F). The four saves of the case are replayed through <see
/// cref="PolicySave"/>
/// in a sandbox; the state and comment the engine reads give the subjects; the options of each <c>setFilter</c>
/// operation are
/// read from the dialog the capture recorded after OK, and the display names the engine shows are compared with
/// the rows of
/// the list classic showed. The captured list is sorted and the engine keeps source order, so the names are
/// compared as sets.
/// </summary>
[TestClass]
public sealed class PolicyFilterGoldenTests
{
    private const string CaseId = "F/Filter_Options";

    /// <summary>The last filter (configured Yes, kept on): its list is read on another node (FC12), not on Sort
    /// cases.</summary>
    private const int LastFilterOp = 70;

    private static readonly Lazy<(IReadOnlyList<FilterSubject> Subjects, IReadOnlyList<FilterSubject> Basics)> Subjects = new(BuildSubjects);

    public static IEnumerable<object[]> FilterOps() =>
        GoldenCase.Load(CaseId).Ops.Where(o => o.Kind == "setFilter" && o.Index != LastFilterOp).Select(o => new object[] { o.Index, o.Text("label")! });

    private static (IReadOnlyList<FilterSubject>, IReadOnlyList<FilterSubject>) BuildSubjects()
    {
        var golden = GoldenCase.Load(CaseId);
        using var sandbox = new GoldenSandbox();
        var templates = GoldenReplay.LoadedTemplates;
        foreach (var op in golden.Ops.Where(o => o.Index < 4))
        {
            Assert.AreEqual("editPolicy", op.Kind, $"{CaseId} operation {op.Index}: the four saves come first.");
            var policy = templates.FindPolicy(op.Text("policyNamespace")!, op.Text("policyId")!)!;
            var edit = op.Text("state") switch
            {
                "Enabled" => PolicyEdit.Enabled(),
                "Disabled" => PolicyEdit.Disabled,
                _ => PolicyEdit.NotConfigured,
            };
            var outcome = PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, edit, op.Text("comment") ?? string.Empty);
            Assert.IsTrue(outcome.IsValid, $"{CaseId} operation {op.Index}: the save reports problems: {string.Join(", ", outcome.Problems)}");
        }

        var snapshot = sandbox.Store.Open();
        var pol = snapshot.Machine.Pol ?? throw new AssertFailedException($"{CaseId}: the engine wrote no Machine policy file.");
        FilterSubject SubjectOf(PolicyDefinition p) =>
            new(p, PolicyStateReader.Read(p, pol), snapshot.Machine.Comments?.GetComment(p.Namespace, p.Name));

        var sort = templates.Categories.Single(c => c.Namespace == Fixture.OracleNamespace && c.Name == "SortCases").Policies.Select(SubjectOf).ToList();
        var basics = templates.Categories.Single(c => c.Namespace == Fixture.OracleNamespace && c.Name == "Basics").Policies.Select(SubjectOf).ToList();
        return (sort, basics);
    }

    private static FilterTriState Tri(JsonNode? combo) =>
        combo?["selectedText"]?.GetValue<string>() switch
        {
            "Yes" => FilterTriState.Yes,
            "No" => FilterTriState.No,
            "Any" => FilterTriState.Any,
            var other => throw new AssertFailedException($"unknown choice '{other}' in the recorded dialog"),
        };

    private static bool Checked(JsonNode? box) => box?["checkState"]?.GetValue<int>() == 1;

    /// <summary>The options as the dialog stood after OK. The platform choice is mapped from the operation: no
    /// platform, or every
    /// platform of a machine the fixture does not know (keys no fixture policy uses).</summary>
    private static FilterOptions OptionsOf(GoldenOp op)
    {
        var after = op.Data?["after"] ?? throw new AssertFailedException($"{CaseId} operation {op.Index}: no dialog was recorded after OK.");
        var keyword = new KeywordFilter(
            Checked(after["keywordsEnabled"]),
            after["keywords"]?["text"]?.GetValue<string>() ?? string.Empty,
            after["match"]?["selectedText"]?.GetValue<string>() switch
            {
                "All" => KeywordMatchMode.All,
                "Exact" => KeywordMatchMode.Exact,
                _ => KeywordMatchMode.Any,
            },
            Checked(after["inTitle"]),
            Checked(after["inHelp"]),
            Checked(after["inComment"]));
        var requirements = Checked(after["requirementsEnabled"])
            ? new RequirementsFilter(true, op.Op["filter"]?["platforms"]?.GetValue<string>() == "all" ? ["stale-platform-one", "stale-platform-two"] : [], PlatformMatchMode.Any)
            : RequirementsFilter.Off;
        return new FilterOptions(Tri(after["managed"]), Tri(after["configured"]), Tri(after["commented"]), keyword, requirements);
    }

    private static string[] RowNames(GoldenCase golden, int index)
    {
        var data = golden.Ops.Single(o => o.Index == index).Data ?? throw new AssertFailedException($"{CaseId} operation {index}: no list was recorded.");
        if (data["nodeHidden"]?.GetValue<bool>() == true)
        {
            return [];
        }

        return (data["rows"] as JsonArray ?? new JsonArray()).Select(r => r!["name"]!.GetValue<string>()).ToArray();
    }

    [TestMethod]
    public void FC14_TheCaseHoldsSeventeenFilterOperations()
    {
        var ops = GoldenCase.Load(CaseId).Ops.Where(o => o.Kind == "setFilter").ToList();
        Assert.AreEqual(17, ops.Count, "The case must hold the 17 setFilter operations; a recapture that changes the number must extend the test.");
        Assert.IsTrue(ops.Where(o => o.Index != LastFilterOp).All(o => RowNamesExist(o.Index + 2)), "Every setFilter operation must be followed by a readList two operations later.");
    }

    private static bool RowNamesExist(int index) => GoldenCase.Load(CaseId).Ops.Any(o => o.Index == index && o.Kind == "readList");

    [TestMethod]
    public void FC0_TheEngineStateOfTheFourSavesShowsTheUnfilteredList()
    {
        var golden = GoldenCase.Load(CaseId);
        var expected = RowNames(golden, 5);
        Assert.AreEqual(8, expected.Length);
        CollectionAssert.AreEquivalent(expected, Subjects.Value.Subjects.Select(s => s.Policy.DisplayName).ToArray(), "The Sort cases node lists other policies than the capture.");
        var states = Subjects.Value.Subjects.ToDictionary(s => s.Policy.DisplayName, s => (s.State!.State, s.Comment));
        Assert.AreEqual(PolicyState.Enabled, states["Zulu sort case"].State);
        Assert.AreEqual(PolicyState.Disabled, states["alpha policy"].State);
        Assert.AreEqual(PolicyState.NotConfigured, states["Policy 10"].State);
        Assert.AreEqual("bwc comment one", states["Policy 10"].Comment);
        Assert.AreEqual(PolicyState.Enabled, states["Policy 2"].State);
        Assert.AreEqual("bwc comment two", states["Policy 2"].Comment);
    }

    [TestMethod]
    [DynamicData(nameof(FilterOps))]
    public void FC1_FC9_TheFilterShowsTheRowsClassicShowed(int index, string label)
    {
        var golden = GoldenCase.Load(CaseId);
        var op = golden.Ops.Single(o => o.Index == index);
        var options = OptionsOf(op);
        var expected = RowNames(golden, index + 2);
        var created = PolicyFilter.TryCreate(options, out var filter, out var problems);
        if (options.Requirements.Enabled && options.Requirements.PlatformKeys.Count == 0)
        {
            // FC10: classic answered with a message box and left the list unfiltered.
            Assert.IsFalse(created, $"{label}: no platform is selected and the filter was created.");
            CollectionAssert.Contains(problems.ToList(), FilterProblem.PlatformsMissing, $"{label}: the problem PlatformsMissing is missing.");
            Assert.AreEqual(8, expected.Length, $"{label}: classic kept the list unfiltered.");
            return;
        }

        Assert.IsTrue(created, $"{label}: the filter cannot be created: {string.Join(", ", problems)}");
        var shown = filter!.Apply(Subjects.Value.Subjects).Shown.Select(s => s.Policy.DisplayName).ToArray();
        CollectionAssert.AreEquivalent(expected, shown, $"{label} (operation {index}): classic showed [{string.Join("|", expected)}], the engine shows [{string.Join("|", shown)}].");
    }

    [TestMethod]
    public void FC12_AFilterThatHidesEveryPolicyOfACategoryCountsNone()
    {
        var golden = GoldenCase.Load(CaseId);
        var hidden = golden.Ops.Single(o => o.Index == 72).Data!;
        Assert.IsTrue(hidden["nodeHidden"]!.GetValue<bool>() && hidden["filterOn"]!.GetValue<bool>(), "Classic hid the Basics node under Configured Yes.");
        Assert.IsTrue(PolicyFilter.TryCreate(OptionsOf(golden.Ops.Single(o => o.Index == LastFilterOp)), out var filter, out _));
        var count = filter!.Count(Subjects.Value.Basics);
        Assert.IsTrue(count.Total > 0, "The Basics category lists no policy.");
        Assert.AreEqual(0, count.Shown, $"Classic hid the node; the engine shows {count.Shown} of {count.Total}.");
    }

    [TestMethod]
    [DataRow("F/AllSettings_Computer")]
    [DataRow("F/AllSettings_User")]
    public void FC13_AllSettingsCapturesHoldNoListToCompare(string caseId)
    {
        var golden = GoldenCase.Load(caseId);
        foreach (var op in golden.Ops.Where(o => o.Kind is "selectNode" or "readStatus" or "readList"))
        {
            Assert.AreEqual("depends on the machine's own templates", op.Data?["untracked"]?.GetValue<string>(),
                $"{caseId} operation {op.Index}: the capture now holds a result; extend PolicyFilterGoldenTests to compare it.");
        }

        var side = caseId.EndsWith("User", StringComparison.Ordinal) ? GpoSide.User : GpoSide.Machine;
        var all = AllSettings.List(GoldenReplay.LoadedTemplates, side);
        Assert.IsTrue(all.Count > 0, "The All Settings list of the fixture is empty.");
        Assert.IsTrue(all.All(p => side == GpoSide.Machine ? p.AppliesToMachine : p.AppliesToUser), "A policy of the other class is in the list.");
    }
}
