using System.Drawing;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Hex;

/// <summary>
/// The hex area of Edit Binary Value has the classic RT_MENU 108 menu: Cut, Copy, Paste, Delete, a separator, and
/// Select All. The enable states follow the selection and the editable state (K1a, CTX-036 to CTX-041).
/// </summary>
[TestClass]
public sealed class HexContextMenuTests
{
    private const string BinaryValueName = "HexMenuBinary";
    private const string HexAutomationId = "HexEditor";
    private static readonly string[] HexMenuNames = ["Cut", "Copy", "Paste", "Delete", "Select All"];
    private static readonly byte[] Pattern = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(10);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Hex")]
    [TestCategory("ContextMenus")]
    [TestCategory("Mutating")]
    public void HexContextMenu_HasTheRt108ItemsAndStates()
    {
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        try
        {
            sandbox.Cleanup();
            sandbox.SetBinary(string.Empty, BinaryValueName, Pattern);

            dataRoot = HexEditingTests.CreateFreshDataRoot(config, "hexmenu");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var menus = new ContextMenuDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "hexmenu.sandbox.navigate");

            bool opened = HexEditingTests.OpenEditor(session, driver, BinaryValueName);
            AutomationElement? editor = opened ? driver.WaitForElement(HexAutomationId) : null;
            E2EResults.Record("hexmenu.open", editor is not null, $"Edit Binary Value opened={editor is not null}.");
            if (editor is null)
            {
                Assert.Fail("The hex editor did not open, so the hex menu was not measured.");
            }

            // The dialog is the foreground window, so the right click is sent at a byte of the first row.
            Rectangle area = editor!.BoundingRectangle;
            Mouse.RightClick(new Point(area.Left + 40, area.Top + 20));
            MenuSnapshot? menu = menus.WaitForMenu(HexMenuNames, MenuTimeout);
            string[] names = menu?.Names.ToArray() ?? Array.Empty<string>();
            bool namesOk = names.SequenceEqual(HexMenuNames);
            bool statesOk = menu is not null &&
                menu.Find(24) is { Enabled: false } &&
                menu.Find(3) is { Enabled: false } &&
                menu.Find(46) is { Enabled: false } &&
                menu.Find(1024) is { Enabled: true };
            bool itemsPass = namesOk && statesOk;
            E2EResults.Record(
                "hexmenu.items",
                itemsPass,
                $"Hex menu=[{string.Join(",", names)}]; with no selection Cut, Copy, and Delete are disabled and Select All is enabled={statesOk}.");

            // The menu and then the dialog are closed with Escape. The dialog is the foreground window now.
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            bool closed = driver.WaitForNoDialog(TimeSpan.FromSeconds(10));
            E2EResults.Record("hexmenu.close", closed, $"Escape closed the hex dialog={closed}.");
            Assert.IsTrue(itemsPass && closed, $"Hex menu=[{string.Join(",", names)}], closed={closed}.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            HexEditingTests.DeleteDataRoot(dataRoot);
        }
    }
}
