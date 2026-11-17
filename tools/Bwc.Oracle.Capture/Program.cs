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
        "  tree-order  classic tree order of sibling keys, symbol and non-ASCII names (tree-order.json)\n" +
        "  permissions  Advanced Security Settings entry list and Applies-to choices (permissions-inherit.json)\n" +
        "  keys      navigation keys and accelerator commands, tree and list focus (regedit-keys.json)\n" +
        "  context   tree, value, list background and text edit context menus (regedit-context.json)\n" +
        "  binary    View > Display Binary Data variants and Modify Binary Data (regedit-binary.json)\n" +
        "  print     File > Print enable states and the Print dialog range panel (regedit-print.json)\n" +
        "  security  Edit > Permissions sheet and Advanced Security Settings windows (regedit-security.json)\n" +
        "  remote    Connect Network Registry to 127.0.0.1, menus, Import picker, Disconnect (regedit-remote.json)\n" +
        "  manifest  embedded RT_MANIFEST requestedExecutionLevel of regedit.exe (regedit-manifest.json)\n" +
        "  export-range  classic Export range panel controls 1280 to 1282 and the missing-branch box (regedit-export-range.json)\n" +
        "  export-text   classic File > Export, Text Files (*.txt), of BWC-Oracle; not measured (no file written), see regedit-export-text.json\n" +
        "  resource-viewers  Modify and Display... on REG_RESOURCE_LIST, FULL and REQUIREMENTS values (regedit-resource-viewers.json)\n" +
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

                case "tree-order":
                    OracleSession.Run("tree-order", () => TreeOrderCapture.Run(paths, allowDpi));
                    return 0;

                case "permissions":
                    OracleSession.Run("permissions", () => PermissionsCapture.Run(paths, allowDpi));
                    return 0;

                case "keys":
                    OracleSession.Run("keys", () =>
                    {
                        SeedCommand.Run(paths);
                        KeysCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "context":
                    OracleSession.Run("context", () =>
                    {
                        SeedCommand.Run(paths);
                        ContextCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "binary":
                    OracleSession.Run("binary", () =>
                    {
                        SeedCommand.Run(paths);
                        BinaryCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "print":
                    OracleSession.Run("print", () =>
                    {
                        SeedCommand.Run(paths);
                        PrintCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "security":
                    OracleSession.Run("security", () =>
                    {
                        SeedCommand.Run(paths);
                        SecurityCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "remote":
                    OracleSession.Run("remote", () =>
                    {
                        SeedCommand.Run(paths);
                        RemoteCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "export-range":
                    OracleSession.Run("export-range", () => ExportRangeCapture.Run(paths, allowDpi));
                    return 0;

                case "export-text":
                    OracleSession.Run("export-text", () =>
                    {
                        SeedCommand.Run(paths);
                        ExportTextCapture.Run(paths, allowDpi);
                    });
                    return 0;
                case "resource-viewers":
                    OracleSession.Run("resource-viewers", () =>
                    {
                        SeedCommand.Run(paths);
                        ResourceViewersCapture.Run(paths, allowDpi);
                    });
                    return 0;

                case "manifest":
                    // Reads the image resource only. regedit is not started, so no session or GUI lock is needed.
                    ManifestCapture.Run(paths);
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
