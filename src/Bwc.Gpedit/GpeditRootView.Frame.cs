using Bwc.Core;
using Bwc.UI.Console.Frame;
using Bwc.UI.Console.Shell;
using Bwc.UI.Dialogs;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace Bwc.Gpedit;

/// <summary>
/// The frame commands that the editor and the E2E frame host share (piece U3a, docs/failure-modes/console-frame.md):
/// View > Add/Remove Columns, View > Customize View and Action > Export List. The dialogs and the export writer are the
/// frame's (<c>Bwc.UI.Console.Frame</c>); this file opens them, applies what they return and keeps the choice for the
/// settings file. The column choice is one for the list: every layout applies it after the columns are set.
/// </summary>
public sealed partial class GpeditRootView
{
    private ConsoleColumnChoice _columnChoice = ConsoleColumnChoice.Default;
    private bool _frameDialogOpen;

    /// <summary>True while a modal dialog of the frame is open, so a second command does not stack another on it.</summary>
    private bool FrameDialogAllowed => !_frameDialogOpen && !_closed && _policyDialog is null && _filterDialog is null && _templatesDialog is null;

    /// <summary>The window that owns the frame dialogs, or false while the view is not in a window.</summary>
    private bool TryGetOwnerHandle(out nint owner)
    {
        owner = 0;
        if (XamlRoot?.ContentIslandEnvironment is not { } environment)
        {
            return false;
        }

        owner = Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
        return owner != 0;
    }

    /// <summary>Applies the column choice to the shown list: the first column stays, the others follow the choice.</summary>
    private void ApplyColumnChoice() => _columnChoice.ApplyTo(_list);

    /// <summary>Applies the five view switches the settings file holds. An absent key means shown.</summary>
    private void ApplySavedSwitches()
    {
        Shell.IsTreeVisible = _settings.ViewConsoleTree;
        Shell.IsToolbarVisible = _settings.ViewStandardToolbar;
        Shell.AreSnapInToolbarsVisible = _settings.ViewSnapInToolbars;
        Shell.IsStatusBarVisible = _settings.ViewStatusBar;
        _extended.IsDescriptionBarShown = _settings.ViewDescriptionBar;
    }

    // ---- Add/Remove Columns --------------------------------------------------------------------------

    private void RunColumns()
    {
        if (!FrameDialogAllowed || _list.Columns.Count == 0 || !TryGetOwnerHandle(out nint owner))
        {
            return;
        }

        _ = RunColumnsAsync(owner);
    }

    private async Task RunColumnsAsync(nint owner)
    {
        _frameDialogOpen = true;
        try
        {
            var dialog = new ConsoleColumnsDialog(_list.Columns);
            if (await dialog.ShowAsync(owner) && dialog.Result is { } result && !_closed)
            {
                _columnChoice = _columnChoice.Replace(result, [.. _list.Columns.Select(column => column.Id)]);
                ApplyColumnChoice();
                if (IsEditorHost)
                {
                    ScheduleSettingsSave();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Add/Remove Columns dialog failed: " + ex.Message);
        }
        finally
        {
            _frameDialogOpen = false;
        }
    }

    // ---- Customize View ------------------------------------------------------------------------------

    private void RunCustomize()
    {
        // The menu entry is disabled while the options forbid customizing; the guard keeps any other way in out as well.
        if (!_options.AllowCustomizeViews || !FrameDialogAllowed || !TryGetOwnerHandle(out nint owner))
        {
            return;
        }

        _ = RunCustomizeAsync(owner);
    }

    private async Task RunCustomizeAsync(nint owner)
    {
        _frameDialogOpen = true;
        try
        {
            var now = new ConsoleViewSwitches(
                Shell.IsTreeVisible,
                Shell.IsToolbarVisible,
                Shell.IsStatusBarVisible,
                _extended.IsDescriptionBarShown,
                Shell.AreSnapInToolbarsVisible);

            // Each box acts when it changes, with the dialog still open; the dialog has no Cancel, so OK only closes it.
            var dialog = new ConsoleCustomizeDialog(now, ApplySwitches);
            _ = await dialog.ShowAsync(owner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Customize View dialog failed: " + ex.Message);
        }
        finally
        {
            _frameDialogOpen = false;
        }
    }

    private void ApplySwitches(ConsoleViewSwitches switches)
    {
        Shell.IsTreeVisible = switches.ConsoleTree;
        Shell.IsToolbarVisible = switches.StandardToolbar;
        Shell.IsStatusBarVisible = switches.StatusBar;
        _extended.IsDescriptionBarShown = switches.DescriptionBar;
        Shell.AreSnapInToolbarsVisible = switches.SnapInToolbars;
        if (IsEditorHost)
        {
            ScheduleSettingsSave();
        }
    }

    // ---- Export List ---------------------------------------------------------------------------------

    private void RunExport()
    {
        if (!FrameDialogAllowed || !TryGetOwnerHandle(out nint owner))
        {
            return;
        }

        // The command returns first; the modal save dialog opens from the dispatcher, so a UI Automation Invoke of the menu
        // entry or the toolbar button is not held until the dialog closes.
        _frameDialogOpen = true;
        if (!DispatcherQueue.TryEnqueue(() => _ = ExportAsync(owner)))
        {
            _frameDialogOpen = false;
        }
    }

    private async Task ExportAsync(nint owner)
    {
        try
        {
            ExportListDialogResult? choice = CommonFileDialogs.ShowExportListSave(owner);
            if (choice is null || _closed)
            {
                return;
            }

            ConsoleListExportFailure failure = ConsoleListExport.Write(choice.FilePath, _list, choice.Format);
            if (failure != ConsoleListExportFailure.None)
            {
                ErrorDialog message = ErrorDialog.ForMessage(ConsoleStrings.Get("ConsoleExportCaption"), ConsoleListExport.MessageFor(failure));
                _ = await message.ShowAsync(owner);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Export List failed: " + ex.Message);
        }
        finally
        {
            _frameDialogOpen = false;
        }
    }
}
