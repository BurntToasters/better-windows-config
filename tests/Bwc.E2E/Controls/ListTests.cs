using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Controls;

/// <summary>
/// Drives the details list in the shipped Registry Editor against a seeded HKCU sandbox: real
/// values, the classic order with a header click that does not reorder, Ctrl multi-select, the F2
/// rename cancel path, and the measured tree and list row heights at the window scale. The expected
/// heights come from the oracle metrics file. The behaviors beyond these are in tests/Bwc.E2E/List.
/// </summary>
[TestClass]
public sealed class ListTests
{
    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    public void List_HeaderClickKeepsOrderMultiSelectsAndCancelsRename()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
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
            var sandbox = ControlTestSupport.SeedRegistry(config, wideChildCount: 0);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("controls-list"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(
                config,
                config.RegeditExePath,
                environment: environment);

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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "controls.list.sandbox");

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
            uint dpi = ControlTestSupport.WindowDpi(session);
            var oracle = OracleMetrics.Load(config);
            double expectedTree = oracle.DevicePixels(oracle.TreeItemHeight, dpi);
            double expectedList = oracle.DevicePixels(oracle.ListRowHeight, dpi);
            int tolerance = OracleMetrics.ToleranceDevicePixels(dpi);
            bool treeHeightOk = Math.Abs(treeHeight - expectedTree) <= tolerance;
            bool listHeightOk = Math.Abs(listHeight - expectedList) <= tolerance;
            E2EResults.Record(
                "controls.list.row.heights",
                treeHeightOk && listHeightOk,
                $"treePx={treeHeight:0.##} (expected {expectedTree:0.##}, oracle treeItemHeight {oracle.TreeItemHeight}), " +
                $"listPx={listHeight:0.##} (expected {expectedList:0.##}, oracle listRowHeight {oracle.ListRowHeight}), " +
                $"tolerance {tolerance} px (1 DIP) at {dpi} DPI, source {oracle.Describe()}.");

            var header = session.Find("HeaderName");
            bool headerFound = header is not null;
            E2EResults.Record("controls.list.header.name", headerFound, $"HeaderName present={headerFound}.");

            if (header is not null)
            {
                try
                {
                    // The click is a mouse input, so it is sent only after the window is confirmed foreground.
                    if (!ControlTestSupport.EnsureForeground(session))
                    {
                        throw new InvalidOperationException("The Registry Editor window did not come to the foreground, so the header was not clicked.");
                    }

                    // Click the header bounds with the OS mouse path. UI Automation reports no clickable
                    // point for a header whose cell is partly clipped, but its visible rectangle is real.
                    AutomationElement? current = session.Find("HeaderName");
                    System.Drawing.Rectangle bounds = (current ?? header).BoundingRectangle;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        throw new InvalidOperationException($"HeaderName has no clickable bounds (width={bounds.Width}, height={bounds.Height}).");
                    }

                    session.RaiseWindows();
                    Mouse.Click(new System.Drawing.Point(
                        (int)(bounds.Left + (bounds.Width / 2)),
                        (int)(bounds.Top + (bounds.Height / 2))));
                    Thread.Sleep(300);
                }
                catch (Exception ex)
                {
                    E2EResults.Record("controls.list.header.error", false, ex.Message);
                }
            }

            // Classic does not sort on a header click (docs/parity/oracle/sort-order.json: changed=false).
            string firstAfterClick = ControlTestSupport.SafeName(session.Find("ListRow_0"));
            bool headerNoReorder = string.Equals(firstAfterClick, "(Default)", StringComparison.Ordinal);
            E2EResults.Record(
                "controls.list.header.no-reorder",
                headerNoReorder,
                $"first row after Name click='{firstAfterClick}', expected '(Default)' (no sort).");

            var first = session.Find("ListRow_0");
            var second = session.Find("ListRow_1");
            ControlTestSupport.Select(first);
            _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(session.Find("ListRow_0")), TimeSpan.FromSeconds(3));

            first = session.Find("ListRow_0");
            second = session.Find("ListRow_1");

            // Ctrl+click is sent only while the window is confirmed foreground. Without it the multi-select check
            // below fails on its own, as a recorded check.
            if (ControlTestSupport.EnsureForeground(session))
            {
                using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
                {
                    if (second is not null)
                    {
                        second.Click();
                    }
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

            // Row 0 is the default value, which cannot be renamed. Row 1 is AlphaString.
            ControlTestSupport.Select(session.Find("ListRow_1"));
            _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(session.Find("ListRow_1")), TimeSpan.FromSeconds(3));

            E2EGuards.RequireListRowIdSelected(session, "ListRow_1", "controls.list.rename.row");
            E2EGuards.RequireSandboxSelection(session, sandbox, "controls.list.rename");
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
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
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
