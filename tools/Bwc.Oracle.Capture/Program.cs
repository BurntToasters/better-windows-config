namespace Bwc.Oracle.Capture;

internal static class Program
{
    private const string Usage =
        "usage: Bwc.Oracle.Capture <seed|export|metrics|cleanup|all> [--allow-dpi]\n" +
        "  seed     recreate HKCU\\Software\\BWC-Oracle and write docs/parity/oracle/seed-tree.md\n" +
        "  export   write classic regedit exports (v5, REGEDIT4, single subkey) to docs/parity/oracle/exports\n" +
        "  metrics  measure classic regedit UI metrics, write regedit-metrics.json and regedit-100dpi.png\n" +
        "  cleanup  delete HKCU\\Software\\BWC-Oracle\n" +
        "  all      seed, export, metrics, cleanup (cleanup and applet restore always run)\n" +
        "  --allow-dpi  let metrics run at a DPI other than 96 (the DPI is recorded)";

    private static int Main(string[] args)
    {
        string command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
        bool allowDpi = args.Contains("--allow-dpi", StringComparer.Ordinal);

        try
        {
            RepoPaths paths = RepoPaths.Locate();
            switch (command)
            {
                case "seed":
                    SeedCommand.Run(paths);
                    return 0;

                case "export":
                    ExportCommand.Run(paths);
                    return 0;

                case "metrics":
                    MetricsCommand.Run(paths, allowDpi);
                    return 0;

                case "cleanup":
                    RegistryGuard.DeleteOracleTree();
                    Console.WriteLine("cleanup: HKCU\\Software\\BWC-Oracle deleted (if it existed)");
                    return 0;

                case "all":
                    return RunAll(paths, allowDpi);

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED ({command}): {ex}");
            return 1;
        }
    }

    private static int RunAll(RepoPaths paths, bool allowDpi)
    {
        AppletSnapshot before = AppletSnapshot.Capture();
        Console.WriteLine($"all: applet state before: {before.Describe()}");
        int result = 0;
        try
        {
            SeedCommand.Run(paths);
            ExportCommand.Run(paths);
            MetricsCommand.Run(paths, allowDpi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"all: step failed: {ex}");
            result = 1;
        }
        finally
        {
            try
            {
                RegistryGuard.DeleteOracleTree();
                Console.WriteLine("all: cleanup done, HKCU\\Software\\BWC-Oracle removed");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"all: cleanup failed: {ex}");
                result = 1;
            }

            try
            {
                before.Restore();
                Console.WriteLine($"all: applet state restored: {before.Describe()}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"all: applet restore failed: {ex}");
                result = 1;
            }
        }

        return result;
    }
}
