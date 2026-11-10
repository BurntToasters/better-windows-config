namespace Bwc.Oracle.Capture;

internal sealed class RepoPaths
{
    private RepoPaths(string root)
    {
        Root = root;
        OracleDocs = Path.Combine(root, "docs", "parity", "oracle");
        ExportsDir = Path.Combine(OracleDocs, "exports");
        SeedTreeDoc = Path.Combine(OracleDocs, "seed-tree.md");
        ExportV5 = Path.Combine(ExportsDir, "bwc-oracle-v5.reg");
        ExportRegedit4 = Path.Combine(ExportsDir, "bwc-oracle-regedit4.reg");
        ExportSingleKeyV5 = Path.Combine(ExportsDir, "bwc-oracle-many300-v5.reg");
        MetricsJson = Path.Combine(OracleDocs, "regedit-metrics.json");
        ScreenshotPng = Path.Combine(OracleDocs, "regedit-100dpi.png");
    }

    internal string Root { get; }
    internal string OracleDocs { get; }
    internal string ExportsDir { get; }
    internal string SeedTreeDoc { get; }
    internal string ExportV5 { get; }
    internal string ExportRegedit4 { get; }
    internal string ExportSingleKeyV5 { get; }
    internal string MetricsJson { get; }
    internal string ScreenshotPng { get; }

    // Walks up from the working directory (then from the executable's folder) to the folder holding global.json.
    internal static RepoPaths Locate()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                {
                    return new RepoPaths(dir.FullName);
                }

                dir = dir.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repo root (no global.json in any parent folder).");
    }
}
