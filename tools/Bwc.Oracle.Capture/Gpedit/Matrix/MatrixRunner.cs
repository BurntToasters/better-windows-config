using System.Text.Json;
using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// Runs the selected cases: one GpeditSession per group (group H: one per case, each with its own bad template), a reset before every case,
// one OpsRunner pass per case, the golden written only when the case is complete. A case that fails never changes an earlier golden.
internal sealed class MatrixRunner
{
    private const int MaxFailuresInARow = 3;
    private const long MinFreeBytes = 500L * 1024 * 1024;

    private readonly RepoPaths repo;
    private readonly MatrixPlan plan;
    private readonly bool resume;
    private readonly DateTime startedLocal = DateTime.Now;
    private readonly List<JsonObject> summary = new();
    private int failuresInARow;
    private bool stopped;

    internal MatrixRunner(RepoPaths repo, MatrixPlan plan, bool resume)
    {
        this.repo = repo;
        this.plan = plan;
        this.resume = resume;
    }

    internal int Done { get; private set; }

    internal int Skipped { get; private set; }

    internal int Failed { get; private set; }

    internal int Changed { get; private set; }

    internal int NotRun { get; private set; }

    internal static string MatrixOutDir(GpeditPaths paths) => Path.Combine(paths.OutputDir, "matrix");

    internal static long FreeBytes(RepoPaths repo)
    {
        string root = Path.GetPathRoot(repo.Root) ?? repo.Root;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    internal static bool EnoughDisk(RepoPaths repo) => FreeBytes(repo) >= MinFreeBytes;

    internal int Run(IReadOnlyList<MatrixCase> selected)
    {
        GpeditPaths paths = GpeditPaths.Real(repo);
        GpeditSession.Preflight(paths, FixtureTemplates.Load(repo));
        if (!EnoughDisk(repo))
        {
            throw new GpeditRefusal($"refused: less than {MinFreeBytes / 1024 / 1024} MB free on the drive of {repo.Root}.");
        }

        Directory.CreateDirectory(MatrixOutDir(paths));
        foreach (string group in MatrixPlan.GroupNames)
        {
            CaseOutput.Recover(repo, group);
        }

        var groups = selected.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (IGrouping<string, MatrixCase> group in groups)
        {
            if (stopped)
            {
                NotRun += group.Count();
                continue;
            }

            List<MatrixCase> todo = group.ToList();
            if (group.Key == "H")
            {
                foreach (MatrixCase c in todo)
                {
                    if (stopped)
                    {
                        NotRun++;
                        continue;
                    }

                    GpeditSession.Run("matrix-H-" + c.Id, c.Templates(repo), ctx => RunCases(ctx, new List<MatrixCase> { c }));
                }
            }
            else
            {
                GpeditSession.Run("matrix-" + group.Key, FixtureTemplates.Load(repo), ctx => RunCases(ctx, todo));
            }
        }

        WriteSummary(paths);
        return Failed > 0 || stopped ? 1 : 0;
    }

    private void RunCases(GpeditContext ctx, List<MatrixCase> cases)
    {
        for (int i = 0; i < cases.Count; i++)
        {
            MatrixCase c = cases[i];
            ctx.ThrowIfCancelled();
            if (stopped)
            {
                NotRun += cases.Count - i;
                return;
            }

            if (resume && CaseOutput.IsComplete(repo, c))
            {
                Skipped++;
                ctx.Log($"[{c.FullId}] already complete, skipped (--resume)");
                continue;
            }

            if (!EnoughDisk(repo))
            {
                stopped = true;
                NotRun += cases.Count - i;
                ctx.Log($"stopping: less than {MinFreeBytes / 1024 / 1024} MB free");
                return;
            }

            ctx.Log($"[{c.FullId}] {c.Description}");
            CaseOutcome outcome = RunOne(ctx, c, out double seconds);
            summary.Add(new JsonObject
            {
                ["case"] = c.FullId,
                ["complete"] = outcome.Complete,
                ["changedAgainstEarlierGolden"] = outcome.Changed,
                ["changedFiles"] = new JsonArray(outcome.ChangedFiles.Select(f => (JsonNode?)f).ToArray()),
                ["optionalFailures"] = outcome.OptionalFailures,
                ["failure"] = outcome.Failure,
                ["seconds"] = (int)seconds,
            });
            if (outcome.Complete)
            {
                Done++;
                failuresInARow = 0;
                if (outcome.Changed)
                {
                    Changed++;
                }

                ctx.Log($"[{c.FullId}] done in {(int)seconds} s{(outcome.Changed ? "  CHANGED against the earlier golden: " + string.Join(", ", outcome.ChangedFiles) : string.Empty)}" +
                        (outcome.OptionalFailures > 0 ? $"  ({outcome.OptionalFailures} optional operation(s) failed, recorded)" : string.Empty));
            }
            else
            {
                Failed++;
                failuresInARow++;
                ctx.Log($"[{c.FullId}] FAILED: {outcome.Failure}");
                if (failuresInARow >= MaxFailuresInARow)
                {
                    stopped = true;
                    NotRun += cases.Count - i - 1;
                    ctx.Log($"stopping: {MaxFailuresInARow} cases failed in a row, so something is wrong with the machine or the plan. Fix it, then run again with --resume.");
                    return;
                }
            }

            WriteSummary(ctx.Paths);
        }
    }

    private CaseOutcome RunOne(GpeditContext ctx, MatrixCase c, out double seconds)
    {
        DateTime began = DateTime.UtcNow;
        string name = $"matrix-{c.Group}-{c.Id}";
        string resultPath = Path.Combine(ctx.Paths.OutputDir, "ops", name + ".result.json");
        string shotsDir = Path.Combine(ctx.Paths.OutputDir, "ops", name);
        string keep = Path.Combine(MatrixOutDir(ctx.Paths), c.Group, c.Id);
        CaseOutcome outcome;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.WriteLine($"[{c.FullId}] taking too long, closing classic");
            StrayMmc.Close(startedLocal);
        }, null, TimeSpan.FromSeconds((c.EstimatedSeconds * 4) + 180), Timeout.InfiniteTimeSpan);
        try
        {
            StrayMmc.Close(startedLocal);
            CaseReset.Reset(ctx.Paths, c.Start);
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }

            if (Directory.Exists(shotsDir))
            {
                FileOps.DeleteTree(shotsDir);
            }

            var file = new OpsFile { Name = name, Description = c.Description, Ops = c.Ops };
            OpsRunner.RunSummary? run = null;
            string? runError = null;
            try
            {
                run = OpsRunner.Run(ctx, file, name);
            }
            catch (Exception ex) when (ex is DriverException or InvalidOperationException or IOException or TimeoutException)
            {
                runError = ex.Message;
            }

            if (!File.Exists(resultPath))
            {
                outcome = Failure(runError ?? "no result file was written", 0);
            }
            else
            {
                JsonObject result = JsonNode.Parse(File.ReadAllText(resultPath))!.AsObject();
                if (runError is not null && result["startError"] is null)
                {
                    result["startError"] = runError;
                }

                if (!CaseOutput.Evaluate(c, result, out int optionalFailures, out string? why))
                {
                    outcome = Failure(why ?? runError ?? "the case did not complete", optionalFailures);
                }
                else
                {
                    var scrub = new Scrubber(plan.Fixture.AllowedStrings, c.Authored, c.KeepRegistry);
                    outcome = CaseOutput.Write(repo, c, result, scrub, optionalFailures);
                }
            }

            MoveLogs(resultPath, shotsDir, keep);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            outcome = Failure(ex.Message, 0);
        }
        finally
        {
            StrayMmc.Close(startedLocal);
        }

        seconds = (DateTime.UtcNow - began).TotalSeconds;
        return outcome;
    }

    private static CaseOutcome Failure(string message, int optionalFailures) =>
        new(false, false, optionalFailures, message, new List<string>());

    // The untracked log of a case: result.json and the screenshots, replaced on every run.
    private static void MoveLogs(string resultPath, string shotsDir, string keep)
    {
        if (Directory.Exists(keep))
        {
            FileOps.DeleteTree(keep);
        }

        Directory.CreateDirectory(keep);
        if (File.Exists(resultPath))
        {
            File.Move(resultPath, Path.Combine(keep, "result.json"));
        }

        if (Directory.Exists(shotsDir))
        {
            Directory.Move(shotsDir, Path.Combine(keep, "screenshots"));
        }
    }

    private void WriteSummary(GpeditPaths paths)
    {
        var root = new JsonObject
        {
            ["startedLocal"] = startedLocal.ToString("O"),
            ["done"] = Done,
            ["skipped"] = Skipped,
            ["failed"] = Failed,
            ["changedAgainstEarlierGolden"] = Changed,
            ["notRun"] = NotRun,
            ["stoppedEarly"] = stopped,
            ["cases"] = new JsonArray(summary.Select(s => (JsonNode?)s.DeepClone()).ToArray()),
        };
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        Util.WriteUtf8NoBom(Path.Combine(MatrixOutDir(paths), "summary.json"), json);
    }
}
