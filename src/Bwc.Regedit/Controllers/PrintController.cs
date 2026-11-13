using Bwc.Core;
using Bwc.Regedit.Printing;
using Bwc.Registry;
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

#if BWC_E2E
        // E2E-only seam: the report is formatted exactly as a job would format it and written to the file
        // the test names. The spooler and the print dialog are skipped, so no paper or PDF is produced.
        string? reportPath = Environment.GetEnvironmentVariable("BWC_E2E_PRINT_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            PrintResult reportResult = await _context.RunRegistryAsync(() => WriteReportForE2E(reportPath, key));
            FocusAfterPrint(listHadFocus);
            if (reportResult.Status == PrintStatus.Failed)
            {
                await _context.ShowErrorAsync(new ClassicMessage(
                    Get("DialogErrorTitle"),
                    Get("MsgPrintFailed")).ToErrorDialog());
            }

            return;
        }
#endif

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
                            () => RegistryPrinter.PrintToDevice(device, _context.Store.Reader, key, ReportLabels()));
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
                    Get("PrintNoDevice"));
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

        // The engine detail stays in the log. The dialog shows the classic print text.
        Log.Warn(
            $"Registry Editor print of '{key.ToDisplayString()}' failed: code {result.ErrorCode}: {result.Message}");
        await _context.ShowErrorAsync(new ClassicMessage(
            Get("DialogErrorTitle"),
            Get("MsgPrintFailed")).ToErrorDialog());
    }

    /// <summary>The classic report labels (oracle 8101 to 8110), read from the resources.</summary>
    private RegistryPrinter.PrintReportLabels ReportLabels() => new(
        Get("PrintKeyName"),
        Get("PrintClassName"),
        Get("PrintLastWriteTime"),
        Get("PrintValueHeading"),
        Get("PrintValueName"),
        Get("PrintValueType"),
        Get("PrintValueDataSize"),
        Get("PrintValueData"),
        Get("PrintNoName"),
        Get("PrintNoClass"));

#if BWC_E2E
    private PrintResult WriteReportForE2E(string reportPath, RegistryPath key)
    {
        try
        {
            string report = RegistryPrinter.FormatReport(
                _context.Store.Reader,
                key,
                RegistryPrinter.DefaultMaxChars,
                ReportLabels());
            File.WriteAllText(reportPath, report, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return PrintResult.Printed();
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not write the E2E print report: " + ex.Message);
            return PrintResult.Failed(0, ex.Message);
        }
    }
#endif

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
