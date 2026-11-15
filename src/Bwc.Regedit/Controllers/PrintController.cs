using Bwc.Core;
using Bwc.Regedit.Printing;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// Print of the selected key and its readable descendants, or of the whole registry at Computer. The job runs behind
/// the classic progress dialog, whose Cancel stops it through the spooler's abort proc. A remote key reads through
/// its connection's store.
/// </summary>
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

        if (!TryResolveTarget(out PrintTarget target))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        bool listHadFocus = _context.IsFocusWithin(_context.Shell.ListContent);

#if BWC_E2E
        if (await TryRunE2ESeamAsync(target, listHadFocus))
        {
            return;
        }
#endif

        // The modal gate covers the print dialog only. The job then holds the progress dialog, which is a modal too.
        _context.DialogOpen = true;
        bool accepted;
        PrintDevice? device;
        PrintResult result;
        try
        {
            accepted = RegistryPrinter.TryShowPrintDialog(_context.WindowHwnd, out device, out result);
        }
        finally
        {
            _context.DialogOpen = false;
        }

        if (!accepted)
        {
            if (result.Status == PrintStatus.Cancelled)
            {
                Log.Info("Registry Editor print was cancelled.");
            }
        }
        else if (device is null)
        {
            result = PrintResult.Failed(0, Get("PrintNoDevice"));
        }
        else
        {
            PrintDevice ready = device;
            using (ready)
            {
                result = await RunJobWithProgressAsync(
                    token => RegistryPrinter.PrintToDevice(ready, target.Reader, target.StartKey, token));
            }
        }

        FocusAfterPrint(listHadFocus);
        await ShowOutcomeAsync(result, target);
    }

    /// <summary>
    /// The key or the whole registry that Print acts on. Computer prints every root, a local key reads the local store,
    /// and a remote key reads its connection's store. False when nothing printable is selected.
    /// </summary>
    private bool TryResolveTarget(out PrintTarget target)
    {
        if (_context.IsComputerSelected)
        {
            target = new PrintTarget(null, _context.Store.Reader, "Computer");
            return true;
        }

        if (_context.TryGetSelectedLocation(out KeyLocation location))
        {
            target = new PrintTarget(location.Path, location.Reader, location.DisplayPath);
            return true;
        }

        target = default;
        return false;
    }

    /// <summary>
    /// Shows the classic progress dialog, owned by the main window, and runs the job behind it. Cancel cancels the
    /// token the job checks, and the dialog closes when the job ends, so the owner is restored before the result.
    /// </summary>
    private async Task<PrintResult> RunJobWithProgressAsync(Func<CancellationToken, PrintResult> job)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new PrintProgressDialog(
            Get("PrintProgressTitle"),
            Get("PrintProgressText"),
            Get("DialogFindCancel"),
            cancellation.Cancel);

        Task<bool> dialogTask = _context.ShowDialogAsync(progress);
        PrintResult result;
        try
        {
            result = await _context.RunRegistryAsync(() => job(cancellation.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Registry Editor print job failed: " + ex.Message);
            result = PrintResult.Failed(0, ex.Message);
        }
        finally
        {
            if (!progress.Cancelled)
            {
                progress.CompleteFromHost();
            }
        }

        _ = await dialogTask;
        return result;
    }

    private async Task ShowOutcomeAsync(PrintResult result, PrintTarget target)
    {
        if (result.Status == PrintStatus.Printed)
        {
            Log.Info($"Registry Editor printed '{target.Name}'.");
            return;
        }

        if (result.Status != PrintStatus.Failed)
        {
            return;
        }

        // The engine detail stays in the log. The dialog shows the classic print text.
        Log.Warn(
            $"Registry Editor print of '{target.Name}' failed: code {result.ErrorCode}: {result.Message}");
        await _context.ShowErrorAsync(new ClassicMessage(
            Get("DialogErrorTitle"),
            Get("MsgPrintFailed")).ToErrorDialog());
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

    /// <summary>The key or whole registry that a print reads, and the name the log uses for it.</summary>
    private readonly record struct PrintTarget(RegistryPath? StartKey, RegistryReader Reader, string Name);

#if BWC_E2E
    private const string ReportPathVariable = "BWC_E2E_PRINT_REPORT_PATH";
    private const string GuardProbeVariable = "BWC_E2E_PRINT_GUARD_PROBE";
    private const string PdfPathVariable = "BWC_E2E_PRINT_PDF_PATH";
    private const string ResultPathVariable = "BWC_E2E_PRINT_RESULT_PATH";

    // E2E-only seams. Each one replaces the dialog, so no test ever opens a printer's dialog and sends a job.
    // Returns false when no seam variable is set, so the normal dialog path runs.
    private async Task<bool> TryRunE2ESeamAsync(PrintTarget target, bool listHadFocus)
    {
        string? reportPath = Environment.GetEnvironmentVariable(ReportPathVariable);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            PrintResult reportResult = await _context.RunRegistryAsync(() => WriteReportForE2E(reportPath, target));
            FocusAfterPrint(listHadFocus);
            await ShowOutcomeAsync(reportResult, target);
            return true;
        }

        string? probe = Environment.GetEnvironmentVariable(GuardProbeVariable);
        if (!string.IsNullOrWhiteSpace(probe))
        {
            await RunGuardProbeAsync(probe, target);
            FocusAfterPrint(listHadFocus);
            return true;
        }

        string? pdfPath = Environment.GetEnvironmentVariable(PdfPathVariable);
        if (!string.IsNullOrWhiteSpace(pdfPath))
        {
            bool created = false;
            PrintResult pdfResult = await RunJobWithProgressAsync(token =>
            {
                RegistryPrinter.E2EPrintOutcome outcome = RegistryPrinter.PrintNamedForE2E(
                    PrintE2EGuard.PdfPrinterName,
                    pdfPath,
                    target.Reader,
                    target.StartKey,
                    token);
                created = outcome.DeviceContextCreated;
                return outcome.Result;
            });

            WriteE2EResult(
                Environment.GetEnvironmentVariable(ResultPathVariable),
                [$"status={pdfResult.Status}", $"dcCreated={created}", $"message={pdfResult.Message}"]);
            FocusAfterPrint(listHadFocus);
            await ShowOutcomeAsync(pdfResult, target);
            return true;
        }

        return false;
    }

    // Each entry is "printer" or "printer|output". Every entry goes through the guarded path, so a refused entry
    // never creates a device context. The result lists the status and dcCreated for each entry.
    private async Task RunGuardProbeAsync(string probe, PrintTarget target)
    {
        var lines = new List<string>();
        foreach (string entry in probe.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int bar = entry.IndexOf('|', StringComparison.Ordinal);
            string name = bar < 0 ? entry : entry[..bar];
            string? output = bar < 0 ? null : entry[(bar + 1)..];
            RegistryPrinter.E2EPrintOutcome outcome = await _context.RunRegistryAsync(() =>
                RegistryPrinter.PrintNamedForE2E(name, output, target.Reader, target.StartKey, CancellationToken.None));
            lines.Add(string.Join('\t', name, outcome.Result.Status, outcome.DeviceContextCreated, outcome.Result.Message));
        }

        WriteE2EResult(Environment.GetEnvironmentVariable(ResultPathVariable), lines);
    }

    // The report comes from the same text export source as File > Export. UTF-8 without a byte-order mark.
    private static PrintResult WriteReportForE2E(string reportPath, PrintTarget target)
    {
        try
        {
            using var text = new StringWriter();
            RegistryPrinter.WriteReport(target.Reader, target.StartKey, text);
            File.WriteAllText(reportPath, text.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return PrintResult.Printed();
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not write the E2E print report: " + ex.Message);
            return PrintResult.Failed(0, ex.Message);
        }
    }

    private static void WriteE2EResult(string? resultPath, IEnumerable<string> lines)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return;
        }

        File.WriteAllText(
            resultPath,
            string.Join('\n', lines) + "\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
#endif
}
