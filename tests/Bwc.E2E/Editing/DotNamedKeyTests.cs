using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Editing;

/// <summary>
/// A key named "." under a sandbox key (docs/failure-modes/tree.md, "Key names . and .."). The registry lists it, so the
/// tree must show it, selecting it must show its value in the list, and it must not be hidden. The key is created with
/// the sandbox helper, because the managed API accepts the name only as a single component.
/// </summary>
[TestClass]
public sealed class DotNamedKeyTests
{
    private const string ParentKey = "DotParent";
    private const string DotName = ".";
    private const string DotValueName = "DotValue";
    private const string DotValueData = "dot-data";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Navigation")]
    [TestCategory("Mutating")]
    public void Editing_DotNamedKeyShowsItsValue()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey(ParentKey);
            sandbox.CreateEnumeratedStringKey(ParentKey, DotName, DotValueName, DotValueData);
            E2EResults.Record("dot.seed", true, "Created the key '.' under the parent with one string value.");

            using var session = StartApp(config, sandbox, "dot-named");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, ParentKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "dot.sandbox.navigate");

            AutomationElement? parentRow = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                ParentKey);
            bool expanded = ControlTestSupport.Expand(parentRow);
            AutomationElement? dotRow = null;
            bool listed = expanded && ControlTestSupport.WaitUntil(
                () => (dotRow = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    DotName)) is not null,
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "dot.tree-row",
                listed,
                $"parent expanded={expanded}, the tree lists '{DotName}' under '{ParentKey}'={listed}.");

            bool selected = dotRow is not null && ControlTestSupport.WaitUntil(
                () =>
                {
                    ControlTestSupport.Select(dotRow);
                    return ControlTestSupport.IsSelected(dotRow);
                },
                TimeSpan.FromSeconds(10));
            E2EResults.Record("dot.selected", selected, $"the '{DotName}' node is selected={selected}.");

            bool valueListed = selected && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(DotValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "dot.list-value",
                valueListed,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}], '{DotValueName}' shown={valueListed}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope)
    {
        string dataRoot = config.DataRootFor(scope);
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }

        Directory.CreateDirectory(dataRoot);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    private static void FailIfNeeded(int startIndex)
    {
        var failures = E2EResults.FailedChecksSince(startIndex);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Dot-named key checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
