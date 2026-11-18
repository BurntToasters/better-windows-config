using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Probe;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Executes a list of GpeditOp inside a GpeditSession. Per operation it records ok or the error, what the operation observed, which GPO
// files changed (with the settled before and after), the gpt.ini version, the live registry under the BWC-Oracle keys and a screenshot.
// After every operation, whatever the operation opened is closed (docs/failure-modes/oracle-gpedit-driver.md sections 23 and 24).
// The result file is rewritten after every operation.
internal static class OpsRunner
{
    internal sealed record RunSummary(int Ok, int Failed, int Skipped, string ResultPath);

    internal static RunSummary Run(GpeditContext ctx, OpsFile file, string name)
    {
        string outDir = Path.Combine(ctx.Paths.OutputDir, "ops");
        string resultPath = Path.Combine(outDir, name + ".result.json");
        Directory.CreateDirectory(outDir);
        var results = new JsonArray();
        var root = new JsonObject
        {
            ["tool"] = "gpedit ops",
            ["name"] = name,
            ["description"] = file.Description,
            ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["os"] = Report.OsInfo(),
            ["elevated"] = SessionGuards.IsElevated(),
            ["dpiAwareness"] = GpeditDpi.Result.DeepClone(),
            ["operationCount"] = file.Ops.Count,
            ["ops"] = results,
        };
        int ok = 0;
        int failed = 0;
        int skipped = 0;
        GpeditDriver? driver = null;
        string? stopReason = null;
        try
        {
            driver = GpeditDriver.Start(ctx, Path.Combine(outDir, name));
            root["pid"] = driver.Pid;
            driver.LastSnapshot = Snapshot(ctx.Paths, "start");
            root["liveRegistryAtStart"] = LiveRegistry.Read();

            for (int i = 0; i < file.Ops.Count; i++)
            {
                GpeditOp op = file.Ops[i];
                if (stopReason is not null)
                {
                    skipped++;
                    results.Add(new JsonObject { ["index"] = i, ["kind"] = op.Kind, ["label"] = op.Label, ["ok"] = false, ["skipped"] = true, ["reason"] = stopReason });
                    Write(resultPath, root, ok, failed, skipped);
                    continue;
                }

                JsonObject entry;
                try
                {
                    entry = RunOne(ctx, driver, op, i, out bool fatal, out string? fatalReason);
                    if (fatal)
                    {
                        stopReason = fatalReason;
                    }
                }
                catch (OperationCanceledException)
                {
                    root["cancelled"] = true;
                    Write(resultPath, root, ok, failed, skipped);
                    throw;
                }

                results.Add(entry);
                if (entry["ok"]!.GetValue<bool>())
                {
                    ok++;
                }
                else
                {
                    failed++;
                }

                ctx.Log($"op {i} {op.Kind}{(op.Label is null ? string.Empty : " [" + op.Label + "]")}: {(entry["ok"]!.GetValue<bool>() ? "ok" : "FAILED (" + entry["error"] + ")")}");
                Write(resultPath, root, ok, failed, skipped);
            }
        }
        catch (DriverException ex) when (driver is null)
        {
            root["startError"] = ex.Message;
            root["startEvidence"] = ex.Evidence?.DeepClone();
            failed++;
            Write(resultPath, root, ok, failed, skipped);
            throw;
        }
        finally
        {
            if (driver is not null)
            {
                try
                {
                    root["close"] = driver.Close();
                }
                catch (Exception ex) when (ex is DriverException or InvalidOperationException)
                {
                    root["close"] = new JsonObject { ["error"] = ex.Message };
                }

                root["finishedUtc"] = DateTime.UtcNow.ToString("O");
                Write(resultPath, root, ok, failed, skipped);
            }
        }

        ctx.Log($"wrote {resultPath}: {ok} ok, {failed} failed, {skipped} skipped");
        return new RunSummary(ok, failed, skipped, resultPath);
    }

    private static void Write(string path, JsonObject root, int ok, int failed, int skipped)
    {
        root["okCount"] = ok;
        root["failedCount"] = failed;
        root["skippedCount"] = skipped;
        string json = Report.Redact(root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n")) + "\n";
        Util.WriteUtf8NoBom(path, json);
    }

    private static JsonObject Snapshot(GpeditPaths paths, string label)
    {
        JsonObject snapshot = PolFiles.Snapshot(paths, label);
        snapshot.Remove("liveRegistry");
        return snapshot;
    }

    private static JsonObject RunOne(GpeditContext ctx, GpeditDriver driver, GpeditOp op, int index, out bool fatal, out string? fatalReason)
    {
        fatal = false;
        fatalReason = null;
        var entry = new JsonObject
        {
            ["index"] = index,
            ["kind"] = op.Kind,
            ["label"] = op.Label,
            ["plan"] = op.Describe(),
            ["startedUtc"] = DateTime.UtcNow.ToString("O"),
        };
        DateTime started = DateTime.UtcNow;
        JsonObject beforeSnapshot = driver.LastSnapshot ?? Snapshot(ctx.Paths, "before");
        driver.LastSnapshot = beforeSnapshot;
        FileWatch watch = FileWatch.Begin(ctx.Paths, beforeSnapshot);
        bool ok = false;
        try
        {
            ctx.ThrowIfCancelled();
            driver.ThrowIfDead();
            entry["data"] = Execute(driver, op, entry);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PartialResultException partial)
        {
            entry["data"] = partial.Partial;
            Fail(entry, partial.InnerException ?? partial, ref fatal, ref fatalReason);
        }
        catch (Exception ex)
        {
            Fail(entry, ex, ref fatal, ref fatalReason);
        }

        try
        {
            JsonArray left = driver.EnsureNoDialogs();
            int menus = driver.CloseMenus();
            if (left.Count > 0 || menus > 0)
            {
                entry["epilogue"] = new JsonObject { ["dialogsClosed"] = left, ["menusClosed"] = menus };
            }
        }
        catch (DriverException ex)
        {
            entry["epilogueError"] = ex.Message;
            entry["epilogueEvidence"] = ex.Evidence?.DeepClone();
            if (ex.Fatal)
            {
                fatal = true;
                fatalReason = "stopped after operation " + index + ": " + ex.Message;
            }
        }

        if (driver.HasExited)
        {
            fatal = true;
            fatalReason ??= "mmc.exe exited during operation " + index;
        }

        if (!op.CanWrite && !driver.HasExited)
        {
            try
            {
                JsonObject settle = watch.Settle(ctx.Token, graceMs: 150, waitRegistry: false);
                if (settle["changed"]!.GetValue<bool>())
                {
                    settle = watch.Settle(ctx.Token, graceMs: 1200, waitRegistry: true);
                    entry["unexpectedFileChange"] = settle;
                    ok = false;
                    entry["error"] ??= "files changed during an operation that must not write";
                    if (watch.AfterSnapshot is not null)
                    {
                        driver.LastSnapshot = watch.AfterSnapshot;
                    }
                }
            }
            catch (IOException ex)
            {
                entry["fileCheckError"] = ex.Message;
            }
        }

        JsonObject afterSnapshot = driver.LastSnapshot ?? beforeSnapshot;
        entry["files"] = PolFiles.Diff(beforeSnapshot, afterSnapshot);
        entry["gptVersion"] = afterSnapshot["gptVersion"]?.DeepClone();
        entry["liveRegistry"] = LiveRegistry.Read();
        if (!ok || op.Screenshot is true)
        {
            if (!driver.HasExited)
            {
                entry["screenshot"] = driver.Shot(driver.MainWindow, $"{index:000}-{op.Kind}");
            }
        }

        entry["ok"] = ok;
        entry["ms"] = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        return entry;
    }

    private static void Fail(JsonObject entry, Exception ex, ref bool fatal, ref string? fatalReason)
    {
        entry["error"] = ex.GetType().Name + ": " + ex.Message;
        if (ex is DriverException driverException)
        {
            entry["error"] = driverException.Message;
            entry["evidence"] = driverException.Evidence?.DeepClone();
            if (driverException.Fatal)
            {
                fatal = true;
                fatalReason = "stopped: " + driverException.Message;
            }
        }
        else
        {
            entry["exception"] = ex.ToString();
        }
    }

    // The node an operation names must be the selected one; it is selected first when it is not.
    private static void EnsureNode(GpeditDriver driver, GpeditOp op, JsonObject entry)
    {
        if (op.Root is null || op.Path is null)
        {
            return;
        }

        if (driver.IsSelected(op.Root, op.Path))
        {
            entry["node"] = "already selected: " + op.Root + "/" + string.Join("/", op.Path);
            return;
        }

        entry["node"] = driver.SelectNode(op.Root, op.Path);
    }

    private static JsonNode? Execute(GpeditDriver driver, GpeditOp op, JsonObject entry)
    {
        switch (op.Kind)
        {
            case "selectNode":
                return driver.SelectNode(op.Root!, op.Path!);

            case "treeChildren":
                return driver.TreeChildren(op.Root!, op.Path!);

            case "readList":
                EnsureNode(driver, op, entry);
                return driver.ReadList(op.MaxRows ?? 6000, op.NamesOnly ?? false);

            case "readStatus":
                EnsureNode(driver, op, entry);
                return driver.ReadStatus();

            case "selectTab":
                return driver.SelectTab(op.Tab!);

            case "editPolicy":
                EnsureNode(driver, op, entry);
                return driver.EditPolicy(op);

            case "dumpMenu":
                if (op.Menu == "Context")
                {
                    EnsureNode(driver, op, entry);
                    return driver.DumpContextMenu(op.Policy, op.Occurrence);
                }

                return driver.DumpTopMenu(op.Menu!);

            case "setFilter":
                EnsureNode(driver, op, entry);
                return driver.SetFilter(op.Filter!, op.Press is { Count: > 0 } ? op.Press[0] : "OK", op.MessageBoxAnswer);

            case "filterOn":
                EnsureNode(driver, op, entry);
                return driver.SetFilterOn(op.FilterOn!.Value);

            case "clearFilter":
                EnsureNode(driver, op, entry);
                return driver.ClearFilter();

            case "sortColumn":
                EnsureNode(driver, op, entry);
                return driver.SortColumn(op.Column!);

            case "snapshot":
                return new JsonObject { ["snapshot"] = Snapshot(driver.Context.Paths, op.Label ?? "snapshot"), ["liveRegistry"] = LiveRegistry.Read() };

            case "screenshot":
                return new JsonObject { ["screenshot"] = driver.Shot(driver.MainWindow, op.Name ?? "screenshot") };

            case "wait":
                DateTime end = DateTime.UtcNow + TimeSpan.FromMilliseconds(op.WaitMs!.Value);
                while (DateTime.UtcNow < end)
                {
                    driver.Context.ThrowIfCancelled();
                    Thread.Sleep(Math.Min(200, (int)Math.Max(1, (end - DateTime.UtcNow).TotalMilliseconds)));
                }

                return new JsonObject { ["waitedMs"] = op.WaitMs };

            case "dismissDialogs":
                return new JsonObject { ["handled"] = driver.EnsureNoDialogs() };

            default:
                throw new DriverException($"unknown kind '{op.Kind}'");
        }
    }
}
