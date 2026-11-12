using System.Text;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Drives a COM common item dialog with real mouse and keyboard input. UIA is used to find controls
/// and to read their state after the input, never to invoke a command. The dialog is confirmed
/// foreground before every click or keystroke.
/// </summary>
internal sealed class SystemFileDialogDriver : IDisposable
{
    private const string CommonDialogClass = "#32770";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(8);

    private readonly AppSession _session;
    private readonly UIA3Automation _automation;

    internal SystemFileDialogDriver(AppSession session)
    {
        _session = session;
        _automation = new UIA3Automation();
    }

    internal IntPtr WaitForDialog(TimeSpan timeout)
    {
        IntPtr found = IntPtr.Zero;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindDialog()) != IntPtr.Zero,
            timeout);
        if (found != IntPtr.Zero)
        {
            // The dialog is checked as it appeared, before any input activates it.
            OwnedWindowChecks.Record(found);
        }

        return found;
    }

    /// <summary>
    /// Finds an element with this automation id in any top level window of the application process.
    /// Menu commands live in a flyout window, not under the main window's automation root.
    /// </summary>
    internal AutomationElement? FindProcessElement(string automationId)
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
                    AutomationElement? found = window.FindFirstDescendant(
                        cf => cf.ByAutomationId(automationId));
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

            AutomationElement? global = desktop.FindFirstDescendant(
                cf => cf.ByAutomationId(automationId));
            if (global is not null && global.Properties.ProcessId.Value == _session.ProcessId)
            {
                return global;
            }
        }
        catch (Exception)
        {
            // UIA can throw while a flyout is opening or closing.
        }

        return null;
    }

    internal bool WaitGone(IntPtr dialog, TimeSpan timeout) =>
        ControlTestSupport.WaitUntil(() => !Win32.IsWindow(dialog), timeout);

    internal bool TrySetFileName(IntPtr dialog, string path, out string notes)
    {
        AutomationElement? edit = null;
        _ = ControlTestSupport.WaitUntil(
            () => (edit = FindFileNameEdit(dialog)) is not null,
            UiTimeout);

        if (edit is null)
        {
            notes = "fileNameEdit not found.";
            return false;
        }

        if (!ClickElement(dialog, edit) || !EnsureForeground(dialog))
        {
            notes = "fileNameEdit could not be clicked or the dialog could not be focused.";
            return false;
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(path);
        Thread.Sleep(200);

        AutomationElement? after = FindFileNameEdit(dialog);
        string readBack = after is null ? "(no file name box)" : ReadValue(after);
        bool matches = string.Equals(readBack, path, StringComparison.Ordinal);
        notes = matches
            ? $"fileNameEdit clicked, path typed and read back from '{SafeId(edit)}'."
            : $"typed the path, but the file name box reads '{readBack}'.";
        return matches;
    }

    internal bool TryAccept(IntPtr dialog, string acceptText, out string notes)
    {
        AutomationElement? window = WindowElement(dialog);
        AutomationElement? button = window is null ? null : FindAcceptButton(window, acceptText);
        if (button is null)
        {
            notes = $"acceptButton '{acceptText}' not found.";
            return false;
        }

        if (!ClickElement(dialog, button))
        {
            notes = $"acceptButton '{acceptText}' could not be clicked.";
            return false;
        }

        notes = $"acceptButton id='{SafeId(button)}' name='{SafeName(button)}' clicked.";
        return true;
    }

    internal bool TryCancel(IntPtr dialog)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!EnsureForeground(dialog))
            {
                return false;
            }

            Thread.Sleep(250);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            if (WaitGone(dialog, TimeSpan.FromSeconds(3)))
            {
                return true;
            }
        }

        return !Win32.IsWindow(dialog);
    }

    /// <summary>Finds a named element below the dialog. Ampersands from Win32 mnemonics are ignored.</summary>
    internal AutomationElement? FindNamed(IntPtr dialog, string name, ControlType? controlType = null)
    {
        AutomationElement? window = WindowElement(dialog);
        if (window is null)
        {
            return null;
        }

        try
        {
            AutomationElement[] elements = controlType is { } type
                ? window.FindAllDescendants(cf => cf.ByControlType(type))
                : window.FindAllDescendants();
            return elements.FirstOrDefault(element =>
                string.Equals(Normalize(SafeName(element)), Normalize(name), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal AutomationElement? FindRadio(IntPtr dialog, string name) =>
        FindNamed(dialog, name, ControlType.RadioButton);

    internal RadioState ReadRadio(AutomationElement? radio)
    {
        if (radio is null)
        {
            return new RadioState(false, false, false);
        }

        bool enabled = false;
        bool selected = false;
        try
        {
            enabled = radio.IsEnabled;
            if (radio.Patterns.SelectionItem.IsSupported)
            {
                selected = radio.Patterns.SelectionItem.Pattern.IsSelected.Value;
            }
            else if (radio.Patterns.Toggle.IsSupported)
            {
                selected = radio.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
            }
        }
        catch (Exception)
        {
            // A stale radio is reported as unselected.
        }

        return new RadioState(true, enabled, selected);
    }

    internal bool ClickRadio(IntPtr dialog, AutomationElement? radio)
    {
        if (radio is null)
        {
            return false;
        }

        return ClickElement(dialog, radio);
    }

    /// <summary>Finds the file type combo by its label text ("Save as type:" or "File type:").</summary>
    internal AutomationElement? FindFileTypeCombo(IntPtr dialog)
    {
        AutomationElement? window = WindowElement(dialog);
        if (window is null)
        {
            return null;
        }

        try
        {
            AutomationElement[] combos = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox));
            return combos.FirstOrDefault(combo =>
                SafeName(combo).Contains("type", StringComparison.OrdinalIgnoreCase) ||
                SafeId(combo).Contains("FileType", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Waits for the shell to finish creating the file type combo.</summary>
    internal AutomationElement? WaitForFileTypeCombo(IntPtr dialog, TimeSpan timeout)
    {
        AutomationElement? combo = null;
        _ = ControlTestSupport.WaitUntil(
            () => (combo = FindFileTypeCombo(dialog)) is not null,
            timeout);
        return combo;
    }

    internal string ReadFileType(IntPtr dialog)
    {
        AutomationElement? combo = WaitForFileTypeCombo(dialog, TimeSpan.FromSeconds(3));
        if (combo is null)
        {
            return string.Empty;
        }

        try
        {
            if (combo.Patterns.Selection.IsSupported)
            {
                AutomationElement[] selected = combo.Patterns.Selection.Pattern.Selection.Value;
                if (selected.Length > 0)
                {
                    return SafeName(selected[0]);
                }
            }
        }
        catch (Exception)
        {
            // The combo may expose only the value pattern.
        }

        return ReadValue(combo);
    }

    /// <summary>
    /// Selects the filter at the one-based <paramref name="index"/> (1 is the first filter, as the
    /// shell reports it through GetFileTypeIndex) with the keyboard: click the combo, Home to the
    /// first filter, Down for each step after the first, Enter to commit. The selected name is read back.
    /// </summary>
    internal bool SelectFileType(IntPtr dialog, int index, string expectedNameContains, out string notes)
    {
        if (index < 1)
        {
            notes = $"file type index {index} is invalid; indexes are one-based.";
            return false;
        }

        AutomationElement? combo = WaitForFileTypeCombo(dialog, TimeSpan.FromSeconds(8));
        if (combo is null)
        {
            notes = "file type combo not found.";
            return false;
        }

        if (!ClickElement(dialog, combo) || !EnsureForeground(dialog))
        {
            notes = "file type combo could not be clicked or the dialog could not be focused.";
            return false;
        }

        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.HOME);
        for (var step = 1; step < index; step++)
        {
            Keyboard.Press(VirtualKeyShort.DOWN);
            Thread.Sleep(50);
        }

        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(200);

        string selected = ReadFileType(dialog);
        bool matches = selected.Contains(expectedNameContains, StringComparison.OrdinalIgnoreCase);
        notes = $"file type index {index} selected '{selected}', expected to contain '{expectedNameContains}'.";
        return matches;
    }

    internal string SaveScreenshot(IntPtr dialog, string path) => WindowCapture.SaveArtifact("dialog.artifact." + Path.GetFileNameWithoutExtension(path), dialog, path);

    public void Dispose() => _automation.Dispose();

    private static string Normalize(string text) =>
        text.Replace("&", string.Empty, StringComparison.Ordinal).Trim();

    private static bool ClickElement(IntPtr dialog, AutomationElement element)
    {
        try
        {
            if (!EnsureForeground(dialog))
            {
                return false;
            }

            var rect = element.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            AppServicesSupport.Click(
                (int)(rect.Left + (rect.Width / 2)),
                (int)(rect.Top + (rect.Height / 2)));
            Thread.Sleep(100);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool EnsureForeground(IntPtr hwnd) => ForegroundGuard.Ensure(hwnd, "dialog.file.foreground", TimeSpan.FromSeconds(2));

    private IntPtr FindDialog()
    {
        IntPtr found = IntPtr.Zero;
        _ = EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId != (uint)_session.ProcessId)
                {
                    return true;
                }

                var className = new StringBuilder(64);
                _ = GetClassName(hwnd, className, className.Capacity);
                if (!string.Equals(className.ToString(), CommonDialogClass, StringComparison.Ordinal))
                {
                    return true;
                }

                found = hwnd;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private AutomationElement? WindowElement(IntPtr dialog)
    {
        try
        {
            return _automation.FromHandle(dialog);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AutomationElement? FindFileNameEdit(IntPtr dialog)
    {
        AutomationElement? window = WindowElement(dialog);
        if (window is null)
        {
            return null;
        }

        try
        {
            AutomationElement[] edits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            return ChooseFileNameEdit(edits);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AutomationElement? ChooseFileNameEdit(AutomationElement[] edits)
    {
        AutomationElement? bottomMost = null;
        double bottomMostTop = double.MinValue;

        foreach (AutomationElement edit in edits)
        {
            if (!IsUsableEdit(edit))
            {
                continue;
            }

            if (LooksLikeFileNameEdit(edit))
            {
                return edit;
            }

            double top = TopOf(edit);
            if (top > bottomMostTop)
            {
                bottomMost = edit;
                bottomMostTop = top;
            }
        }

        return bottomMost;
    }

    private static bool IsUsableEdit(AutomationElement edit)
    {
        try
        {
            return edit.IsEnabled
                && !edit.Properties.IsOffscreen.ValueOrDefault
                && edit.Patterns.Value.IsSupported;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool LooksLikeFileNameEdit(AutomationElement edit)
    {
        string id = SafeId(edit);
        if (id is "1001" or "1148" or "1152" or "FileNameControl" or "FileNameControlHost")
        {
            return true;
        }

        if (SafeName(edit).Contains("file name", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            AutomationElement? parent = edit.Parent;
            for (var depth = 0; parent is not null && depth < 3; depth++)
            {
                if (parent.ControlType == ControlType.ComboBox &&
                    SafeName(parent).Contains("file name", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                parent = parent.Parent;
            }
        }
        catch (Exception)
        {
            // Ancestor navigation is best effort; the bottom-most fallback still applies.
        }

        return false;
    }

    private static double TopOf(AutomationElement element)
    {
        try
        {
            return element.BoundingRectangle.Top;
        }
        catch (Exception)
        {
            return double.MinValue;
        }
    }

    private static AutomationElement? FindAcceptButton(AutomationElement window, string acceptText)
    {
        AutomationElement[] buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        foreach (AutomationElement button in buttons)
        {
            if (SafeId(button) == "1" && IsUsableButton(button))
            {
                return button;
            }
        }

        foreach (AutomationElement button in buttons)
        {
            string name = Normalize(SafeName(button));
            if (string.Equals(name, acceptText, StringComparison.OrdinalIgnoreCase) &&
                IsUsableButton(button))
            {
                return button;
            }
        }

        return null;
    }

    private static bool IsUsableButton(AutomationElement button)
    {
        try
        {
            return button.IsEnabled && !button.Properties.IsOffscreen.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ReadValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported
                ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty
                : SafeName(element);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeId(AutomationElement element)
    {
        try
        {
            return element.AutomationId ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

#pragma warning disable SYSLIB1054 // The small test-only P/Invoke surface does not need LibraryImport plumbing.
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
#pragma warning restore SYSLIB1054
}

/// <summary>What UIA reports for one radio button: present, enabled, selected.</summary>
internal readonly record struct RadioState(bool Present, bool Enabled, bool Selected);
