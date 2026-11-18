using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Message boxes (captured, then answered), closing windows the driver opened, and the cleanup that leaves no dialog open between
// operations. Failure modes 12, 13 and 23 of the driver document.
internal sealed partial class GpeditDriver
{
    private static readonly (string Name, int Id)[] StandardButtons =
    {
        ("OK", 1), ("Cancel", 2), ("Abort", 3), ("Retry", 4), ("Ignore", 5), ("Yes", 6), ("No", 7), ("Close", 8), ("Help", 9),
        ("Try Again", 10), ("Continue", 11),
    };

    private static readonly string[] DefaultAnswers = { "OK", "Close", "Cancel", "No" };

    private static string CleanText(string text) => text.Replace("&", string.Empty, StringComparison.Ordinal).Trim();

    // What a window says: title, native static texts, native buttons, UI Automation texts and buttons (for task dialogs), a screenshot.
    internal JsonObject CaptureWindow(WinInfo window)
    {
        var statics = new JsonArray();
        var buttons = new JsonArray();
        foreach (WinInfo child in ProbeWindows.Children(window.Hwnd).Where(c => c.Visible))
        {
            if (string.Equals(child.Class, "Static", StringComparison.OrdinalIgnoreCase) && child.Title.Length > 0)
            {
                statics.Add(child.Title);
            }
            else if (string.Equals(child.Class, "Button", StringComparison.OrdinalIgnoreCase))
            {
                buttons.Add(new JsonObject { ["text"] = child.Title, ["id"] = Win32.GetDlgCtrlID(child.Hwnd) });
            }
        }

        var uiaTexts = new JsonArray();
        var uiaButtons = new JsonArray();
        try
        {
            AutomationElement element = AutomationElement.FromHandle(window.Hwnd);
            foreach (AutomationElement text in ProbeUia.OfType(element, ControlType.Text))
            {
                string name = ProbeUia.NameOf(text);
                if (name.Length > 0)
                {
                    uiaTexts.Add(name);
                }
            }

            foreach (AutomationElement button in ProbeUia.OfType(element, ControlType.Button))
            {
                uiaButtons.Add(ProbeUia.NameOf(button));
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            uiaTexts.Add("<uia unavailable: " + ex.Message + ">");
        }

        return new JsonObject
        {
            ["title"] = window.Title,
            ["class"] = window.Class,
            ["kind"] = Classify(window).ToString(),
            ["texts"] = statics,
            ["buttons"] = buttons,
            ["uiaTexts"] = uiaTexts,
            ["uiaButtons"] = uiaButtons,
            ["screenshot"] = Shot(window.Hwnd, "box-" + window.Hwnd.ToString("X")),
        };
    }

    // Captures a message box and closes it with the wanted button (default order OK, Close, Cancel, No). The box must be gone afterwards.
    internal JsonObject DismissMessageBox(WinInfo window, string? answer)
    {
        JsonObject info = CaptureWindow(window);
        string[] order = answer is null ? DefaultAnswers : new[] { answer }.Concat(DefaultAnswers).ToArray();
        List<WinInfo> native = ProbeWindows.Children(window.Hwnd)
            .Where(c => c.Visible && string.Equals(c.Class, "Button", StringComparison.OrdinalIgnoreCase)).ToList();
        string? used = null;
        string via = string.Empty;
        foreach (string wanted in order)
        {
            int id = StandardButtons.FirstOrDefault(b => string.Equals(b.Name, wanted, StringComparison.OrdinalIgnoreCase)).Id;
            WinInfo? button = native.FirstOrDefault(c => (id != 0 && Win32.GetDlgCtrlID(c.Hwnd) == id) || string.Equals(CleanText(c.Title), wanted, StringComparison.OrdinalIgnoreCase));
            if (button is not null)
            {
                DriverNative.Click(button.Hwnd);
                used = wanted;
                via = "BM_CLICK";
                break;
            }

            AutomationElement? uia = null;
            try
            {
                uia = ProbeUia.ByName(AutomationElement.FromHandle(window.Hwnd), ControlType.Button, wanted, prefix: false);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                uia = null;
            }

            if (uia is not null && ProbeUia.Invoke(uia))
            {
                used = wanted;
                via = "Invoke";
                break;
            }
        }

        if (used is null)
        {
            Win32.PostMessageW(window.Hwnd, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            used = "(window closed)";
            via = "WM_CLOSE";
        }

        info["answeredWith"] = used;
        info["answeredVia"] = via;
        info["answerRequested"] = answer;
        info["answerFallback"] = answer is not null && !string.Equals(answer, used, StringComparison.OrdinalIgnoreCase);
        if (!WaitClosed(window.Hwnd, TimeSpan.FromSeconds(5)))
        {
            Win32.PostMessageW(window.Hwnd, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!WaitClosed(window.Hwnd, TimeSpan.FromSeconds(4)))
            {
                throw new DriverException($"a message box '{window.Title}' could not be closed", fatal: true, evidence: info);
            }
        }

        return info;
    }

    // Closes a dialog the driver does not want open by Cancel, never OK: the Cancel button, then Escape, then WM_CLOSE.
    internal string CancelWindow(WinInfo window)
    {
        if (!IsOpen(window.Hwnd))
        {
            return "already closed";
        }

        try
        {
            AutomationElement element = AutomationElement.FromHandle(window.Hwnd);
            AutomationElement? cancel = ById(element, "buttonCancel") ?? ProbeUia.ByName(element, ControlType.Button, "Cancel", prefix: false);
            if (cancel is not null && ProbeUia.Invoke(cancel) && WaitClosed(window.Hwnd, TimeSpan.FromSeconds(4)))
            {
                return "Cancel button";
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            Note("cancel by pattern failed: " + ex.Message);
        }

        IntPtr cancelItem = Win32Ext.GetDlgItem(window.Hwnd, Wm.IDCANCEL);
        if (cancelItem != IntPtr.Zero)
        {
            DriverNative.Click(cancelItem);
            if (WaitClosed(window.Hwnd, TimeSpan.FromSeconds(4)))
            {
                return "IDCANCEL click";
            }
        }

        try
        {
            DriverInput.RequireForeground(window.Hwnd, Pid);
            DriverInput.Key(ProbeInput.VkEscape);
            if (WaitClosed(window.Hwnd, TimeSpan.FromSeconds(4)))
            {
                return "Escape";
            }
        }
        catch (DriverException ex)
        {
            Note("escape not sent: " + ex.Message);
        }

        Win32.PostMessageW(window.Hwnd, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return WaitClosed(window.Hwnd, TimeSpan.FromSeconds(4)) ? "WM_CLOSE" : "NOT CLOSED";
    }

    // Visible top-level windows of mmc that are not in the known set and are not the main window, menus or tooltips.
    internal List<WinInfo> NewDialogs(HashSet<IntPtr> known) => Dialogs().Where(d => !known.Contains(d.Hwnd)).ToList();

    // Any message box among the new windows is captured and answered. Other new windows are left to the caller (a dialog it expects).
    internal JsonArray AnswerNewMessageBoxes(HashSet<IntPtr> known, string? answer)
    {
        var handled = new JsonArray();
        foreach (WinInfo window in NewDialogs(known))
        {
            if (Classify(window) == DialogKind.MessageBox)
            {
                handled.Add(DismissMessageBox(window, answer));
            }
        }

        return handled;
    }

    // Closes every dialog of the process, topmost first: message boxes are answered, the rest cancelled. Throws a fatal error when a
    // window stays. Returns what was done, so a result file shows an operation that left something open.
    internal JsonArray EnsureNoDialogs()
    {
        var handled = new JsonArray();
        for (int round = 0; round < 10; round++)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            List<WinInfo> open = Dialogs();
            if (open.Count == 0)
            {
                return handled;
            }

            WinInfo window = open[0];
            DialogKind kind = Classify(window);
            if (kind == DialogKind.MessageBox)
            {
                handled.Add(DismissMessageBox(window, null));
                continue;
            }

            string shot = Shot(window.Hwnd, "left-open-" + window.Hwnd.ToString("X"));
            string how = CancelWindow(window);
            handled.Add(new JsonObject { ["title"] = window.Title, ["class"] = window.Class, ["kind"] = kind.ToString(), ["closedBy"] = how, ["screenshot"] = shot });
            if (how == "NOT CLOSED")
            {
                throw new DriverException($"a window '{window.Title}' ({window.Class}) cannot be closed", fatal: true, evidence: handled);
            }
        }

        throw new DriverException("windows keep appearing after they are closed", fatal: true, evidence: handled);
    }

    // Closes menus that are still open. A menu that survives six Escape presses stops the run, since it would block every later step.
    internal int CloseMenus()
    {
        int closed = 0;
        for (int i = 0; i < 6 && ProbeWindows.PopupMenus(Pid).Count > 0; i++)
        {
            DriverInput.RequireForeground(main, Pid);
            DriverInput.Key(ProbeInput.VkEscape);
            Thread.Sleep(250);
            closed++;
        }

        if (ProbeWindows.PopupMenus(Pid).Count > 0)
        {
            throw new DriverException("a popup menu cannot be closed", fatal: true);
        }

        return closed;
    }
}
