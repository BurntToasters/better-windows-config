using System.Text;

namespace Bwc.Oracle.Capture;

// `export`: lets classic regedit write its own .reg files for the seeded tree. regedit writes
// straight to the golden paths, so the files are exactly what regedit produced.
internal static class ExportCommand
{
    private const string OracleKeyArg = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string ManyKeyArg = @"HKEY_CURRENT_USER\Software\BWC-Oracle\Many300";
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(120);

    internal static void Run(RepoPaths paths)
    {
        Directory.CreateDirectory(paths.ExportsDir);

        GuiLock.Run(() =>
        {
            int v5Exit = ExportTo(paths.ExportV5, "/e", OracleKeyArg);
            int regedit4Exit = ExportTo(paths.ExportRegedit4, "/a", OracleKeyArg);
            int singleExit = ExportTo(paths.ExportSingleKeyV5, "/e", ManyKeyArg);

            Console.WriteLine($"export exit codes: v5={v5Exit} regedit4={regedit4Exit} single-key-v5={singleExit}");
            if (v5Exit != 0 || regedit4Exit != 0 || singleExit != 0)
            {
                throw new InvalidOperationException("regedit export returned a nonzero exit code.");
            }
        });

        ValidateHeader(paths.ExportV5, isV5: true);
        ValidateHeader(paths.ExportRegedit4, isV5: false);
        ValidateHeader(paths.ExportSingleKeyV5, isV5: true);
        Console.WriteLine($"export: wrote {paths.ExportV5}, {paths.ExportRegedit4}, {paths.ExportSingleKeyV5}");
    }

    private static int ExportTo(string destination, string switchArg, string keyArg)
    {
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        int exit = RegeditProcess.RunToExit(new[] { switchArg, destination, keyArg }, ExportTimeout);
        if (!File.Exists(destination))
        {
            throw new FileNotFoundException($"regedit {switchArg} exited {exit} but did not create '{destination}'.");
        }

        return exit;
    }

    private static void ValidateHeader(string path, bool isV5)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (isV5)
        {
            byte[] bom = { 0xFF, 0xFE };
            if (bytes.Length < bom.Length || !bytes.AsSpan(0, 2).SequenceEqual(bom))
            {
                throw new InvalidDataException($"'{path}' does not start with the UTF-16LE BOM.");
            }

            string header = Encoding.Unicode.GetString(bytes, 2, Math.Min(bytes.Length - 2, 2 * 40));
            const string expected = "Windows Registry Editor Version 5.00";
            if (!header.StartsWith(expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{path}' header is not '{expected}'.");
            }
        }
        else
        {
            string header = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8));
            if (!header.StartsWith("REGEDIT4", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{path}' does not start with REGEDIT4.");
            }
        }
    }
}
