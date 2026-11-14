using System.Diagnostics;
using System.Globalization;
using Bwc.Core;
using Bwc.Registry;
using Bwc.Regedit.Controllers;
using Bwc.Regedit.Registry;

namespace Bwc.Regedit.CommandLine;

/// <summary>
/// Parses and runs the classic Registry Editor command line. The class has no UI dependency so
/// it can be called before a window exists. <see cref="TryRun"/> returns false when the caller
/// should continue with the normal window path.
/// </summary>
public static class RegeditCommandLine
{
    private const string UsageText =
        "Usage: BwcRegedit.exe [/s <file>] [/e <file> [key]] [/a <file> [key]] [/m]";

    private enum Operation
    {
        None,
        SilentImport,
        ExportV5,
        ExportRegedit4,
    }

    /// <summary>
    /// Runs a command-line operation. Arguments exclude the executable path. Returns true when a
    /// command line operation was handled; the caller must then exit with <paramref name="exitCode"/>.
    /// </summary>
    public static bool TryRun(IReadOnlyList<string> arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        exitCode = 0;
        Operation operation = Operation.None;
        string? file = null;
        string? key = null;
        string? startupRegFile = null;

        for (var index = 0; index < arguments.Count; index += 1)
        {
            string argument = arguments[index] ?? string.Empty;

            if (IsSwitch(argument, "/m"))
            {
                continue;
            }

            // A single positional path opens the window and asks to import it, as classic regedit does.
            if (IsPositionalPath(argument))
            {
                if (startupRegFile is not null)
                {
                    return UsageError(out exitCode);
                }

                startupRegFile = argument;
                continue;
            }

            if (IsSwitch(argument, "/s"))
            {
                if (operation != Operation.None || index + 1 >= arguments.Count)
                {
                    return UsageError(out exitCode);
                }

                operation = Operation.SilentImport;
                index += 1;
                file = arguments[index];
                continue;
            }

            if (IsSwitch(argument, "/e") || IsSwitch(argument, "/a"))
            {
                if (operation != Operation.None || index + 1 >= arguments.Count)
                {
                    return UsageError(out exitCode);
                }

                operation = IsSwitch(argument, "/e")
                    ? Operation.ExportV5
                    : Operation.ExportRegedit4;
                index += 1;
                file = arguments[index];

                if (index + 1 < arguments.Count && !LooksLikeSwitch(arguments[index + 1]))
                {
                    index += 1;
                    key = arguments[index];
                }

                continue;
            }

            return UsageError(out exitCode);
        }

        if (operation == Operation.None)
        {
            // No arguments, /m alone, or one .reg path: this is the normal window path.
            return false;
        }

        if (startupRegFile is not null || string.IsNullOrWhiteSpace(file))
        {
            return UsageError(out exitCode);
        }

        Log.Initialize("regedit");
        exitCode = Execute(operation, file, key);
        return true;
    }

    private static int Execute(Operation operation, string file, string? key)
    {
        try
        {
            // Policy level 2 refuses a silent import. The message goes to standard error, like the other refusals.
            if (operation == Operation.SilentImport &&
                RegeditPolicy.BlocksSilentImport(RegeditPolicy.ReadDisableRegistryTools()))
            {
                Log.Warn("Registry Editor silent import was refused by the DisableRegistryTools policy.");
                WriteStandardError("BwcRegedit: " + RegeditStrings.Get("MsgRegistryEditingDisabled"));
                return 1;
            }

            RegistryStore store = RegeditWritePolicy.CreateStore();

            if (operation == Operation.SilentImport)
            {
                RegFileImportResult result = RegFileImporter.ApplyFile(store, file, CancellationToken.None);
                return ReportSilentImport(file, result);
            }

            RegFileFormat format = operation == Operation.ExportV5
                ? RegFileFormat.V5
                : RegFileFormat.Regedit4;

            if (key is null)
            {
                // No key means the whole registry, as classic regedit does: every root under Computer.
                WriteWholeRegistryAtomically(store, file, format);
                Log.Info($"Registry Editor export succeeded: {file}, the whole registry, {format} format.");
                return 0;
            }

            using var buffer = new MemoryStream();
            RegFileDocument document = RegFileExporter.Export(store.Reader, key, format, CancellationToken.None);
            if (format == RegFileFormat.V5)
            {
                RegFileWriter.WriteV5(document, buffer);
            }
            else
            {
                RegFileWriter.WriteRegedit4(document, buffer);
            }

            File.WriteAllBytes(file, buffer.ToArray());
            Log.Info(
                $"Registry Editor export succeeded: {file}, {document.Keys.Count} key(s), {format} format.");
            return 0;
        }
        catch (Exception ex)
        {
            string action = operation == Operation.SilentImport ? "Silent import" : "Export";
            string message = $"{action} failed for '{file}': {ex.Message}";
            Log.Exception(message, ex);
            WriteStandardError("BwcRegedit: " + message);
            return 1;
        }
    }

    /// <summary>
    /// Streams the whole registry to a temp file beside the target, then moves it over the target. A failure
    /// leaves the old target untouched and the temp file is removed.
    /// </summary>
    private static void WriteWholeRegistryAtomically(RegistryStore store, string file, RegFileFormat format)
    {
        string fullPath = Path.GetFullPath(file);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The export target has no folder: " + fullPath);
        string temp = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CreateExportService(store).WriteRegFile(
                    startKey: null,
                    stream,
                    format,
                    RegistryExportScope.WholeRegistry,
                    CancellationToken.None);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException ex)
            {
                Log.Warn("Registry Editor could not remove the export temp file " + temp + ": " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warn("Registry Editor could not remove the export temp file " + temp + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// The export service for a command line export. The ship build reads the live registry. The E2E build
    /// maps HKEY_CURRENT_USER to the run sandbox, as the window's export does.
    /// </summary>
    private static RegistryExportService CreateExportService(RegistryStore store)
    {
#if BWC_E2E
        string? configured = Environment.GetEnvironmentVariable("BWC_E2E_SANDBOX_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            RegistryPath.TryParse(configured.Trim(), out RegistryPath? sandboxRoot) &&
            sandboxRoot is not null)
        {
            return new RegistryExportService(new SandboxExportSource(store.Reader, sandboxRoot));
        }
#endif

        return new RegistryExportService(store.Reader);
    }

    /// <summary>
    /// Logs the outcome of a silent import. A refused file or a rejected write exits 1 and writes the classic
    /// text to standard error. A skipped line exits 0, because classic regedit skips it without a message.
    /// </summary>
    private static int ReportSilentImport(string file, RegFileImportResult result)
    {
        RegFileImportReport.LogOutcome(file, result);
        if (!RegFileImportReport.IsFailure(result))
        {
            return 0;
        }

        string text = string.Format(
            CultureInfo.CurrentCulture,
            RegeditStrings.Get(RegFileImportReport.MessageKey(result)),
            file);
        WriteStandardError("BwcRegedit: " + text);
        return 1;
    }

    private static bool UsageError(out int exitCode)
    {
        exitCode = 2;
        WriteStandardError(UsageText);
        return true;
    }

    private static void WriteStandardError(string text)
    {
        try
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("BwcRegedit could not write to standard error: " + ex.Message);
        }
    }

    /// <summary>
    /// True when the argument is a positional path. Classic regedit offers to import any file it is
    /// given, so the extension does not matter; the import reads the file as a registry file.
    /// </summary>
    public static bool IsPositionalPath(string argument) =>
        !string.IsNullOrWhiteSpace(argument) && !LooksLikeSwitch(argument);

    /// <summary>The file given on the command line for a window launch, or null.</summary>
    public static string? StartupRegFile(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.FirstOrDefault(IsPositionalPath);
    }

    /// <summary>True when /m is present. Such a launch never joins the single-instance gate.</summary>
    public static bool AllowsMultipleInstances(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument => IsSwitch(argument ?? string.Empty, "/m"));
    }

    private static bool IsSwitch(string argument, string expected) =>
        string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSwitch(string argument) =>
        argument.StartsWith('/') || argument.StartsWith('-');
}
