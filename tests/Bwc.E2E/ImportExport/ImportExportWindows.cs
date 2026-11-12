using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Finds the owned progress and result windows of the Registry Editor. A progress window is identified
/// by its ProgressBar automation id, because its caption differs by direction: the export progress uses
/// "Registry Editor" and the import progress (DLG-019) uses "Import Registry File". A result window is
/// identified by its result text and the "Registry Editor" caption of the classic message box.
/// </summary>
internal static class ImportExportWindows
{
    private const string Caption = "Registry Editor";

    /// <summary>Waits for the owned progress dialog and returns its HWND, or zero on timeout.</summary>
    internal static IntPtr WaitForProgress(UIA3Automation automation, IntPtr mainHwnd, TimeSpan timeout)
    {
        IntPtr found = IntPtr.Zero;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                found = FindProgress(automation, mainHwnd);
                return found != IntPtr.Zero;
            },
            timeout);
        return found;
    }

    internal static IntPtr FindProgress(UIA3Automation automation, IntPtr mainHwnd)
    {
        foreach ((IntPtr hwnd, string _, string _) in AppServicesSupport.OwnedWindows(mainHwnd))
        {
            try
            {
                AutomationElement window = automation.FromHandle(hwnd);
                if (window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressBar")) is not null)
                {
                    return hwnd;
                }
            }
            catch (Exception)
            {
                // The window closed while it was being inspected. The next candidate is tried.
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Reads the status line of the progress dialog, or an empty string when it is gone.</summary>
    internal static string ProgressStatus(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? status = window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressStatus"));
            return status?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The progress bar state: whether it was found, its value, and its maximum (sections or keys).</summary>
    internal readonly record struct BarState(bool Present, double Value, double Maximum);

    /// <summary>Reads the progress bar value and maximum through the RangeValue pattern.</summary>
    internal static BarState ReadProgress(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? bar = window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressBar"));
            if (bar is null || !bar.Patterns.RangeValue.IsSupported)
            {
                return new BarState(false, 0, 0);
            }

            var range = bar.Patterns.RangeValue.Pattern;
            return new BarState(true, range.Value.Value, range.Maximum.Value);
        }
        catch (Exception)
        {
            return new BarState(false, 0, 0);
        }
    }

    /// <summary>
    /// The names of every button UI Automation reports inside the progress window, or null when the window
    /// cannot be read. The title bar Close button is one of them, so callers filter it by name.
    /// </summary>
    internal static IReadOnlyList<string>? ButtonNames(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement[] buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            return buttons.Select(button => button.Name ?? string.Empty).ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the Name of the element with this automation id inside the progress window, or empty.</summary>
    internal static string ReadText(UIA3Automation automation, IntPtr progressHwnd, string automationId)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The caption of an owned window, read from the window text, or empty when it is not owned.</summary>
    internal static string TitleOf(IntPtr mainHwnd, IntPtr hwnd)
    {
        foreach ((IntPtr candidate, string _, string title) in AppServicesSupport.OwnedWindows(mainHwnd))
        {
            if (candidate == hwnd)
            {
                return title;
            }
        }

        return string.Empty;
    }

    /// <summary>Clicks the Cancel button of the progress dialog with a real mouse click.</summary>
    internal static bool ClickCancel(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? cancel = window.FindFirstDescendant(cf => cf.ByAutomationId("DialogSecondary"));
            if (cancel is null)
            {
                return false;
            }

            if (!ForegroundGuard.Ensure(progressHwnd, "dialog.progress.foreground", TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            var rect = cancel.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            AppServicesSupport.Click(
                (int)(rect.Left + (rect.Width / 2)),
                (int)(rect.Top + (rect.Height / 2)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes a screenshot of the whole dialog window, including its frame, as a PNG.</summary>
    internal static string CaptureWindow(IntPtr hwnd, string path)
    {
        // The picture is a verified, settled screen copy (WindowCapture), so nothing is raised before the capture.
        return WindowCapture.SaveArtifact("dialog.artifact." + Path.GetFileNameWithoutExtension(path), hwnd, path);
    }

    /// <summary>
    /// Waits for an owned result message whose text matches. The progress dialog is skipped even
    /// though it has the same title.
    /// </summary>
    internal static IntPtr WaitForResult(
        IntPtr mainHwnd,
        Func<string, bool> matches,
        TimeSpan timeout,
        params IntPtr[] exclude)
    {
        IntPtr found = IntPtr.Zero;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                foreach ((IntPtr hwnd, string _, string title) in AppServicesSupport.OwnedWindows(mainHwnd))
                {
                    if (title != Caption || exclude.Contains(hwnd))
                    {
                        continue;
                    }

                    string text = AppServicesSupport.DialogText(hwnd);
                    if (matches(text))
                    {
                        found = hwnd;
                        return true;
                    }
                }

                return false;
            },
            timeout);
        return found;
    }
}
