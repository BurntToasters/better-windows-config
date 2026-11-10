using FlaUI.Core.Definitions;

namespace Bwc.Spike.Interop.E2E;

[TestClass]
public sealed class PermissionsTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Permissions_Dialog_Is_Owned_By_Main_Window_And_Cancel_Closes_It()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "permissions"), "main window must be foreground before clicks");
        var keyPresent = S4Results.Record("permissions.registry-key-created", SpikeRegistry.Exists(),
            @"HKCU\Software\BWC-Spike-S4 exists at startup");

        // Mouse click: aclui!EditSecurity runs a nested modal loop, so the click must not block the test thread.
        var button = s.Find("PermissionsButton")!;
        TestSupport.ClickWhenStable(button);
        bool IsPermissionsTitle(string title) =>
            title.Contains("BWC-Spike-S4", StringComparison.OrdinalIgnoreCase) || title.Contains("Permissions", StringComparison.OrdinalIgnoreCase);
        var dlg = TestSupport.WaitForOwnWindow(s, IsPermissionsTitle, cls => cls == "#32770", TimeSpan.FromSeconds(4));
        if (dlg == 0)
        {
            // One retry (see ModalDialogTests.OpenModal): a click before the button is in place can be lost.
            TestSupport.ClickWhenStable(button);
            dlg = TestSupport.WaitForOwnWindow(s, IsPermissionsTitle, cls => cls == "#32770", Settle);
        }
        var ok = keyPresent;
        ok &= S4Results.Record("permissions.dialog-exists", dlg != 0,
            $"#32770 dialog hwnd={dlg:X} title='{(dlg == 0 ? "" : Native.TitleOf(dlg))}'");
        if (dlg == 0)
        {
            Assert.Fail("permissions dialog did not appear");
        }

        var owner = Native.OwnerOf(dlg);
        ok &= S4Results.Record("permissions.owner-is-main-window", owner == s.MainHwnd,
            $"GetWindow(GW_OWNER)={owner:X} main={s.MainHwnd:X}");

        var mainEnabled = Native.IsWindowEnabled(s.MainHwnd) != 0;
        ok &= S4Results.Record("permissions.owner-disabled-while-open", !mainEnabled,
            $"IsWindowEnabled(main) while dialog open={mainEnabled}");

        // Close with the dialog's Cancel button (not Escape), after verifying it is foreground.
        var dlgElement = s.Automation.FromHandle(dlg);
        var cancelButton = dlgElement.FindFirstDescendant(cf => cf.ByName("Cancel").And(cf.ByControlType(ControlType.Button)));
        ok &= S4Results.Record("permissions.cancel-button-found", cancelButton is not null, "Cancel button in permissions dialog");
        TestSupport.ForegroundFor(dlg, "permissions.cancel");
        cancelButton?.Click();

        var gone = TestSupport.WaitWindowGone(dlg, Settle);
        ok &= S4Results.Record("permissions.cancel-closes", gone, $"dialog gone after Cancel={gone}");

        var enabledAgain = GuiSession.WaitUntil(() => Native.IsWindowEnabled(s.MainHwnd) != 0, Settle);
        S4Results.Record("permissions.post-cancel-windows", true,
            $"process {s.ProcessState()}; own top-level windows after Cancel: " + DescribeOwnWindows(s));
        ok &= S4Results.Record("permissions.owner-enabled-after", enabledAgain, $"IsWindowEnabled(main) after close={enabledAgain}");

        var hrText = GuiSession.WaitUntil(() => s.TextOf("PermissionsResult").StartsWith("EditSecurity hr=0x", StringComparison.Ordinal), Settle);
        ok &= S4Results.Record("permissions.returned", hrText, $"PermissionsResult='{s.TextOf("PermissionsResult")}'");

        Assert.IsTrue(ok, "permissions checks failed; see out/s4-result.json");
    }

    private static string DescribeOwnWindows(GuiSession s)
    {
        var parts = new List<string>();
        foreach (var hwnd in Native.TopLevelWindows())
        {
            if (Native.ProcessIdOf(hwnd) != (uint)s.ProcessId) continue;
            parts.Add($"{hwnd:X} class='{Native.ClassOf(hwnd)}' title='{Native.TitleOf(hwnd)}' visible={Native.IsWindowVisible(hwnd) != 0} enabled={Native.IsWindowEnabled(hwnd) != 0} owner={Native.OwnerOf(hwnd):X}");
        }
        return string.Join(" | ", parts);
    }
}
