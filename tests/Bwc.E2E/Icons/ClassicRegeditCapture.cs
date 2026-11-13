using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;

using FlaUI.Core.Definitions;

namespace Bwc.E2E.Icons;

/// <summary>
/// Captures classic regedit (C:\Windows\regedit.exe) for the icon oracle: the initial window, and the
/// window with HKEY_CURRENT_USER expanded. It runs inside the GUI gate that the whole test run holds.
/// The process starts through AppSession, which sets __COMPAT_LAYER=RunAsInvoker on the child only, so
/// no UAC prompt is raised. Classic regedit writes LastKey and other applet values when it exits, so
/// those values are snapshotted before launch and put back after it has exited. The capture is
/// recorded as a check only when both files were written by this run. Otherwise it is a note.
/// </summary>
internal static class ClassicRegeditCapture
{
    internal const string RegeditPath = @"C:\Windows\regedit.exe";
    internal const string InitialFileName = "classic-initial-window.png";
    internal const string ExpandedFileName = "classic-hkcu-expanded-window.png";

    private const string CaptureCheckId = "icons.classic.captures";
    private const string RestoreCheckId = "icons.classic.applet.restore";
    private const string HkcuRow = "HKEY_CURRENT_USER";
    private const string SoftwareRow = "Software";
    private const string ComputerRow = "Computer";
    private static readonly TimeSpan ItemTimeout = TimeSpan.FromSeconds(15);

    internal static void Run(E2EConfig config, string captureDir)
    {
        string initialPath = Path.Combine(captureDir, InitialFileName);
        string expandedPath = Path.Combine(captureDir, ExpandedFileName);
        Directory.CreateDirectory(captureDir);

        // Remove older copies first, so a file on disk is always from this run.
        File.Delete(initialPath);
        File.Delete(expandedPath);

        if (!File.Exists(RegeditPath))
        {
            E2EResults.Note(CaptureCheckId, $"Not captured: {RegeditPath} is missing. The check is not recorded.");
            return;
        }

        ControlTestSupport.AppletSnapshot before;
        try
        {
            before = ControlTestSupport.SnapshotAppletValues();
        }
        catch (Exception ex)
        {
            // Without a snapshot there is no exact restore, so classic regedit is not started.
            E2EResults.Note(CaptureCheckId, $"Not captured: the applet values could not be read ({ex.Message}). The check is not recorded.");
            return;
        }

        string? failure = null;
        try
        {
            CaptureWindows(config, initialPath, expandedPath);
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            RestoreApplet(before);
        }

        bool written = File.Exists(initialPath) && File.Exists(expandedPath);
        if (failure is null && written)
        {
            E2EResults.Record(
                CaptureCheckId,
                true,
                $"classic captures taken by this run beside the BWC capture: {InitialFileName}, {ExpandedFileName}.");
            return;
        }

        E2EResults.Note(
            CaptureCheckId,
            $"Not captured: {failure ?? "a capture file was not written"}. The check is not recorded.");
    }

    private static void CaptureWindows(E2EConfig config, string initialPath, string expandedPath)
    {
        // The session closes classic regedit on dispose. Classic regedit writes its applet values on exit.
        using var session = new AppSession(config, RegeditPath);

        if (!ControlTestSupport.WaitUntil(() => FindTreeItem(session, ComputerRow) is not null, ItemTimeout))
        {
            throw new InvalidOperationException("Classic regedit showed no Computer row.");
        }

        SaveWindow(session, initialPath);

        AutomationElement hkcu = FindTreeItem(session, HkcuRow)
            ?? throw new InvalidOperationException("Classic regedit showed no HKEY_CURRENT_USER row.");
        ControlTestSupport.Select(hkcu);
        _ = ControlTestSupport.Expand(hkcu);

        bool expanded = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.StateOf(FindTreeItem(session, HkcuRow)) == "Expanded" &&
                FindTreeItem(session, SoftwareRow) is not null,
            ItemTimeout);
        if (!expanded)
        {
            throw new InvalidOperationException(
                $"HKEY_CURRENT_USER did not expand (state {ControlTestSupport.StateOf(FindTreeItem(session, HkcuRow))}).");
        }

        SaveWindow(session, expandedPath);

        session.Close();
    }

    private static void RestoreApplet(ControlTestSupport.AppletSnapshot before)
    {
        try
        {
            string? mismatch = ControlTestSupport.RestoreAppletValues(before);
            if (mismatch is null)
            {
                E2EResults.Record(
                    RestoreCheckId,
                    true,
                    $"Applet values match the snapshot taken before classic regedit ran ({before.Values.Count} value(s), key existed={before.KeyExisted}).");
            }
            else
            {
                E2EResults.Record(RestoreCheckId, false, $"Applet values differ after restore: {mismatch}.");
            }
        }
        catch (Exception ex)
        {
            E2EResults.Record(RestoreCheckId, false, $"Applet restore threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SaveWindow(AppSession session, string path)
    {
        _ = WindowCapture.SaveArtifact("classic.artifact." + Path.GetFileNameWithoutExtension(path), session.MainHwnd, path);
    }

    private static AutomationElement? FindTreeItem(AppSession session, string name)
    {
        try
        {
            return session.Window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(item => string.Equals(ControlTestSupport.SafeName(item), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
