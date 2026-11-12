using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Finds the owned progress and result windows of the Registry Editor. The progress dialog and the
/// classic message box have the same window title, so a progress window is identified by its
/// ProgressBar automation id and a result window by its result text.
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
        foreach ((IntPtr hwnd, string _, string title) in AppServicesSupport.OwnedWindows(mainHwnd))
        {
            if (title != Caption)
            {
                continue;
            }

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

    /// <summary>Reads the determinate value of the progress bar, or -1 when it is a marquee or gone.</summary>
    internal static double ProgressValue(UIA3Automation automation, IntPtr progressHwnd)
    {
        try
        {
            AutomationElement window = automation.FromHandle(progressHwnd);
            AutomationElement? bar = window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressBar"));
            if (bar is null || !bar.Patterns.RangeValue.IsSupported)
            {
                return -1;
            }

            return bar.Patterns.RangeValue.Pattern.Value.Value;
        }
        catch (Exception)
        {
            return -1;
        }
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
