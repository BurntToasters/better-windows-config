namespace Bwc.Oracle.Capture;

// Entry point. scripts/oracle.js runs one command per capture (`npm run oracle -- --only <capture>`).
// Every command runs inside OracleSession: GUI mutex, applet snapshot, BWC-Oracle cleanup, applet restore.
internal static class Program
{
    private const string Usage =
        "usage: Bwc.Oracle.Capture <command> [--allow-dpi]\n" +
        "  export    classic regedit /e and /a exports of the seeded tree to docs/parity/oracle/exports (tracked)\n" +
        "  metrics   main window metrics and screenshot, regedit-metrics.json and regedit-100dpi.png (tracked numbers)\n" +
        "  dialogs   classic dialog screenshots and control rectangles (regedit-dialog-rects.json, dialogs/*.png)\n" +
        "  menus     popup menu metrics (regedit-menu-metrics.json)\n" +
        "  import    malformed .reg import behavior under BWC-Oracle (import-behavior.json)\n" +
        "  find      Find hit sequence under BWC-Oracle (find-sequence.json)\n" +
        "  sort      value list order and header click behavior (sort-order.json)\n" +
        "  permissions  Advanced Security Settings entry list and Applies-to choices (permissions-inherit.json)\n" +
        "  cleanup   delete HKCU\\Software\\BWC-Oracle\n" +
        "  --allow-dpi  record UI captures at a DPI other than 96 (the DPI is written into each output)";

    private static int Main(string[] args)
    {
        string command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
        bool allowDpi = args.Contains("--allow-dpi", StringComparer.Ordinal);

        try
        {
            RepoPaths paths = RepoPaths.Locate();
            switch (command)
            {
                case "export":
                    OracleSession.Run("export", () =>
                    {
                        SeedCommand.Run(paths);
                        ExportCommand.Run(paths);
                    });
                    return 0;

                case "metrics":
                    OracleSession.Run("metrics", () =>
                    {
                        SeedCommand.Run(paths);
                        MetricsCommand.Run(paths, allowDpi);
                    });
                    return 0;

                case "dialogs":
                    OracleSession.Run("dialogs", () =>
                    {
                        SeedCommand.Run(paths);
                        DialogCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "menus":
                    OracleSession.Run("menus", () =>
                    {
                        SeedCommand.Run(paths);
                        MenuCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "import":
                    OracleSession.Run("import", () => ImportCapture.Run(paths));
                    return 0;

                case "find":
                    OracleSession.Run("find", () => FindCapture.Run(paths, allowDpi));
                    return 0;

                case "sort":
                    OracleSession.Run("sort", () => SortCapture.Run(paths, allowDpi));
                    return 0;

                case "permissions":
                    OracleSession.Run("permissions", () => PermissionsCapture.Run(paths, allowDpi));
                    return 0;

                case "cleanup":
                    GuiLock.Run(() => RegistryGuard.DeleteOracleTree());
                    Console.WriteLine("cleanup: HKCU\\Software\\BWC-Oracle deleted (if it existed)");
                    return 0;

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
}
