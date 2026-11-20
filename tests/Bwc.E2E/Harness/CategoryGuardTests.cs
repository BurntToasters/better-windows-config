using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Keeps every Gpedit and Console test out of the ship step of the full gate, because those tests drive seams that only
/// the E2E flavor has (docs/failure-modes/e2e-harness.md, section 45). A test belongs to the group when its class lives
/// in the Gpedit or Console folder of this project (by source file, by namespace) or carries the category Gpedit or
/// Console, or when a class in any other file of this project calls ListRun.Open, ShellRun.Open or GpeditRun.Open. A
/// new file in those folders cannot be missed. Source files are read from the repository, nothing is started.
/// </summary>
[TestClass]
public sealed class CategoryGuardTests
{
    private const string E2EOnly = "E2EOnly";
    private static readonly Regex SeamOpenCall = new(@"\b(?:ListRun|ShellRun|GpeditRun)\.Open\(", RegexOptions.CultureInvariant);
    private const string GuardCall = "E2EGuards.RequireE2EOnlyFlavor(";
    private static readonly string[] GroupFolders = ["Gpedit", "Console"];
    private static readonly string[] GroupCategories = ["Gpedit", "Console"];
    private static readonly string[] GroupNamespaces = ["Bwc.E2E.Gpedit", "Bwc.E2E.ConsoleFrame", "Bwc.E2E.Console"];

    // Tests that were run in the ship flavor and pass there. A test that needs a seam is E2EOnly and not listed here.
    private static readonly string[] ShipAllowlist =
    [
        "Gpedit_Smoke_WindowOpens",
        "Gpedit_Tree_SystemTemplates",
    ];

    [TestMethod]
    [TestCategory("Harness")]
    public void Category_GpeditAndConsoleTests_AreE2EOnlyOrAllowlisted()
    {
        int checkStart = E2EResults.CheckCount;
        List<(MethodInfo Method, string Id)> members = GroupMethods();

        E2EResults.Record(
            "category.guard.members",
            members.Count > 0,
            $"found {members.Count} test method(s) in the Gpedit and Console group.");

        var missing = members
            .Where(m => !HasE2EOnly(m.Method) && !ShipAllowlist.Contains(m.Method.Name, StringComparer.Ordinal))
            .Select(m => m.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        E2EResults.Record(
            "category.guard.e2eonly",
            missing.Count == 0,
            missing.Count == 0
                ? "every test of the group is E2EOnly or allowlisted."
                : $"{missing.Count} test(s) without E2EOnly and not allowlisted: {string.Join(", ", missing)}.");

        var stale = ShipAllowlist
            .Where(name => !members.Any(m => m.Method.Name == name) || members.Any(m => m.Method.Name == name && HasE2EOnly(m.Method)))
            .ToList();
        E2EResults.Record(
            "category.guard.allowlist",
            stale.Count == 0,
            stale.Count == 0
                ? $"the allowlist names {ShipAllowlist.Length} existing ship test(s)."
                : $"allowlist entries that no longer exist or are E2EOnly already: {string.Join(", ", stale)}.");

        Finish(checkStart, "Category guard failed");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Category_E2EOnlyTestsOfTheGroup_CallTheFlavorGuardFirst()
    {
        int checkStart = E2EResults.CheckCount;
        string root = GuiGate.Config.RepoRoot;
        Dictionary<string, string> sources = GroupSources(root);
        List<(MethodInfo Method, string Id)> members = GroupMethods()
            .Where(m => HasE2EOnly(m.Method))
            .ToList();

        var unguarded = new List<string>();
        foreach ((MethodInfo method, string id) in members)
        {
            string name = method.Name;
            string className = method.DeclaringType!.Name;
            string? text = sources.Values.FirstOrDefault(s => Regex.IsMatch(s, $@"\bclass\s+{Regex.Escape(className)}\b"));
            bool guarded = text is not null && Regex.IsMatch(
                text,
                $@"\b{Regex.Escape(name)}\s*\(\s*\)\s*\{{\s*{Regex.Escape(GuardCall)}");
            if (!guarded)
            {
                unguarded.Add(id);
            }
        }

        unguarded.Sort(StringComparer.Ordinal);
        E2EResults.Record(
            "category.guard.flavor-call",
            unguarded.Count == 0,
            unguarded.Count == 0
                ? $"{members.Count} E2EOnly test(s) of the group start with {GuardCall}...)."
                : $"{unguarded.Count} E2EOnly test(s) do not start with the flavor guard: {string.Join(", ", unguarded)}.");

        Finish(checkStart, "Category guard (flavor call) failed");
    }

    private static void Finish(int checkStart, string title)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(0, failures.Count, $"{title}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static bool HasE2EOnly(MethodInfo method) =>
        method.GetCustomAttributes<TestCategoryAttribute>(inherit: true)
            .Concat(method.DeclaringType!.GetCustomAttributes<TestCategoryAttribute>(inherit: true))
            .Any(a => a.TestCategories.Contains(E2EOnly, StringComparer.Ordinal));

    // Source text of every .cs file in the group folders, and of every other .cs file under tests/Bwc.E2E that opens a
    // console seam run, by relative path. The classes of those files belong to the group.
    private static Dictionary<string, string> GroupSources(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in GroupFolders)
        {
            string path = Path.Combine(root, "tests", "Bwc.E2E", folder);
            Assert.IsTrue(Directory.Exists(path), $"The test folder '{path}' does not exist.");
            foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                result[Path.GetRelativePath(root, file)] = File.ReadAllText(file);
            }
        }

        string project = Path.Combine(root, "tests", "Bwc.E2E");
        foreach (string file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (result.ContainsKey(relative) || parts.Contains("bin") || parts.Contains("obj"))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (SeamOpenCall.IsMatch(text))
            {
                result[relative] = text;
            }
        }

        return result;
    }

    // Every test method of the group with a readable id (Class.Method). The group is the union of the three rules.
    private static List<(MethodInfo Method, string Id)> GroupMethods()
    {
        Dictionary<string, string> sources = GroupSources(GuiGate.Config.RepoRoot);
        var folderClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in sources.Values)
        {
            foreach (Match match in Regex.Matches(text, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)"))
            {
                folderClasses.Add(match.Groups[1].Value);
            }
        }

        var list = new List<(MethodInfo, string)>();
        foreach (Type type in typeof(CategoryGuardTests).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<TestClassAttribute>(inherit: true) is null)
            {
                continue;
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<TestMethodAttribute>(inherit: true) is null)
                {
                    continue;
                }

                if (InGroup(type, method, folderClasses))
                {
                    list.Add((method, type.FullName + "." + method.Name));
                }
            }
        }

        return list;
    }

    private static bool InGroup(Type type, MethodInfo method, HashSet<string> folderClasses)
    {
        string ns = type.Namespace ?? string.Empty;
        if (GroupNamespaces.Any(g => ns == g || ns.StartsWith(g + ".", StringComparison.Ordinal)))
        {
            return true;
        }

        if (folderClasses.Contains(type.Name))
        {
            return true;
        }

        return method.GetCustomAttributes<TestCategoryAttribute>(inherit: true)
            .Concat(type.GetCustomAttributes<TestCategoryAttribute>(inherit: true))
            .Any(a => a.TestCategories.Any(c => GroupCategories.Contains(c, StringComparer.Ordinal)));
    }
}
