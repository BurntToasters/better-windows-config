using System.Diagnostics;
using Bwc.Core;
using Bwc.Registry;
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

        for (var index = 0; index < arguments.Count; index += 1)
        {
            string argument = arguments[index] ?? string.Empty;

            if (IsSwitch(argument, "/m"))
            {
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
            // No arguments, or /m alone: this is the normal window path.
            return false;
        }

        if (string.IsNullOrWhiteSpace(file))
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
            RegistryStore store = RegeditWritePolicy.CreateStore();

            if (operation == Operation.SilentImport)
            {
                RegFileImporter.ApplyFile(store, file, CancellationToken.None);
                Log.Info("Registry Editor silent import succeeded: " + file);
                return 0;
            }

            RegFileFormat format = operation == Operation.ExportV5
                ? RegFileFormat.V5
                : RegFileFormat.Regedit4;
            RegFileDocument document = key is null
                ? RegFileExporter.Export(
                    store.Reader,
                    RegistryPath.Parse("HKEY_CURRENT_USER"),
                    format,
                    CancellationToken.None)
                : RegFileExporter.Export(store.Reader, key, format, CancellationToken.None);

            using var buffer = new MemoryStream();

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

    private static bool IsSwitch(string argument, string expected) =>
        string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSwitch(string argument) =>
        argument.StartsWith('/') || argument.StartsWith('-');
}
