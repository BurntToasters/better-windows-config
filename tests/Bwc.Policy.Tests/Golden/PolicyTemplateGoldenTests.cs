using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// What the template loader does with each bad template of group H (docs/failure-modes/policy-admx.md, section
/// 51). The captures
/// install one bad template next to the good fixture and record the error box at start, the children of
/// Administrative
/// Templates, whether the bad category could be selected and whether the good fixture still shows its seven
/// categories. The
/// tests read those answers from the captures, then load the same two folders through <see cref="TemplateLoader"/>.
/// </summary>
[TestClass]
public sealed class PolicyTemplateGoldenTests
{
    private static readonly string[] AllCases =
    [
        "BoolAttributeMixed", "BoolAttributeUpper", "BooleanNoValueName", "ClassLowerCase", "DtdEntity", "DuplicatePolicy",
        "ListItemNoKey", "MalformedXml", "MissingAdml", "UndefinedString", "UndefinedUsing",
    ];

    private static readonly string[] RefusedCases = AllCases.Where(c => c != "UndefinedUsing").ToArray();

    public static IEnumerable<string[]> RefusedRows() => RefusedCases.Select(c => new[] { c });

    private static string NamespaceOf(string name) => "BWC.Policies.Bad." + name;

    private static string CategoryOf(string name) => "Bad fixture " + name;

    private static TemplateSet LoadBoth(string name) => TemplateLoader.Load([Fixture.Root, Fixture.Bad(name)], "en-US");

    private static bool UnderBad(string path, string name) =>
        path.StartsWith(Fixture.Bad(name) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static int BoxesAtStart(GoldenCase golden) =>
        (golden.Ops.Single(o => o.Index == 0).Data?["handled"] as System.Text.Json.Nodes.JsonArray)?.Count
        ?? throw new AssertFailedException($"{golden.Id}: operation 0 recorded no list of dialogs.");

    private static string[] ChildrenOf(GoldenCase golden, int op) =>
        (golden.Ops.Single(o => o.Index == op).Data?["children"] as System.Text.Json.Nodes.JsonArray)?.Select(c => c!.GetValue<string>()).ToArray()
        ?? throw new AssertFailedException($"{golden.Id}: operation {op} recorded no children.");

    private static bool Failed(GoldenCase golden, int op) =>
        golden.Ops.Single(o => o.Index == op).Observed?["ok"]?.GetValue<bool>() == false;

    [TestMethod]
    public void H7_TheCapturesRefuseExactlyTheTenNamedTemplatesAndAcceptUndefinedUsing()
    {
        var folders = Directory.GetDirectories(Path.Combine(GoldenRepo.Goldens, "H")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(AllCases.Order(StringComparer.Ordinal).ToArray(), folders, "The cases of group H changed.");

        var refused = new List<string>();
        foreach (var name in AllCases)
        {
            var golden = GoldenCase.Load("H/" + name);
            var boxes = BoxesAtStart(golden);
            var inTree = ChildrenOf(golden, 4).Contains(CategoryOf(name));
            Assert.AreEqual(boxes == 0, inTree, $"H/{name}: a box at start and a category in the tree disagree ({boxes} boxes, in tree {inTree}).");
            CollectionAssert.AreEqual(new[] { "Basics", "Elements", "Explain text", "Machine only", "Shared keys", "Sort cases", "Tools & Utilities" },
                ChildrenOf(golden, 6), $"H/{name}: the good fixture does not show its seven categories.");
            if (boxes > 0)
            {
                CollectionAssert.AreEqual(ChildrenOf(golden, 4), ChildrenOf(golden, 5), $"H/{name}: the user side shows other children than the computer side.");
                Assert.AreEqual(1, boxes, $"H/{name}: more than one box at start.");
                Assert.IsTrue(Failed(golden, 7) && Failed(golden, 8) && Failed(golden, 9), $"H/{name}: selecting, listing or opening the refused template did not fail.");
                refused.Add(name);
            }
        }

        CollectionAssert.AreEquivalent(RefusedCases, refused, "The set of templates classic refused is not the ten named in policy-admx.md section 51.");
    }

    [TestMethod]
    [DynamicData(nameof(RefusedRows))]
    public void H1_H3_ARefusedTemplateGivesFailuresInItsFolderAndAddsNothing(string name)
    {
        var set = LoadBoth(name);
        Assert.IsTrue(set.Failures.Any(f => UnderBad(f.FilePath, name)), $"H/{name}: classic refused the template and the loader reported no failure for it.");
        var outside = set.Failures.Where(f => !UnderBad(f.FilePath, name)).Select(f => f.FilePath).ToArray();
        Assert.AreEqual(0, outside.Length, $"H/{name}: failures were attributed to files outside the bad folder: {string.Join(", ", outside)}");

        var ns = NamespaceOf(name);
        Assert.IsNull(set.FindFile(ns), $"H/{name}: the file of a refused template is in the set.");
        Assert.AreEqual(0, set.Files.Count(f => f.Namespace == ns), $"H/{name}: the file is listed.");
        Assert.AreEqual(0, set.Categories.Count(c => c.Namespace == ns), $"H/{name}: a category of the refused template is in the set.");
        Assert.AreEqual(0, set.Policies.Count(p => p.Namespace == ns), $"H/{name}: a policy of the refused template is in the set.");
        Assert.AreEqual(0, set.SupportedOn.Count(s => s.Namespace == ns), $"H/{name}: a supported-on definition of the refused template is in the set.");
    }

    [TestMethod]
    [DynamicData(nameof(RefusedRows))]
    public void H2_H5_TheGoodFixtureAndItsRootAreTheSameNextToARefusedTemplate(string name)
    {
        var alone = Fixture.Load();
        Assert.AreEqual(0, alone.Failures.Count, "The good fixture alone must load without failures.");
        var both = LoadBoth(name);

        CollectionAssert.AreEqual(alone.Files.Select(f => f.Namespace).ToArray(), both.Files.Select(f => f.Namespace).ToArray(), $"H/{name}: the files of the good fixture changed.");
        CollectionAssert.AreEqual(alone.Categories.Select(c => c.Namespace + ":" + c.Name).ToArray(), both.Categories.Select(c => c.Namespace + ":" + c.Name).ToArray(), $"H/{name}: the categories of the good fixture changed.");
        CollectionAssert.AreEqual(alone.Policies.Select(p => p.FullName).ToArray(), both.Policies.Select(p => p.FullName).ToArray(), $"H/{name}: the policies of the good fixture changed.");
        CollectionAssert.AreEqual(alone.RootCategories.Select(c => c.DisplayName).ToArray(), both.RootCategories.Select(c => c.DisplayName).ToArray(), $"H/{name}: the roots of the tree changed.");
        Assert.IsFalse(both.RootCategories.Any(c => c.DisplayName == CategoryOf(name)), $"H/{name}: the category of a refused template is a root of the tree.");
    }

    [TestMethod]
    public void H6_UndefinedUsing_KeepsTheFileItsCategoryAndItsPolicy()
    {
        var set = LoadBoth("UndefinedUsing");
        var ns = NamespaceOf("UndefinedUsing");
        Assert.IsNotNull(set.FindFile(ns), "Classic showed no box and its category; the file must stay loaded.");
        Assert.IsTrue(set.Categories.Any(c => c.Namespace == ns && c.DisplayName == CategoryOf("UndefinedUsing")), "The category is not in the set.");
        var policy = set.Policies.SingleOrDefault(p => p.Namespace == ns);
        Assert.IsNotNull(policy, "The policy is not in the set.");
        Assert.AreEqual("Bad fixture policy", policy.DisplayName);
        Assert.AreEqual("BWC oracle bad fixture", policy.SupportedOnText);
        Assert.IsTrue(set.RootCategories.Any(c => c.DisplayName == CategoryOf("UndefinedUsing")), "Classic lists the category beside BWC Oracle at the root of the tree.");
    }

    [TestMethod]
    public void H6_UndefinedUsing_DeclaresADifferenceFromClassic_TheLoaderReportsTheLinkFailureClassicDoesNotShow()
    {
        // Difference named: classic shows no box for an unknown using; the loader reports one UndefinedUsing failure and
        // keeps the file
        // (policy-admx.md, section 35). The failure is for the UI to show as a note, never to hide the template.
        var set = LoadBoth("UndefinedUsing");
        Assert.IsTrue(set.Failures.All(f => f.Code == TemplateFailureCode.UndefinedUsing && UnderBad(f.FilePath, "UndefinedUsing")),
            $"Only link-level failures of the bad folder may appear: {string.Join(", ", set.Failures.Select(f => f.Code))}");
    }

    [TestMethod]
    public void H4_AllTenRefusedTemplatesTogetherDoNotHideTheGoodFixtureOrEachOther()
    {
        var folders = new List<string> { Fixture.Root };
        folders.AddRange(RefusedCases.Select(Fixture.Bad));
        var set = TemplateLoader.Load(folders, "en-US");
        var alone = Fixture.Load();
        CollectionAssert.AreEqual(alone.Policies.Select(p => p.FullName).ToArray(), set.Policies.Select(p => p.FullName).ToArray(),
            "BWC rule: one bad template does not hide the rest, and ten bad ones add nothing.");
        foreach (var name in RefusedCases)
        {
            Assert.IsTrue(set.Failures.Any(f => UnderBad(f.FilePath, name)), $"H/{name}: no failure was reported when all templates load together.");
        }

        Assert.AreEqual(0, set.Failures.Count(f => RefusedCases.All(n => !UnderBad(f.FilePath, n))), "A failure was attributed to a file outside the bad folders.");
    }
}
