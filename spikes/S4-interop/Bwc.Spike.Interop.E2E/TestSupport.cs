using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Bwc.Spike.Interop.E2E;

/// <summary>Shared helpers for the S4 E2E tests: focus inspection, keyboard gating, and dialog lookup.</summary>
internal static class TestSupport
{
    public const string ModalTitle = "Modal Dialog (S4)";

    /// <summary>Identifier of the element that has UIA keyboard focus, or "(none)".</summary>
    public static string FocusedDescription(GuiSession s)
    {
        try
        {
            var e = s.Automation.FocusedElement();
            return $"{e.ControlType}:'{e.Name}'#{e.AutomationId}";
        }
        catch (Exception ex)
        {
            return "(none: " + ex.Message + ")";
        }
    }

    public static bool FocusedIs(GuiSession s, ControlType type, string name)
    {
        try
        {
            var e = s.Automation.FocusedElement();
            return e.ControlType == type && e.Name == name;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool FocusedAutomationIdIs(GuiSession s, string automationId)
    {
        try
        {
            return s.Automation.FocusedElement().AutomationId == automationId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Keys are only sent when the target window is the foreground window; otherwise the check is recorded as failed.</summary>
    public static bool ForegroundFor(nint hwnd, string checkId)
    {
        var fg = GuiSession.EnsureForeground(hwnd);
        S4Results.Record(checkId + ".foreground", fg, fg ? "target window is foreground" : $"foreground={Native.GetForegroundWindow():X} target={hwnd:X}");
        return fg;
    }

    /// <summary>Finds a visible element by name and type anywhere on the desktop (popups and flyouts are not children of the window).</summary>
    public static AutomationElement? FindVisibleOnDesktop(GuiSession s, string name, ControlType type)
    {
        return s.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)));
    }

    /// <summary>
    /// Clicks once the element's bounding rectangle has stopped changing. XAML popups (ComboBox dropdown, flyouts)
    /// animate in, and a click computed from an intermediate rectangle can miss the item.
    /// </summary>
    public static void ClickWhenStable(AutomationElement element)
    {
        var last = element.BoundingRectangle;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(150);
            var now = element.BoundingRectangle;
            var stable = now.Left == last.Left && now.Top == last.Top && now.Width == last.Width && now.Height == last.Height;
            last = now;
            if (stable && now.Width > 0 && now.Height > 0)
            {
                break;
            }
        }
        element.Click();
    }

    public static bool IsVisible(AutomationElement? element)
    {
        if (element is null) return false;
        try
        {
            return !element.Properties.IsOffscreen.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static nint WaitForOwnWindow(GuiSession s, Func<string, bool> title, Func<string, bool>? cls, TimeSpan timeout)
    {
        var found = nint.Zero;
        GuiSession.WaitUntil(() =>
        {
            found = s.FindOwnTopLevel(title, cls);
            return found != 0;
        }, timeout);
        return found;
    }

    public static bool WaitWindowGone(nint hwnd, TimeSpan timeout)
    {
        return GuiSession.WaitUntil(() => Native.IsWindow(hwnd) == 0, timeout);
    }
}
