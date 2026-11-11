using Bwc.Core;
using Bwc.Registry;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// The classic outcome of a finished .reg import, shared by the window and the command line. A refused
/// file and a rejected write are failures. A skipped line is not, because classic regedit skips it
/// silently and the oracle shows no message for it.
/// </summary>
internal static class RegFileImportReport
{
    /// <summary>True when the file was refused or at least one registry write was rejected.</summary>
    public static bool IsFailure(RegFileImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsRefused || result.HasWriteFailures;
    }

    /// <summary>
    /// The resource key of the classic message. A refused file has its own text (133, 125, or 129), a
    /// rejected write has string 132, and any other import has string 228.
    /// </summary>
    public static string MessageKey(RegFileImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Refusal switch
        {
            RegFileRefusal.NotRegistryFile => "MsgImportNotRegistryFile",
            RegFileRefusal.InvalidKeyPath => "MsgImportInvalidKey",
            RegFileRefusal.ReadFailed => "MsgImportOpenFailed",
            _ => result.HasWriteFailures ? "MsgImportPartial" : "MsgImportSucceeded",
        };
    }

    /// <summary>
    /// Writes the outcome to the log: the refusal, or the counts and every skipped line and rejected
    /// write with its key, value, and Win32 error.
    /// </summary>
    public static void LogOutcome(string filePath, RegFileImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsRefused)
        {
            Log.Warn($"Registry Editor did not import '{filePath}': {result.Refusal}.");
            return;
        }

        foreach (RegFileImportFailure failure in result.Failures)
        {
            Log.Warn(
                $"Registry Editor import of '{filePath}' line {failure.Line} ({failure.Kind}): " +
                $"key '{failure.KeyPath ?? string.Empty}', value '{failure.ValueName ?? string.Empty}', " +
                $"Win32 {failure.Win32Error}: {failure.Message}");
        }

        Log.Info(
            $"Registry Editor import of '{filePath}': {result.KeysWritten} key(s) written, " +
            $"{result.KeysDeleted} deleted, {result.ValuesWritten} value(s) written, " +
            $"{result.ValuesDeleted} value(s) deleted, {result.Failures.Count} failure(s).");
    }
}
