using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Controls;

/// <summary>
/// Drives the details list in the shipped Registry Editor against a seeded HKCU sandbox: real
/// values, sorting, Ctrl multi-select, the F2 rename cancel path, and the measured tree and list
/// row heights at 200 percent scale.
/// </summary>
[TestClass]
public sealed class ListTests
{
    private const double ExpectedTreeRowPx = 34;
    private const double ExpectedListRowPx = 36;
    private const double HeightTolerancePx = 1;

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Controls")]
    public void List_SortsMultiSelectsAndCancelsRename()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("controls.list.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        try
        {
            ControlTestSupport.SeedRegistry(config, wideChildCount: 0);

            using var session = new AppSession(
                config,
                config.RegeditExePath,
                environment: config.DataRootEnvironment("controls-list"));

            var list = session.Find(ControlTestSupport.ListControl);
            bool found = list is not null;
            E2EResults.Record("controls.list.found", found, found ? "ListControl is present." : "ListControl is missing.");
            if (!found)
            {
                FailIfNeeded();
                return;
            }

            var treeRow0 = session.Find("TreeRow_0");
            double treeHeight = ControlTestSupport.HeightOf(treeRow0);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record(
                "controls.list.navigate",
                navigated,
                $"navigated to '{config.RunId}' through expand and type-ahead={navigated}.");

            bool valuesOk = ControlTestSupport.WaitUntil(
                () => ContainsAll(
                    ControlTestSupport.ListRowNames(session),
                    "(Default)",
                    "AlphaString",
                    "EmptyString",
                    "MultiString",
                    "SampleBinary",
                    "SampleDword",
                    "ZebraString"),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "controls.list.values.real",
                valuesOk,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            var row0 = session.Find("ListRow_0");
            bool row0Ok = row0 is not null && !string.IsNullOrEmpty(ControlTestSupport.SafeName(row0));
            E2EResults.Record(
                "controls.list.row0",
                row0Ok,
                $"name='{ControlTestSupport.SafeName(row0)}' id='{ControlTestSupport.SafeId(row0)}'.");

            double listHeight = ControlTestSupport.HeightOf(row0);
            bool treeHeightOk = Math.Abs(treeHeight - ExpectedTreeRowPx) <= HeightTolerancePx;
            bool listHeightOk = Math.Abs(listHeight - ExpectedListRowPx) <= HeightTolerancePx;
            E2EResults.Record(
                "controls.list.row.heights",
                treeHeightOk && listHeightOk,
                $"treePx={treeHeight:0.##} (expected {ExpectedTreeRowPx}), listPx={listHeight:0.##} (expected {ExpectedListRowPx}), tolerance {HeightTolerancePx}.");

            var header = session.Find("HeaderName");
            bool headerFound = header is not null;
            E2EResults.Record("controls.list.header.name", headerFound, $"HeaderName present={headerFound}.");

            _ = ControlTestSupport.EnsureForeground(session);
            bool asc = false;
            if (header is not null)
            {
                try
                {
                    header.Click();
                    asc = ControlTestSupport.WaitUntil(
                        () => string.Equals(ControlTestSupport.SafeName(session.Find("ListRow_0")), "(Default)", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    E2EResults.Record("controls.list.sort.asc.error", false, ex.Message);
                }
            }

            E2EResults.Record(
                "controls.list.sort.asc",
                asc,
                $"first row after Name click='{ControlTestSupport.SafeName(session.Find("ListRow_0"))}', expected '(Default)'.");

            bool desc = false;
            header = session.Find("HeaderName");
            if (header is not null)
            {
                try
                {
                    header.Click();
                    desc = ControlTestSupport.WaitUntil(
                        () => string.Equals(ControlTestSupport.SafeName(session.Find("ListRow_0")), "ZebraString", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    E2EResults.Record("controls.list.sort.desc.error", false, ex.Message);
                }
            }

            E2EResults.Record(
                "controls.list.sort.desc",
                desc,
                $"first row after second Name click='{ControlTestSupport.SafeName(session.Find("ListRow_0"))}', expected 'ZebraString'.");

            var first = session.Find("ListRow_0");
            var second = session.Find("ListRow_1");
            ControlTestSupport.Select(first);
            _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(session.Find("ListRow_0")), TimeSpan.FromSeconds(3));

            first = session.Find("ListRow_0");
            second = session.Find("ListRow_1");
            _ = ControlTestSupport.EnsureForeground(session);
            using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
            {
                if (second is not null)
                {
                    second.Click();
                }
            }

            bool multi = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(session.Find("ListRow_0")) &&
                    ControlTestSupport.IsSelected(session.Find("ListRow_1")),
                TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "controls.list.select.ctrl",
                multi,
                $"row0 selected={ControlTestSupport.IsSelected(session.Find("ListRow_0"))}, row1 selected={ControlTestSupport.IsSelected(session.Find("ListRow_1"))}.");

            ControlTestSupport.Select(session.Find("ListRow_0"));
            _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(session.Find("ListRow_0")), TimeSpan.FromSeconds(3));

            ControlTestSupport.SendKeys(session, "F2");
            bool renameOpened = ControlTestSupport.WaitUntil(
                () => session.Find("ListRenameBox") is not null,
                TimeSpan.FromSeconds(5));
            E2EResults.Record("controls.list.rename.open", renameOpened, "F2 opened ListRenameBox.");

            ControlTestSupport.SendKeys(session, "Escape");
            bool renameClosed = ControlTestSupport.WaitUntil(
                () => session.Find("ListRenameBox") is null,
                TimeSpan.FromSeconds(5));
            E2EResults.Record("controls.list.rename.cancel", renameClosed, "Escape closed ListRenameBox.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static bool ContainsAll(IReadOnlyList<string> names, params string[] expected)
    {
        foreach (string name in expected)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Control checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
