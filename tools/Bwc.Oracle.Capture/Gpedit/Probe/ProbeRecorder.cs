using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// A step that cannot run because a precondition is missing. Recorded as skipped, the run goes on.
internal sealed class ProbeSkipException : Exception
{
    internal ProbeSkipException(string reason)
        : base(reason)
    {
    }
}

// Runs probe steps and keeps probe.json current: after every step the file is rewritten, so a hang or crash keeps all earlier results.
internal sealed class ProbeRecorder
{
    private readonly string path;
    private readonly JsonArray steps = new();

    internal ProbeRecorder(string path, JsonObject header)
    {
        this.path = path;
        Root = header;
        Root["steps"] = steps;
    }

    internal JsonObject Root { get; }

    internal int Failed { get; private set; }

    // Records success with the data returned, or the exception. Cancellation is not a step failure and propagates.
    internal bool Step(string name, Func<JsonNode?> action)
    {
        var started = DateTime.UtcNow;
        var entry = new JsonObject { ["name"] = name, ["startedUtc"] = started.ToString("O") };
        bool ok;
        try
        {
            entry["data"] = action();
            ok = true;
            Console.WriteLine($"probe: step {name}: ok");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProbeSkipException skip)
        {
            ok = false;
            entry["skipped"] = true;
            entry["error"] = skip.Message;
            Console.WriteLine($"probe: step {name}: skipped ({skip.Message})");
        }
        catch (Exception ex)
        {
            ok = false;
            Failed++;
            entry["error"] = ex.GetType().Name + ": " + ex.Message;
            entry["exception"] = ex.ToString();
            Console.WriteLine($"probe: step {name}: FAILED ({ex.GetType().Name}: {ex.Message})");
        }

        entry["ok"] = ok;
        entry["ms"] = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        steps.Add(entry);
        Flush();
        return ok;
    }

    internal void Flush()
    {
        Root["failedSteps"] = Failed;
        Report.WriteJson(path, Root);
    }
}
