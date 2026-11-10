using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Bwc.Spike.Interop.E2E;

[TestClass]
public sealed class ModalDialogTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Open_Modal_Is_Owned_Disables_Owner_And_Cancel_Re_Enables_It()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "modal.cancel"), "main window must be foreground before clicks");

        var dlg = OpenModal(s);
        var ok = S4Results.Record("modal.window-exists", dlg != 0, $"dialog hwnd={dlg:X}");
        if (dlg == 0)
        {
            Assert.Fail("modal dialog did not appear");
        }

        var owner = Native.OwnerOf(dlg);
        ok &= S4Results.Record("modal.owner-is-main-window", owner == s.MainHwnd,
            $"GetWindow(GW_OWNER)={owner:X} main={s.MainHwnd:X}");

        var mainEnabledWhileOpen = Native.IsWindowEnabled(s.MainHwnd) != 0;
        ok &= S4Results.Record("modal.owner-disabled-while-open", !mainEnabledWhileOpen,
            $"IsWindowEnabled(main) while dialog open={mainEnabledWhileOpen}");

        var exStyle = Native.GetWindowLongPtrExStyle(dlg);
        var taskbarHidden = (exStyle & Native.WS_EX_APPWINDOW) == 0 && owner != 0;
        ok &= S4Results.Record("modal.not-in-taskbar", taskbarHidden,
            $"exStyle=0x{exStyle:X} owner={owner:X} (owned and not WS_EX_APPWINDOW => no taskbar button)");

        var dialogRect = Native.RectOf(dlg);
        var ownerRect = Native.RectOf(s.MainHwnd);
        var dialogCx = (dialogRect.left + dialogRect.right) / 2;
        var dialogCy = (dialogRect.top + dialogRect.bottom) / 2;
        var ownerCx = (ownerRect.left + ownerRect.right) / 2;
        var ownerCy = (ownerRect.top + ownerRect.bottom) / 2;
        var centered = Math.Abs(dialogCx - ownerCx) <= 2 && Math.Abs(dialogCy - ownerCy) <= 2;
        ok &= S4Results.Record("modal.centered-on-owner", centered,
            $"dialog centre=({dialogCx},{dialogCy}) owner centre=({ownerCx},{ownerCy})");

        var dlgElement = s.Automation.FromHandle(dlg);
        var cancel = s.Find("ModalCancel", dlgElement);
        ok &= S4Results.Record("modal.cancel-button-found", cancel is not null, "ModalCancel present: " + (cancel is not null));
        S4Results.Record("modal.uia-geometry-info", true,
            "UIA BoundingRectangle (info only): dialog=" + Describe(dlgElement) + "; ModalCombo=" + Describe(s.Find("ModalCombo", dlgElement))
            + "; ModalOk=" + Describe(s.Find("ModalOk", dlgElement)) + "; ModalCancel=" + Describe(cancel));
        Invoke(cancel);
        var gone = TestSupport.WaitWindowGone(dlg, Settle);
        ok &= S4Results.Record("modal.cancel-closes", gone, $"dialog gone after Cancel={gone}");

        var mainEnabledAfter = GuiSession.WaitUntil(() => Native.IsWindowEnabled(s.MainHwnd) != 0, Settle);
        ok &= S4Results.Record("modal.owner-enabled-after-cancel", mainEnabledAfter, $"IsWindowEnabled(main) after Cancel={mainEnabledAfter}");

        var reactivated = GuiSession.WaitUntil(() => Native.GetForegroundWindow() == s.MainHwnd, Settle);
        ok &= S4Results.Record("modal.owner-reactivated", reactivated, $"foreground after close={Native.GetForegroundWindow():X} main={s.MainHwnd:X}");

        var result = GuiSession.WaitUntil(() => s.TextOf("ModalResult") == "cancelled", Settle);
        ok &= S4Results.Record("modal.result-cancelled", result, $"ModalResult='{s.TextOf("ModalResult")}'");

        Assert.IsTrue(ok, "one or more modal checks failed; see out/s4-result.json");
    }

    /// <summary>
    /// The first ComboBox interaction of a fresh launch is inside the modal: mouse-open the dropdown, then mouse-click
    /// "Beta". This is the scenario that failed in an earlier version of this test (see README).
    /// </summary>
    [TestMethod]
    public void Modal_ComboBox_Mouse_Selects_Item_On_First_Open()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "modal.combo.mouse"), "main window must be foreground before clicks");
        var ok = ComboAttempt(s, "mouse", (dlg, combo) =>
        {
            combo.Click();
            var opened = GuiSession.WaitUntil(() => TestSupport.IsVisible(TestSupport.FindVisibleOnDesktop(s, "Beta", ControlType.ListItem)), Settle);
            if (opened)
            {
                ClickBeta(s);
            }
            return opened;
        });
        Assert.IsTrue(ok, "modal ComboBox mouse selection failed; see out/s4-result.json");
    }

    /// <summary>Control: the same ComboBox in the main (non-modal) window, mouse only.</summary>
    [TestMethod]
    public void Main_Window_ComboBox_Mouse_Selects_Item_Control()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "combo.control"), "main window must be foreground before clicks");
        s.Find("MainCombo")?.Click();
        var opened = GuiSession.WaitUntil(() => TestSupport.IsVisible(TestSupport.FindVisibleOnDesktop(s, "Beta", ControlType.ListItem)), Settle);
        ClickBeta(s);
        var selected = GuiSession.WaitUntil(() => s.TextOf("MainComboText") == "Main combo: Beta", Settle);
        var ok = S4Results.Record("combo.control.main-window-mouse-select", opened && selected,
            $"main (non-modal) ComboBox: dropdown opened={opened}; MainComboText='{s.TextOf("MainComboText")}'");
        Assert.IsTrue(ok, "main-window ComboBox control failed; see out/s4-result.json");
    }

    /// <summary>Modal ComboBox selected through UIA SelectionItem.Select and through the keyboard (Alt+Down, Down, Enter).</summary>
    [TestMethod]
    public void Modal_ComboBox_UIA_And_Keyboard_Select_Item()
    {
        using var s = GuiSession.Start();
        Assert.IsTrue(TestSupport.ForegroundFor(s.MainHwnd, "modal.combo"), "main window must be foreground before clicks");
        var ok = true;

        ok &= ComboAttempt(s, "uia-select", (dlg, combo) =>
        {
            combo.Click();
            var beta = GuiSession.WaitUntil(() => TestSupport.IsVisible(TestSupport.FindVisibleOnDesktop(s, "Beta", ControlType.ListItem)), Settle)
                ? TestSupport.FindVisibleOnDesktop(s, "Beta", ControlType.ListItem)
                : null;
            if (beta is null) return false;
            beta.Patterns.SelectionItem.Pattern.Select();
            return true;
        });

        ok &= ComboAttempt(s, "keyboard", (dlg, combo) =>
        {
            combo.Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.DOWN);
            var opened = GuiSession.WaitUntil(() => TestSupport.IsVisible(TestSupport.FindVisibleOnDesktop(s, "Beta", ControlType.ListItem)), TimeSpan.FromSeconds(3));
            if (!opened) return false;
            Keyboard.Type(VirtualKeyShort.DOWN);
            Keyboard.Type(VirtualKeyShort.ENTER);
            return true;
        });

        Assert.IsTrue(ok, "ComboBox-in-modal UIA/keyboard checks failed; see out/s4-result.json");
    }

    /// <summary>Opens the modal, runs one selection attempt, records the outcome, then closes it with Cancel.</summary>
    private static bool ComboAttempt(GuiSession s, string label, Func<nint, AutomationElement, bool> attempt)
    {
        var dlg = OpenModal(s);
        var checkId = "modal.combo." + label;
        if (dlg == 0)
        {
            return S4Results.Record(checkId + ".window-exists", false, "modal dialog did not appear");
        }
        var dlgElement = s.Automation.FromHandle(dlg);
        TestSupport.ForegroundFor(dlg, checkId);
        var combo = s.Find("ModalCombo", dlgElement);
        if (combo is null)
        {
            Invoke(s.Find("ModalCancel", dlgElement));
            return S4Results.Record(checkId + ".combo-found", false, "ModalCombo missing");
        }

        var attempted = attempt(dlg, combo);
        var selected = GuiSession.WaitUntil(() => s.TextOf("ModalSelection") == "Selected: Beta", Settle);
        var ok = S4Results.Record(checkId, attempted && selected,
            $"attempt={label} dropdown-or-action ok={attempted}; ModalSelection='{s.TextOf("ModalSelection")}'");

        // Close: Escape closes an open dropdown; then Cancel the dialog through its button.
        if (TestSupport.FocusedAutomationIdIs(s, "ModalCombo") || attempted)
        {
            if (TestSupport.ForegroundFor(dlg, checkId + ".cleanup"))
            {
                Keyboard.Type(VirtualKeyShort.ESCAPE);
            }
        }
        if (Native.IsWindow(dlg) != 0)
        {
            Invoke(s.Find("ModalCancel", s.Automation.FromHandle(dlg)));
        }
        if (!TestSupport.WaitWindowGone(dlg, Settle))
        {
            S4Results.Record(checkId + ".dialog-closed", false, "dialog still open after cleanup");
            return false;
        }
        var resultOk = GuiSession.WaitUntil(() => s.TextOf("ModalResult") == "cancelled" || s.TextOf("ModalResult").StartsWith("ok:", StringComparison.Ordinal), Settle);
        return ok && resultOk;
    }

    private static nint OpenModal(GuiSession s)
    {
        var button = s.Find("OpenModalButton");
        if (button is not null) TestSupport.ClickWhenStable(button);
        var dlg = TestSupport.WaitForOwnWindow(s, t => t == TestSupport.ModalTitle, null, TimeSpan.FromSeconds(4));
        if (dlg == 0 && button is not null)
        {
            // One retry: the first click can land before the button reaches its final position after launch.
            // Safe: while no dialog exists the owner is enabled, and once a dialog exists the owner is disabled.
            TestSupport.ClickWhenStable(button);
            dlg = TestSupport.WaitForOwnWindow(s, t => t == TestSupport.ModalTitle, null, Settle);
        }
        return dlg;
    }

    private static void ClickBeta(GuiSession s)
    {
        var beta = TestSupport.FindVisibleOnDesktop(s, "Beta", ControlType.ListItem);
        if (beta is not null)
        {
            TestSupport.ClickWhenStable(beta);
        }
    }

    private static string Describe(AutomationElement? e)
    {
        if (e is null) return "missing";
        try
        {
            return $"rect={e.BoundingRectangle} offscreen={e.Properties.IsOffscreen.ValueOrDefault}";
        }
        catch (Exception ex)
        {
            return "error " + ex.Message;
        }
    }

    /// <summary>UIA Invoke (not a mouse click), so the dialog button works even when its clickable point is unavailable.</summary>
    private static void Invoke(AutomationElement? element)
    {
        element?.AsButton().Invoke();
    }
}
