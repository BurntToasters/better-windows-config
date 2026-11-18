using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// `gpedit matrix [--only A,B,...] [--case <Group/Id>] [--resume] [--dry-run] [--list]`. Exit codes: 0 every selected case complete,
// 1 a case failed or the run stopped early, 2 bad arguments, a refusal, or an invalid plan.
internal static class MatrixCommand
{
    internal const string Usage =
        "usage: Bwc.Oracle.Capture gpedit matrix [--only A,B,...] [--case <Group/Id>] [--resume] [--dry-run] [--list]\n" +
        "  --only A,B   run only these groups (A to H)\n" +
        "  --case G/Id  run one case, for example B/Pol_Decimal_MinMax (see --list)\n" +
        "  --resume     skip cases whose golden is complete and was captured with the current plan\n" +
        "  --dry-run    print the case counts per group and the preflight; touches nothing, works unelevated\n" +
        "  --list       print every case with its estimated duration, and the total per group; touches nothing, works unelevated\n" +
        "  a real run needs an elevated terminal on the interactive desktop. Take a VM snapshot first.";

    internal static int Run(RepoPaths repo, string[] rest)
    {
        bool resume = false;
        bool dryRun = false;
        bool list = false;
        var only = new HashSet<string>(StringComparer.Ordinal);
        string? caseId = null;
        string[] args = rest.SkipWhile(a => !string.Equals(a, "matrix", StringComparison.Ordinal)).Skip(1).ToArray();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--resume":
                    resume = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--list":
                    list = true;
                    break;
                case "--only" when i + 1 < args.Length:
                    foreach (string g in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string upper = g.ToUpperInvariant();
                        if (!MatrixPlan.GroupNames.Contains(upper))
                        {
                            return Bad($"unknown group '{g}'. Groups are {string.Join(", ", MatrixPlan.GroupNames)}.");
                        }

                        only.Add(upper);
                    }

                    break;
                case "--case" when i + 1 < args.Length:
                    caseId = args[++i];
                    break;
                default:
                    return Bad($"unknown or incomplete argument '{args[i]}'.");
            }
        }

        MatrixPlan plan = MatrixPlan.Build(repo);
        if (plan.Problems.Count > 0)
        {
            Console.Error.WriteLine($"gpedit matrix: the plan is not valid ({plan.Problems.Count} problems):");
            foreach (string problem in plan.Problems.Take(40))
            {
                Console.Error.WriteLine("  " + problem);
            }

            return 2;
        }

        IEnumerable<MatrixCase> chosen = plan.Cases;
        if (only.Count > 0)
        {
            chosen = chosen.Where(c => only.Contains(c.Group));
        }

        if (caseId is not null)
        {
            chosen = chosen.Where(c => string.Equals(c.FullId, caseId, StringComparison.OrdinalIgnoreCase)
                || (!caseId.Contains('/') && string.Equals(c.Id, caseId, StringComparison.OrdinalIgnoreCase)));
        }

        List<MatrixCase> selected = chosen.ToList();
        if (selected.Count == 0)
        {
            return Bad("no case matches. `--list` shows every case.");
        }

        if (list)
        {
            PrintList(selected);
            return 0;
        }

        if (dryRun)
        {
            PrintDryRun(repo, plan, selected, resume);
            return 0;
        }

        var runner = new MatrixRunner(repo, plan, resume);
        Console.WriteLine($"gpedit matrix: {selected.Count} cases, about {MatrixPlan.Duration(selected.Sum(c => c.EstimatedSeconds))}. Do not touch the keyboard or mouse while it runs.");
        int code = runner.Run(selected);
        Console.WriteLine($"gpedit matrix: {runner.Done} done, {runner.Skipped} skipped, {runner.Failed} failed, {runner.NotRun} not run, {runner.Changed} changed against an earlier golden.");
        Console.WriteLine("Goldens: docs/parity/oracle/gpedit/goldens (tracked). Logs and screenshots: artifacts/oracle/gpedit/matrix (not tracked).");
        if (code != 0)
        {
            Console.WriteLine("Some cases did not complete: see artifacts/oracle/gpedit/matrix/summary.json, then run again with --resume.");
        }

        Console.WriteLine("The session restored GroupPolicy and the MMC state. If anything looks off, revert the VM snapshot, or run `gpupdate /force` in an administrator terminal.");
        return code;
    }

    private static int Bad(string message)
    {
        Console.Error.WriteLine("gpedit matrix: " + message);
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static void PrintCounts(List<MatrixCase> selected)
    {
        Console.WriteLine("group  cases  estimate    what");
        foreach (IGrouping<string, MatrixCase> group in selected.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{group.Key,-6} {group.Count(),5}  {MatrixPlan.Duration(group.Sum(c => c.EstimatedSeconds)),-10}  {MatrixPlan.GroupTitles[group.Key]}");
        }

        Console.WriteLine($"total  {selected.Count,5}  {MatrixPlan.Duration(selected.Sum(c => c.EstimatedSeconds)),-10}  (estimate; classic start and close dominate)");
    }

    private static void PrintList(List<MatrixCase> selected)
    {
        Console.WriteLine("gpedit matrix --list: nothing is changed, started or written.");
        foreach (IGrouping<string, MatrixCase> group in selected.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine($"group {group.Key}: {MatrixPlan.GroupTitles[group.Key]}");
            foreach (MatrixCase c in group)
            {
                Console.WriteLine($"  {c.FullId,-60} {c.Ops.Count,3} ops  {(int)c.EstimatedSeconds,4} s  {c.Description}");
            }

            Console.WriteLine($"  estimated {MatrixPlan.Duration(group.Sum(c => c.EstimatedSeconds))} for {group.Count()} cases");
        }

        Console.WriteLine();
        PrintCounts(selected);
    }

    private static void PrintDryRun(RepoPaths repo, MatrixPlan plan, List<MatrixCase> selected, bool resume)
    {
        GpeditPaths paths = GpeditPaths.Real(repo);
        Console.WriteLine("gpedit matrix --dry-run: nothing is changed, started or written.");
        Console.WriteLine();
        PrintCounts(selected);
        Console.WriteLine();
        Console.WriteLine("preflight (read only):");
        Console.WriteLine($"  elevated: {(SessionGuards.IsElevated() ? "yes" : "NO, a real run refuses with exit code 2")}");
        Console.WriteLine($"  interactive desktop: {SessionGuards.InteractiveProblem() ?? "ok"}");
        Console.WriteLine($"  backup manifest {paths.ManifestPath}: {(File.Exists(paths.ManifestPath) ? "EXISTS, a real run refuses until `gpedit restore`" : "absent")}");
        Console.WriteLine($"  free disk: {MatrixRunner.FreeBytes(repo) / 1024 / 1024} MB ({(MatrixRunner.EnoughDisk(repo) ? "enough" : "TOO LITTLE, a real run refuses")})");
        try
        {
            TemplateSet templates = FixtureTemplates.Load(repo);
            GpeditSession.RequireTemplatesFree(paths, templates);
            Console.WriteLine("  template destinations: free");
        }
        catch (Exception ex) when (ex is GpeditRefusal or FileNotFoundException)
        {
            Console.WriteLine("  fixture templates: " + ex.Message);
        }

        int complete = selected.Count(c => CaseOutput.IsComplete(repo, c));
        Console.WriteLine($"  goldens already complete with the current plan: {complete} of {selected.Count} ({(resume ? "--resume skips them" : "a run without --resume captures them again")})");
        Console.WriteLine($"  plan check: {plan.Cases.Count} cases, {plan.Cases.Sum(c => c.Ops.Count)} operations, all valid");
        Console.WriteLine();
        Console.WriteLine("outputs: docs/parity/oracle/gpedit/goldens/<group>/<case> (tracked), artifacts/oracle/gpedit/matrix (not tracked)");
    }
}
