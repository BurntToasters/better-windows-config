using FlaUI.Core.Input;
using FlaUI.Core.Definitions;

namespace Bwc.Spike.Interop.E2E;

[TestClass]
public sealed class FileDialogTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Open_File_Dialog_Is_Owned_By_Main_Window_And_Escape_Reports_Cancelled()
    {
        using var s = GuiSession.Start();
        RunCommonDialogCancel(s, buttonId: "OpenFileButton", checkPrefix: "file.open", titleContains: null);
    }

    [TestMethod]
    public void Save_File_Dialog_Is_Owned_By_Main_Window_And_Escape_Reports_Cancelled()
    {
        using var s = GuiSession.Start();
        RunCommonDialogCancel(s, buttonId: "SaveFileButton", checkPrefix: "file.save", titleContains: null);
    }

    private static void RunCommonDialogCancel(GuiSession s, string buttonId, string checkPrefix, string? titleContains)
    {
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, checkPrefix), "main window must be foreground before clicks");

        // Mouse click: the common dialog runs a nested modal loop, so the click must not block the test thread.
        var button = s.Find(buttonId)!;
        TestSupport.ClickWhenStable(button);
        var dlg = TestSupport.WaitForOwnWindow(s,
            title => titleContains is null || title.Contains(titleContains, StringComparison.OrdinalIgnoreCase),
            cls => cls == "#32770",
            TimeSpan.FromSeconds(4));
        if (dlg == 0)
        {
            // One retry (see ModalDialogTests.OpenModal): a click before the button is in place can be lost.
            TestSupport.ClickWhenStable(button);
            dlg = TestSupport.WaitForOwnWindow(s,
                title => titleContains is null || title.Contains(titleContains, StringComparison.OrdinalIgnoreCase),
                cls => cls == "#32770",
                Settle);
        }
        var ok = S4Results.Record(checkPrefix + ".dialog-exists", dlg != 0,
            $"#32770 dialog of this process hwnd={dlg:X} title='{(dlg == 0 ? "" : Native.TitleOf(dlg))}'");
        if (dlg == 0)
        {
            Assert.Fail("common file dialog did not appear");
        }

        var owner = Native.OwnerOf(dlg);
        ok &= S4Results.Record(checkPrefix + ".owner-is-main-window", owner == s.MainHwnd,
            $"GetWindow(GW_OWNER)={owner:X} main={s.MainHwnd:X}");

        var mainEnabled = Native.IsWindowEnabled(s.MainHwnd) != 0;
        ok &= S4Results.Record(checkPrefix + ".owner-disabled-while-open", !mainEnabled,
            $"IsWindowEnabled(main) while dialog open={mainEnabled}");

        // Escape is only sent once the dialog is verified foreground. The dialog gets a moment to take keyboard focus;
        // one retry is allowed because the first Escape can be swallowed while the dialog is still settling.
        var presses = 0;
        var gone = false;
        for (var attempt = 0; attempt < 2 && !gone; attempt++)
        {
            Thread.Sleep(500);
            if (!TestSupport.ForegroundFor(dlg, checkPrefix)) break;
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            presses++;
            gone = TestSupport.WaitWindowGone(dlg, TimeSpan.FromSeconds(3));
        }
        ok &= S4Results.Record(checkPrefix + ".escape-closes", gone, $"dialog gone after {presses} Escape press(es)={gone}");

        var result = GuiSession.WaitUntil(() => s.TextOf("FileResult") == "cancelled", Settle);
        ok &= S4Results.Record(checkPrefix + ".result-cancelled", result, $"FileResult='{s.TextOf("FileResult")}'");

        var enabledAgain = GuiSession.WaitUntil(() => Native.IsWindowEnabled(s.MainHwnd) != 0, Settle);
        ok &= S4Results.Record(checkPrefix + ".owner-enabled-after", enabledAgain, $"IsWindowEnabled(main) after close={enabledAgain}");

        Assert.IsTrue(ok, checkPrefix + " checks failed; see out/s4-result.json");
    }
}
