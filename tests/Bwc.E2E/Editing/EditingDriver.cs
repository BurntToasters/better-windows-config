using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Bwc.E2E.Editing;

/// <summary>
/// Finds modal dialog windows and menu flyouts. A dialog is a separate top level HWND, so the
/// main window automation root used by <see cref="AppSession"/> cannot see its controls.
/// </summary>
internal sealed class EditingDriver : IDisposable
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly AppSession _session;
    private readonly UIA3Automation _automation;

    internal EditingDriver(AppSession session)
    {
        _session = session;
        _automation = new UIA3Automation();
    }

    internal void OpenMenu(params int[] commandIds)
    {
        _ = ControlTestSupport.EnsureForeground(_session);

        foreach (int commandId in commandIds)
        {
            AutomationElement? element = WaitForElement("MenuCommand_" + commandId, UiTimeout);
            if (element is null)
            {
                throw new InvalidOperationException(
                    $"Menu command {commandId} did not appear. Visible menu commands: {DescribeMenuCommands()}. " +
                    $"Foreground: {DescribeForeground()}.");
            }

            // A submenu opens through ExpandCollapse. Invoke on a MenuFlyoutSubItem does not expand it, so the
            // command below it never appears (docs/failure-modes/menus.md, Invoke through automation).
            if (IsSubmenu(element) && element.Patterns.ExpandCollapse.IsSupported)
            {
                element.Patterns.ExpandCollapse.Pattern.Expand();
                continue;
            }

            try
            {
                if (element.Patterns.Invoke.IsSupported)
                {
                    element.Patterns.Invoke.Pattern.Invoke();
                }
                else
                {
                    element.Click();
                }
            }
            catch (Exception)
            {
                element.Click();
            }
        }
    }

    private static bool IsSubmenu(AutomationElement element)
    {
        try
        {
            return string.Equals(element.ClassName, "MenuFlyoutSubItem", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool WaitForDialog(TimeSpan? timeout = null) =>
        WaitForElement("DialogRoot", timeout ?? UiTimeout) is not null;

    /// <summary>
    /// Dismisses a message box whose OK button has the focus. Enter is sent once, and the message text must
    /// clear. A prompt underneath is left open.
    /// </summary>
    internal bool DismissErrorBox(TimeSpan? timeout = null)
    {
        Keyboard.Type(VirtualKeyShort.RETURN);
        return ControlTestSupport.WaitUntil(
            () => TextOf("DialogErrorText").Length == 0,
            timeout ?? TimeSpan.FromSeconds(10));
    }

    internal bool WaitForNoDialog(TimeSpan? timeout = null) =>
        ControlTestSupport.WaitUntil(
            () => FindElement("DialogRoot") is null,
            timeout ?? TimeSpan.FromSeconds(10));

    internal AutomationElement? WaitForElement(string automationId, TimeSpan? timeout = null)
    {
        AutomationElement? result = null;
        _ = ControlTestSupport.WaitUntil(
            () => (result = FindElement(automationId)) is not null,
            timeout ?? UiTimeout);
        return result;
    }

    internal bool SetText(string automationId, string text)
    {
        AutomationElement? element = WaitForElement(automationId);
        if (element is null)
        {
            return false;
        }

        try
        {
            element.AsTextBox().Text = text;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool Invoke(string automationId)
    {
        AutomationElement? element = WaitForElement(automationId);
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }
        }
        catch (Exception)
        {
            // Fall back to a click below.
        }

        try
        {
            element.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal string TextOf(string automationId)
    {
        AutomationElement? element = FindElement(automationId);
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // TextBlock and other read-only elements expose their text as Name.
        }

        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal bool TypeKeys(params string[] keys)
    {
        try
        {
            ControlTestSupport.SendKeys(_session, keys);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _automation.Dispose();
    }

    /// <summary>The MenuCommand_ automation ids on screen in the app's windows. Diagnostic only.</summary>
    private string DescribeMenuCommands()
    {
        var ids = new List<string>();
        try
        {
            AutomationElement desktop = _automation.GetDesktop();
            foreach (AutomationElement window in desktop.FindAllChildren())
            {
                int processId;
                try
                {
                    processId = window.Properties.ProcessId.Value;
                }
                catch (Exception)
                {
                    continue;
                }

                if (processId != _session.ProcessId)
                {
                    continue;
                }

                foreach (AutomationElement item in window.FindAllDescendants())
                {
                    try
                    {
                        string id = item.AutomationId ?? string.Empty;
                        if (id.StartsWith("MenuCommand_", StringComparison.Ordinal))
                        {
                            string offscreen = item.IsOffscreen ? "offscreen" : "onscreen";
                            ids.Add($"{id}({offscreen})");
                        }
                    }
                    catch (Exception)
                    {
                        // An element that closed during the walk is skipped.
                    }
                }
            }
        }
        catch (Exception)
        {
            return "unavailable";
        }

        return ids.Count == 0 ? "none" : string.Join(",", ids);
    }

    private string DescribeForeground()
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        return $"0x{foreground.ToInt64():X} class='{Win32.ClassNameOf(foreground)}' main={foreground == _session.MainHwnd}";
    }

    private AutomationElement? FindElement(string automationId)
    {
        try
        {
            AutomationElement desktop = _automation.GetDesktop();

            foreach (AutomationElement window in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                int processId;
                try
                {
                    processId = window.Properties.ProcessId.Value;
                }
                catch (Exception)
                {
                    continue;
                }

                if (processId != _session.ProcessId)
                {
                    continue;
                }

                try
                {
                    AutomationElement? found = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                    if (found is not null)
                    {
                        return found;
                    }
                }
                catch (Exception)
                {
                    // A window that disappeared during enumeration is skipped.
                }
            }

            AutomationElement? global = desktop.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (global is not null && global.Properties.ProcessId.Value == _session.ProcessId)
            {
                return global;
            }
        }
        catch (Exception)
        {
            // UIA can throw while a window is being created or destroyed.
        }

        return null;
    }
}
