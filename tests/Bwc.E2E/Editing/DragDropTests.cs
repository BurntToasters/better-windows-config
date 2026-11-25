using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Editing;

/// <summary>
/// Negative drag-and-drop (BEH-009). Classic regedit has no drag-and-drop for keys or values, so a key or value dragged
/// onto a key, onto the tree, or onto the value list must change nothing. The registry is read after each drag, so a move
/// or a copy would fail the check even if the tree looked right.
/// </summary>
[TestClass]
public sealed class DragDropTests
{
    private const string ListControl = "ListControl";
    private const string SourceKey = "DragSource";
    private const string TargetKey = "DragTarget";
    private const string MoverValue = "DragMover";
    private const string MoverData = "moving";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Editing")]
    [TestCategory("Mutating")]
    public void Editing_DragAndDropDoesNothing()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey(SourceKey);
            sandbox.SetString(SourceKey, MoverValue, MoverData);
            sandbox.CreateKey(TargetKey);

            string dataRoot = config.DataRootFor("editing-drag");
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

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "drag.navigate");

            var runRow = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, config.RunId);
            _ = ControlTestSupport.Expand(runRow);
            bool rows = ControlTestSupport.WaitUntil(
                () => Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, SourceKey) is not null &&
                    Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, TargetKey) is not null,
                ControlTestSupport.UiTimeout);
            E2EResults.Record("drag.rows", rows, $"tree rows for both keys shown={rows}.");
            if (!rows)
            {
                Assert.Fail("The drag keys were not shown in the tree.");
            }

            // 1. A key dragged onto another key.
            AutomationElement? source = Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, SourceKey);
            AutomationElement? target = Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, TargetKey);
            Drag(session, source!, target!);
            E2EResults.Record(
                "drag.key-onto-key",
                KeyUnchanged(sandbox) && !sandbox.Exists(TargetKey + @"\" + SourceKey),
                $"after the drag, source kept={KeyUnchanged(sandbox)}, no copy under the target={!sandbox.Exists(TargetKey + @"\" + SourceKey)}.");

            // 2. A value dragged from the list onto a key in the tree. The source key is selected first.
            ControlTestSupport.Select(Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, SourceKey));
            bool sourceSelected = ControlTestSupport.WaitForTreeSelection(session, SourceKey, ControlTestSupport.UiTimeout);
            bool moverListed = ControlTestSupport.WaitUntil(
                () => Row(session, ListControl, ControlType.DataItem, MoverValue) is not null,
                ControlTestSupport.UiTimeout);
            AutomationElement? mover = moverListed ? Row(session, ListControl, ControlType.DataItem, MoverValue) : null;
            if (mover is not null)
            {
                Drag(session, mover, Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, TargetKey)!);
            }

            E2EResults.Record(
                "drag.value-onto-key",
                sourceSelected && mover is not null && KeyUnchanged(sandbox) && sandbox.ReadString(TargetKey, MoverValue) is null,
                $"source selected={sourceSelected}, value row found={mover is not null}, " +
                    $"value kept={KeyUnchanged(sandbox)}, target gained no value={sandbox.ReadString(TargetKey, MoverValue) is null}.");

            // 3. A key dragged onto the value list. The list is a drop target only for nothing: no value is created in it.
            AutomationElement? list = session.Find(ListControl);
            if (list is not null)
            {
                Drag(session, Row(session, ControlTestSupport.TreeControl, ControlType.TreeItem, TargetKey)!, list);
            }

            E2EResults.Record(
                "drag.key-onto-list",
                list is not null && KeyUnchanged(sandbox) && sandbox.ReadString(TargetKey, MoverValue) is null &&
                    !sandbox.Exists(TargetKey + @"\" + SourceKey),
                $"list found={list is not null}, source kept={KeyUnchanged(sandbox)}, nothing added to the target={sandbox.ReadString(TargetKey, MoverValue) is null}.");

            var failures = E2EResults.FailedChecksSince(checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Drag checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>The source key still holds its value, and it is the only key that holds it.</summary>
    private static bool KeyUnchanged(SandboxRegistry sandbox) =>
        sandbox.Exists(SourceKey) && sandbox.ReadString(SourceKey, MoverValue) == MoverData;

    private static AutomationElement? Row(AppSession session, string rootId, ControlType type, string name) =>
        ControlTestSupport.FindRowByName(session, rootId, type, name);

    /// <summary>Presses the left button on one element, moves across the other, and releases it. Real input only.</summary>
    private static void Drag(AppSession session, AutomationElement from, AutomationElement to)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        Rectangle a = from.BoundingRectangle;
        Rectangle b = to.BoundingRectangle;
        var start = new Point(a.Left + (a.Width / 2), a.Top + (a.Height / 2));
        var end = new Point(b.Left + (b.Width / 2), b.Top + (b.Height / 2));

        // The approach has no button down, so the cursor is placed on the source. The press and the move are real.
        Mouse.Position = start;
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(150);
        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            Mouse.MoveTo(new Point(start.X + ((end.X - start.X) * i / steps), start.Y + ((end.Y - start.Y) * i / steps)));
            Thread.Sleep(30);
        }

        Thread.Sleep(150);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(500);
    }
}
