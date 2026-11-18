using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class TemplateSetTests
{
    private static TemplateSet fixture = null!;

    [ClassInitialize]
    public static void Init(TestContext context) => fixture = Fixture.Load();

    [TestMethod]
    public void Fixture_LoadsWithoutFailures_AndWithTheDocumentedCounts()
    {
        FailureAssert.None(fixture);
        Assert.AreEqual(2, fixture.Files.Count);
        Assert.AreEqual(70, fixture.Policies.Count);
        Assert.AreEqual(18, fixture.Categories.Count);
        Assert.AreEqual(4, fixture.SupportedOn.Count);
        Assert.AreEqual(57, fixture.Policies.Count(p => p.Class == PolicyClass.Machine));
        Assert.AreEqual(11, fixture.Policies.Count(p => p.Class == PolicyClass.User));
        Assert.AreEqual(2, fixture.Policies.Count(p => p.Class == PolicyClass.Both));
        Assert.AreEqual(70, fixture.Policies.Count(p => p.Category is not null));
    }

    [TestMethod]
    public void Files_AreInFileNameOrder_AndRecordNamespaceAndUsings()
    {
        Assert.AreEqual("BwcOracle.admx", Path.GetFileName(fixture.Files[0].AdmxPath));
        Assert.AreEqual("BwcOracleBase.admx", Path.GetFileName(fixture.Files[1].AdmxPath));
        Assert.AreEqual(0, fixture.Files[0].Order);
        Assert.AreEqual(1, fixture.Files[1].Order);
        Assert.AreEqual(Fixture.OracleNamespace, fixture.Files[0].Namespace);
        Assert.AreEqual("bwcoracle", fixture.Files[0].Prefix);
        Assert.AreEqual(new NamespaceUsing("bwcbase", Fixture.BaseNamespace), fixture.Files[0].Usings.Single());
        Assert.AreEqual(0, fixture.Files[1].Usings.Count);
    }

    [TestMethod]
    public void Tree_RootIsInAnotherFileThatComesLaterInSourceOrder()
    {
        var root = fixture.RootCategories.Single();
        Assert.AreEqual(Fixture.BaseNamespace, root.Namespace);
        Assert.AreEqual("BwcOracle", root.Name);
        Assert.IsTrue(root.IsRoot);
        Assert.IsFalse(root.IsDetached);
        Assert.IsNull(root.Parent);

        var basics = fixture.Category("Basics");
        Assert.AreSame(root, basics.Parent);
        Assert.AreEqual("bwcbase:BwcOracle", basics.ParentRef);
        Assert.IsTrue(basics.File.Order < root.File.Order);
    }

    [TestMethod]
    public void Tree_ChildrenOfTheRoot_AreInDocumentOrder()
    {
        var root = fixture.RootCategories.Single();
        CollectionAssert.AreEqual(
            new[] { "Basics", "Elements", "Shared", "SortCases", "Ampersand", "Explain", "MachineOnly", "UserOnly", "EmptyCategory" },
            root.Children.Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Booleans", "Numbers", "Texts", "Choices", "Lists" },
            fixture.Category("Elements").Children.Select(c => c.Name).ToArray());
        Assert.AreEqual("Pol_Top_DirectUnderRoot", root.Policies.Single().Name);
    }

    [TestMethod]
    public void Tree_NestedChains_ResolveThroughSameFileReferences()
    {
        var deep = fixture.Category("MachineDeep");
        Assert.AreEqual("MachineNested", deep.Parent!.Name);
        Assert.AreEqual("MachineOnly", deep.Parent.Parent!.Name);
        Assert.AreEqual("BwcOracle", deep.Parent.Parent.Parent!.Name);
        Assert.AreEqual("UserOnly", fixture.Category("UserNested").Parent!.Name);
        Assert.AreEqual(0, fixture.Category("UserOnly").Policies.Count);
        Assert.AreEqual("Pol_Nested_UserDeep", fixture.Category("UserNested").Policies.Single().Name);
        Assert.AreEqual(0, fixture.Category("EmptyCategory").Policies.Count);
        Assert.AreEqual(0, fixture.Category("EmptyCategory").Children.Count);
    }

    [TestMethod]
    public void Tree_EveryCategoryReachesTheRoot_AndPolicyListsCoverEveryPolicy()
    {
        foreach (var category in fixture.Categories)
        {
            var current = category;
            var steps = 0;
            while (current.Parent is not null)
            {
                current = current.Parent;
                Assert.IsTrue(++steps <= fixture.Categories.Count);
            }

            Assert.IsTrue(current.IsRoot, category.Name);
        }

        Assert.AreEqual(fixture.Policies.Count, fixture.Categories.Sum(c => c.Policies.Count));
    }

    [TestMethod]
    public void SupportedOn_ResolvesAcrossFilesAndInTheSameFile()
    {
        Assert.AreEqual(
            "BWC oracle fixture: any system",
            fixture.Policy("Pol_Class_Machine").SupportedOnText);
        var other = fixture.Policy("Pol_Class_Machine").SupportedOn!;
        Assert.AreEqual(Fixture.BaseNamespace, other.Namespace);
        Assert.AreEqual("SUPPORTED_BwcAny", other.Name);

        var local = fixture.Policy("Pol_Both_WithElements").SupportedOn!;
        Assert.AreEqual(Fixture.OracleNamespace, local.Namespace);
        Assert.AreEqual("SUPPORTED_LocalOnly", local.Name);
        Assert.AreEqual("BWC oracle fixture: defined in the same file as the policy", fixture.Policy("Pol_Both_WithElements").SupportedOnText);
        Assert.AreEqual("BWC oracle fixture: newer systems only", fixture.Policy("Pol_Class_User").SupportedOnText);
    }

    [TestMethod]
    public void Lookups_IgnoreCase_AndMissReturnNull()
    {
        Assert.IsNotNull(fixture.FindPolicy("bwc.policies.oracle", "POL_CLASS_BOTH"));
        Assert.IsNotNull(fixture.FindCategory("BWC.POLICIES.ORACLEBASE", "bwcoracle"));
        Assert.IsNotNull(fixture.FindFile("bwc.policies.oracle"));
        Assert.IsNotNull(fixture.FindSupportedOn("bwc.policies.oraclebase", "supported_bwcany"));
        Assert.IsNull(fixture.FindPolicy("BWC.Policies.Oracle", "Nope"));
        Assert.IsNull(fixture.FindPolicy("Nope", "Pol_Class_Both"));
    }

    [TestMethod]
    public void Policy_FullNameIsNamespaceColonName_AndDisplayCaseIsKept()
    {
        var policy = fixture.FindPolicy("bwc.policies.oracle", "pol_class_both")!;
        Assert.AreEqual("BWC.Policies.Oracle:Pol_Class_Both", policy.FullName);
        Assert.AreEqual("Pol_Class_Both", policy.Name);
    }

    [TestMethod]
    public void Load_TwiceGivesIdenticalOrderingAndFailures()
    {
        var again = Fixture.Load();
        CollectionAssert.AreEqual(fixture.Policies.Select(p => p.FullName).ToArray(), again.Policies.Select(p => p.FullName).ToArray());
        CollectionAssert.AreEqual(
            fixture.Categories.Select(c => c.Namespace + ":" + c.Name + ":" + c.Parent?.Name).ToArray(),
            again.Categories.Select(c => c.Namespace + ":" + c.Name + ":" + c.Parent?.Name).ToArray());
        CollectionAssert.AreEqual(fixture.SupportedOn.Select(s => s.Name).ToArray(), again.SupportedOn.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(fixture.Failures.ToArray(), again.Failures.ToArray());
    }

    [TestMethod]
    public void Load_AFolderTwice_ShadowsTheSecondCopyAndKeepsTheFirst()
    {
        using var temp = new TempTemplates();
        CopyFixture(Path.Combine(temp.Root, "second"));
        var set = TemplateLoader.Load([Fixture.Root, Path.Combine(temp.Root, "second")]);
        Assert.AreEqual(2, set.Files.Count);
        Assert.AreEqual(70, set.Policies.Count);
        Assert.AreEqual(2, set.Failures.Count(f => f.Code == TemplateFailureCode.NamespaceShadowed));
        Assert.AreEqual(2, set.Failures.Count);
        Assert.IsTrue(set.Failures.All(f => f.FilePath.StartsWith(Path.Combine(temp.Root, "second"), StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(set.Files.All(f => f.SourceIndex == 0));
    }

    [TestMethod]
    public void Load_TwoFilesWithOneNamespaceInOneFolder_IsADuplicateNotShadowed()
    {
        using var temp = new TempTemplates();
        var policy = """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""";
        temp.AddSimple("A", "T.Same", policy, """<string id="P">A</string>""");
        temp.AddSimple("B", "t.same", policy, """<string id="P">B</string>""");
        var set = temp.Load();
        Assert.AreEqual(1, set.Files.Count);
        Assert.AreEqual("A", set.Policies.Single().DisplayName);
        var failure = FailureAssert.Single(set, TemplateFailureCode.DuplicateNamespace);
        Assert.AreEqual("B.admx", Path.GetFileName(failure.FilePath));
    }

    [TestMethod]
    public void Load_SourcesKeepTheOrderGiven()
    {
        var policy = """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""";
        var one = new TempTemplates();
        var two = new TempTemplates();
        try
        {
            one.AddSimple("Zzz", "T.One", policy, """<string id="P">One</string>""");
            two.AddSimple("Aaa", "T.Two", policy, """<string id="P">Two</string>""");
            var set = TemplateLoader.Load([one.Root, two.Root]);
            CollectionAssert.AreEqual(new[] { "T.One", "T.Two" }, set.Files.Select(f => f.Namespace).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 1 }, set.Files.Select(f => f.SourceIndex).ToArray());
        }
        finally
        {
            one.Dispose();
            two.Dispose();
        }
    }

    [TestMethod]
    public void Load_NoFoldersOrAnEmptyFolder_GivesAnEmptySetWithoutFailures()
    {
        var none = TemplateLoader.Load([]);
        Assert.AreEqual(0, none.Policies.Count);
        FailureAssert.None(none);
        using var temp = new TempTemplates();
        var empty = temp.Load();
        Assert.AreEqual(0, empty.Files.Count);
        FailureAssert.None(empty);
        Assert.AreEqual(0, TemplateSet.Empty.Policies.Count);
    }

    [TestMethod]
    public void Load_MissingFolder_IsOneFailureAndTheOtherFoldersStillLoad()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "scratch", "does-not-exist-" + Guid.NewGuid().ToString("N"));
        var set = TemplateLoader.Load([missing, Fixture.Root]);
        Assert.AreEqual(70, set.Policies.Count);
        var failure = FailureAssert.Single(set, TemplateFailureCode.FolderNotFound);
        Assert.AreEqual(missing, failure.FilePath);
        Assert.AreEqual(1, set.Failures.Count);
    }

    [TestMethod]
    public void Load_NonTemplateFilesAndSubFolders_AreIgnored()
    {
        using var temp = new TempTemplates();
        temp.Write("readme.txt", "hello");
        temp.Write("notes.admx.bak", "<x/>");
        temp.Write(Path.Combine("sub", "Deep.admx"), "<policyDefinitions/>");
        var set = temp.Load();
        Assert.AreEqual(0, set.Files.Count);
        FailureAssert.None(set);
    }

    [TestMethod]
    public void Load_CancelledToken_ThrowsOperationCanceled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => TemplateLoader.Load([Fixture.Root], "en-US", source.Token));
    }

    [TestMethod]
    public void Load_NullFolders_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => TemplateLoader.Load(null!));
    }

    [TestMethod]
    public void Link_UndefinedPrefix_LeavesTheReferenceUnresolved()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Prefix",
            "T.Prefix",
            """
            <policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="nope:Root" /><supportedOn ref="nope:SUP" /></policy>
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        Assert.AreEqual(2, set.Failures.Count(f => f.Code == TemplateFailureCode.UndefinedPrefix && f.Detail == "nope"));
        Assert.IsNull(set.Policies.Single().Category);
        Assert.IsNull(set.Policies.Single().SupportedOn);
        Assert.AreEqual(string.Empty, set.Policies.Single().SupportedOnText);
    }

    [TestMethod]
    public void Link_PrefixCaseDoesNotMatter()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "PrefixCase",
            "T.PrefixCase",
            """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="T:ROOT" /><supportedOn ref="t:sup" /></policy>""",
            """<string id="P">P</string>""");
        var set = temp.Load();
        FailureAssert.None(set);
        Assert.IsNotNull(set.Policies.Single().Category);
        Assert.IsNotNull(set.Policies.Single().SupportedOn);
    }

    [TestMethod]
    public void Link_CategoryOrSupportedOnNotFound_IsRecorded()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Ghost",
            "T.Ghost",
            """
            <policy name="Lost" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Nowhere" /><supportedOn ref="Nothing" /></policy>
            <policy name="Bare" class="Machine" displayName="$(string.P)" key="K" />
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        Assert.AreEqual(2, set.Policies.Count);
        Assert.IsTrue(set.Policies.All(p => p.Category is null));
        var lost = set.FindPolicy("T.Ghost", "Lost")!;
        var missingCategory = set.Failures.Single(f => f.Code == TemplateFailureCode.UndefinedCategory);
        Assert.AreEqual("Nowhere", missingCategory.Detail);
        Assert.AreEqual(lost.Line, missingCategory.Line);
        Assert.IsTrue(missingCategory.Line > 0);
        Assert.AreEqual("Nothing", FailureAssert.Single(set, TemplateFailureCode.UndefinedSupportedOn).Detail);
        Assert.AreEqual("Bare", FailureAssert.Single(set, TemplateFailureCode.MissingCategory).Detail);
        Assert.IsNull(set.FindPolicy("T.Ghost", "Bare")!.SupportedOn);
    }

    [TestMethod]
    public void Link_CategoryWithUnknownParent_IsDetachedAndNotAChild()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Detached",
            "T.Detached",
            string.Empty,
            extraCategories: """<category name="Orphan" displayName="$(string.Root)"><parentCategory ref="Missing" /></category>""");
        var set = temp.Load();
        var orphan = set.FindCategory("T.Detached", "Orphan")!;
        Assert.IsTrue(orphan.IsDetached);
        Assert.IsFalse(orphan.IsRoot);
        Assert.IsNull(orphan.Parent);
        Assert.AreEqual(1, set.RootCategories.Count);
        Assert.AreEqual("Root", set.RootCategories[0].Name);
        Assert.AreEqual(0, set.RootCategories[0].Children.Count);
        Assert.AreEqual("Missing", FailureAssert.Single(set, TemplateFailureCode.UndefinedCategory).Detail);
    }

    [TestMethod]
    public void Link_CategoryCycle_IsCutAtTheFirstMemberInSourceOrder()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Cycle",
            "T.Cycle",
            string.Empty,
            extraCategories: """
            <category name="A" displayName="$(string.Root)"><parentCategory ref="C" /></category>
            <category name="B" displayName="$(string.Root)"><parentCategory ref="A" /></category>
            <category name="C" displayName="$(string.Root)"><parentCategory ref="B" /></category>
            <category name="Tail" displayName="$(string.Root)"><parentCategory ref="B" /></category>
            <category name="Self" displayName="$(string.Root)"><parentCategory ref="Self" /></category>
            """);
        var set = temp.Load();
        var cycles = set.Failures.Where(f => f.Code == TemplateFailureCode.CategoryCycle).Select(f => f.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "A", "Self" }, cycles);

        var a = set.FindCategory("T.Cycle", "A")!;
        Assert.IsTrue(a.IsDetached);
        Assert.AreSame(a, set.FindCategory("T.Cycle", "B")!.Parent);
        Assert.AreSame(set.FindCategory("T.Cycle", "B"), set.FindCategory("T.Cycle", "Tail")!.Parent);
        Assert.IsTrue(set.FindCategory("T.Cycle", "Self")!.IsDetached);
        foreach (var category in set.Categories)
        {
            var current = category;
            var steps = 0;
            while (current.Parent is not null)
            {
                current = current.Parent;
                Assert.IsTrue(++steps <= set.Categories.Count, "parent chain loops");
            }
        }
    }

    [TestMethod]
    public void Link_ParentCategoryInAnotherFileWithoutUsing_IsAnUndefinedPrefix()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("Base", "T.Base", string.Empty);
        temp.Add(
            "Child",
            "T.Child",
            "c",
            """<categories><category name="Kid" displayName="$(string.K)"><parentCategory ref="t:Root" /></category></categories>""",
            """<string id="K">Kid</string>""");
        var set = temp.Load();
        Assert.IsTrue(set.FindCategory("T.Child", "Kid")!.IsDetached);
        Assert.AreEqual("t", FailureAssert.Single(set, TemplateFailureCode.UndefinedPrefix).Detail);
    }

    [TestMethod]
    public void Link_ParentCategoryInAnotherFileThroughUsing_Resolves()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("Base", "T.Base", string.Empty);
        temp.Add(
            "Child",
            "T.Child",
            "c",
            """
            <categories><category name="Kid" displayName="$(string.K)"><parentCategory ref="b:Root" /></category></categories>
            <policies><policy name="P" class="User" displayName="$(string.K)" key="K"><parentCategory ref="Kid" /><supportedOn ref="b:SUP" /></policy></policies>
            """,
            """<string id="K">Kid</string>""",
            usings: """<using prefix="b" namespace="t.base" />""");
        var set = temp.Load();
        FailureAssert.None(set);
        var kid = set.FindCategory("T.Child", "Kid")!;
        Assert.AreEqual("T.Base", kid.Parent!.Namespace);
        Assert.AreSame(kid, kid.Parent.Children.Single());
        Assert.AreSame(kid, set.Policies.Single().Category);
        Assert.AreEqual("Sup", set.Policies.Single().SupportedOnText);
    }

    private static void CopyFixture(string target)
    {
        Directory.CreateDirectory(Path.Combine(target, "en-US"));
        foreach (var file in Directory.EnumerateFiles(Fixture.Root))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Fixture.Root, "en-US")))
        {
            File.Copy(file, Path.Combine(target, "en-US", Path.GetFileName(file)));
        }
    }
}
