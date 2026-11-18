using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// The oracle fixture templates (good set only): the two ADMX files and their en-US ADML from docs/parity/oracle/gpedit/fixture.
internal static class FixtureTemplates
{
    private static readonly string[] Files =
    {
        "BwcOracleBase.admx",
        "BwcOracle.admx",
        "en-US\\BwcOracleBase.adml",
        "en-US\\BwcOracle.adml",
    };

    internal static string Dir(RepoPaths repo) => Path.Combine(repo.Root, "docs", "parity", "oracle", "gpedit", "fixture");

    internal static TemplateSet Load(RepoPaths repo)
    {
        string dir = Dir(repo);
        var items = new List<TemplateSet.Item>();
        foreach (string relative in Files)
        {
            string source = Path.Combine(dir, relative);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"The oracle fixture template {source} was not found.");
            }

            items.Add(new TemplateSet.Item(source, relative));
        }

        return new TemplateSet(items);
    }
}

// `gpedit ops <file.json> [--dry-run]`: validate an operations file, then (without --dry-run) run it in a GpeditSession with the oracle
// fixture templates. Exit codes: 0 all operations ok, 1 an operation or the run failed, 2 refused or bad file.
internal static class OpsCommand
{
    internal const string Usage = "usage: Bwc.Oracle.Capture gpedit ops <file.json> [--dry-run]";

    internal static int Run(RepoPaths repo, string[] rest, bool dryRun)
    {
        string? path = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Skip(1).FirstOrDefault();
        if (path is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string full = Path.GetFullPath(path);
        (OpsFile? file, List<string> problems) = OpsFile.Load(full);
        if (file is null || problems.Count > 0)
        {
            Console.Error.WriteLine($"gpedit ops: {full} is not valid ({problems.Count} problems):");
            foreach (string problem in problems)
            {
                Console.Error.WriteLine("  " + problem);
            }

            return 2;
        }

        string name = SafeFileName(file.Name ?? Path.GetFileNameWithoutExtension(full));
        if (dryRun)
        {
            DryRun(repo, full, file, name);
            return 0;
        }

        TemplateSet templates = FixtureTemplates.Load(repo);
        OpsRunner.RunSummary? summary = null;
        GpeditSession.Run("ops", templates, ctx => summary = OpsRunner.Run(ctx, file, name));
        return summary is { Failed: 0, Skipped: 0 } ? 0 : 1;
    }

    private static string SafeFileName(string text)
    {
        string safe = string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')).Trim('.');
        return safe.Length == 0 ? "ops" : safe;
    }

    // Prints the plan and checks what it can without touching anything. Works unelevated; nothing is started or written.
    private static void DryRun(RepoPaths repo, string full, OpsFile file, string name)
    {
        GpeditPaths paths = GpeditPaths.Real(repo);
        Console.WriteLine("gpedit ops --dry-run: nothing is changed, started or written.");
        Console.WriteLine($"file: {full}");
        Console.WriteLine($"name: {name}" + (file.Description is null ? string.Empty : $"  ({file.Description})"));
        Console.WriteLine();
        Console.WriteLine("preflight (read only):");
        Console.WriteLine($"  elevated: {(SessionGuards.IsElevated() ? "yes" : "NO, a real run refuses with exit code 2")}");
        Console.WriteLine($"  interactive desktop: {SessionGuards.InteractiveProblem() ?? "ok"}");
        Console.WriteLine($"  backup manifest {paths.ManifestPath}: {(File.Exists(paths.ManifestPath) ? "EXISTS, a real run refuses until `gpedit restore`" : "absent")}");
        TemplateSet templates = TemplateSet.Empty;
        try
        {
            templates = FixtureTemplates.Load(repo);
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"  fixture templates: MISSING ({ex.Message})");
        }

        try
        {
            GpeditSession.RequireTemplatesFree(paths, templates);
            Console.WriteLine("  template destinations: free");
        }
        catch (GpeditRefusal refusal)
        {
            Console.WriteLine("  template destinations: " + refusal.Message);
        }

        Console.WriteLine();
        Console.WriteLine("templates copied for the run (removed again by the restore):");
        foreach (TemplateSet.Item item in templates.Items)
        {
            Console.WriteLine($"  {item.SourcePath}  ->  {Path.Combine(paths.PolicyDefinitionsDir, item.RelativePath)}");
        }

        Console.WriteLine();
        Console.WriteLine($"operations ({file.Ops.Count}, all valid):");
        for (int i = 0; i < file.Ops.Count; i++)
        {
            Console.WriteLine($"  {i,3}  {file.Ops[i].Describe()}");
        }

        Console.WriteLine();
        Console.WriteLine("kinds used: " + string.Join(", ", file.Ops.Select(o => o.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal)));
        Console.WriteLine("kinds known: " + string.Join(", ", GpeditOp.KindNames.OrderBy(k => k, StringComparer.Ordinal)));
        Console.WriteLine();
        Console.WriteLine($"outputs: {Path.Combine(paths.OutputDir, "ops", name + ".result.json")} and {Path.Combine(paths.OutputDir, "ops", name)}\\*.png");
        Console.WriteLine("every operation closes what it opened; the session restores GroupPolicy, the MMC state, the templates and the BWC-Oracle keys.");
    }
}
