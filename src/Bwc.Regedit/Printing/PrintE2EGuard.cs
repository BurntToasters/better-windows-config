#if BWC_E2E
using Bwc.Core;

namespace Bwc.Regedit.Printing;

/// <summary>
/// The E2E-only print guard. A job starts only on "Microsoft Print to PDF" with an output file under the repo's
/// artifacts folder, named by BWC_E2E_PRINT_ARTIFACTS_DIR. Every other device, and every job without such a file,
/// is refused before StartDoc. Ship builds have no guard.
/// </summary>
internal static class PrintE2EGuard
{
    internal const string PdfPrinterName = "Microsoft Print to PDF";
    internal const string ArtifactsDirVariable = "BWC_E2E_PRINT_ARTIFACTS_DIR";
    internal const string LineDelayVariable = "BWC_E2E_PRINT_LINE_DELAY_MS";
    internal const string RefusalText = "print refused by E2E guard";

    private static int? _lineDelayMs;

    /// <summary>True only for the PDF printer with an output file inside the artifacts folder.</summary>
    internal static bool Allows(string? deviceName, string? outputFile, out string reason)
    {
        if (!string.Equals(deviceName, PdfPrinterName, StringComparison.Ordinal))
        {
            reason = "the device is not " + PdfPrinterName;
            return false;
        }

        string? artifactsDir = Environment.GetEnvironmentVariable(ArtifactsDirVariable);
        if (string.IsNullOrWhiteSpace(outputFile) || string.IsNullOrWhiteSpace(artifactsDir))
        {
            reason = "there is no output file under the artifacts folder";
            return false;
        }

        try
        {
            string root = Path.GetFullPath(artifactsDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(outputFile);
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                reason = "the output file is outside the artifacts folder";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "the output path is not valid";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Logs the refusal and returns the failure. The job never reaches StartDoc.</summary>
    internal static PrintResult Refuse(string reason)
    {
        string message = RefusalText + ": " + reason + ".";
        Log.Warn(message);
        return PrintResult.Failed(0, message);
    }

    /// <summary>Sleeps for the per-line delay from BWC_E2E_PRINT_LINE_DELAY_MS, so a cancel test has time to cancel.</summary>
    internal static void PaceLine()
    {
        _lineDelayMs ??= ReadLineDelay();
        if (_lineDelayMs > 0)
        {
            Thread.Sleep(_lineDelayMs.Value);
        }
    }

    private static int ReadLineDelay() =>
        int.TryParse(
            Environment.GetEnvironmentVariable(LineDelayVariable),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int value) && value > 0 && value <= 5000
            ? value
            : 0;
}
#endif
