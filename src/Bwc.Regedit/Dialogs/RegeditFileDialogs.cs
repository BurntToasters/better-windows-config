using Bwc.Core;
using Bwc.Regedit.Controllers;
using Bwc.UI.Dialogs;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// The Registry Editor's file dialog requests. Each one checks the modal state, shows the common
/// dialog through <see cref="CommonFileDialogs"/>, and reports an unexpected failure as the classic
/// command failure. Null means cancelled, or that another modal was open.
/// </summary>
internal static class RegeditFileDialogs
{
    /// <summary>File > Import: the Import open dialog with the Registration, Hive, and All Files filters.</summary>
    public static ImportFileDialogResult? ShowImport(RegeditContext context) =>
        Run(context, () => CommonFileDialogs.ShowImportOpen(context.WindowHwnd));

    /// <summary>File > Load Hive: the open dialog with the single All Files filter (oracle string 180).</summary>
    public static string? ShowLoadHive(RegeditContext context) =>
        Run(context, () => CommonFileDialogs.ShowLoadHiveOpen(context.WindowHwnd));

    /// <summary>File > Export: the save dialog with the export range and the five classic filters.</summary>
    public static ExportFileDialogResult? ShowExport(RegeditContext context, string? defaultFileName, bool hasSelectedKey) =>
        Run(context, () => CommonFileDialogs.ShowExportSave(context.WindowHwnd, defaultFileName, hasSelectedKey));

    private static T? Run<T>(RegeditContext context, Func<T?> show)
        where T : class
    {
        if (context.DialogOpen || context.FileDialogOpen)
        {
            Log.Warn("Registry Editor ignored a file dialog request because another modal is already open.");
            return null;
        }

        if (context.WindowHwnd == 0)
        {
            Log.Warn("Registry Editor ignored a file dialog request because the window handle is not attached.");
            return null;
        }

        context.FileDialogOpen = true;
        try
        {
            return show();
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not show a file dialog: " + ex.Message);
            _ = context.ShowErrorAsync(context.Messages.ForCommandFailure().ToErrorDialog());
            return null;
        }
        finally
        {
            context.FileDialogOpen = false;
        }
    }
}
