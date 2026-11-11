using System.Globalization;
using Bwc.Core;
using Bwc.Regedit.Printing;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.Controllers;

/// <summary>Print of the selected key and its readable descendants.</summary>
internal sealed class PrintController
{
    private readonly RegeditContext _context;

    public PrintController(RegeditContext context)
    {
        _context = context;
    }

    private string Get(string key) => _context.Messages.Get(key);

    /// <summary>Prints the selected key and its readable descendants after the owned print dialog.</summary>
    public void Print() => _context.StartCommand(PrintAsync);

    private async Task PrintAsync()
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Print because another modal is open or the window handle is not attached.");
            return;
        }

        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        bool listHadFocus = _context.IsFocusWithin(_context.Shell.ListContent);
        PrintResult result;

        // Keep the modal gate for the dialog and the background GDI job. This rejects a second
        // print or another modal while the spooler is receiving the report.
        _context.DialogOpen = true;
        try
        {
            if (!RegistryPrinter.TryShowPrintDialog(
                    _context.WindowHwnd,
                    out PrintDevice? device,
                    out result))
            {
                if (result.Status == PrintStatus.Cancelled)
                {
                    Log.Info("Registry Editor print was cancelled.");
                }
            }
            else if (device is not null)
            {
                using (device)
                {
                    try
                    {
                        result = await _context.RunRegistryAsync(
                            () => RegistryPrinter.PrintToDevice(device, _context.Store.Reader, key));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Registry Editor print job failed: " + ex.Message);
                        result = PrintResult.Failed(0, ex.Message);
                    }
                }
            }
            else
            {
                result = PrintResult.Failed(
                    0,
                    MessageCatalog.PrintNoDeviceMessage);
            }
        }
        finally
        {
            _context.DialogOpen = false;
        }

        FocusAfterPrint(listHadFocus);

        if (result.Status == PrintStatus.Printed)
        {
            Log.Info($"Registry Editor printed '{key.ToDisplayString()}'.");
            return;
        }

        if (result.Status != PrintStatus.Failed)
        {
            return;
        }

        string message = string.Format(
            CultureInfo.InvariantCulture,
            Get("DialogPrintFailed"),
            key.ToDisplayString(),
            result.ErrorCode,
            result.Message);

        await _context.ShowErrorAsync(ErrorDialog.ForMessage(Get("DialogErrorTitle"), message));
    }

    private void FocusAfterPrint(bool listHadFocus)
    {
        if (listHadFocus)
        {
            _ = _context.Shell.ListContent.Focus(FocusState.Programmatic);
            return;
        }

        try
        {
            _context.Shell.TreeContent.FocusSelected();
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not return focus to the tree after printing: " + ex.Message);
        }
    }
}
