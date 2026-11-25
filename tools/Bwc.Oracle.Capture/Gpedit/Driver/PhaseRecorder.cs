using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Records how long the named phases of one operation take, in the order they end. A phase that wraps others ends after them and is
// listed after them. The recorder only measures: it adds no wait and no UI call, and an exception inside a phase is recorded and
// then continues unchanged. Failure mode 32 in docs/failure-modes/oracle-gpedit-driver.md.
internal sealed class PhaseRecorder
{
    private readonly List<KeyValuePair<string, long>> phases = new();

    // Starts a phase that ends when the returned scope is disposed (use it in a using block).
    internal PhaseScope Begin(string name) => new(this, name);

    internal void Run(string name, Action action)
    {
        using (Begin(name))
        {
            action();
        }
    }

    internal T Run<T>(string name, Func<T> func)
    {
        using (Begin(name))
        {
            return func();
        }
    }

    internal void Add(string name, long ms) => phases.Add(new KeyValuePair<string, long>(name, ms));

    // The phases as { "name", "ms" } objects. Never throws: a failure here drops the phases and must not cost an operation its result.
    internal JsonArray? ToJson()
    {
        try
        {
            return new JsonArray(phases.Select(p => (JsonNode?)new JsonObject { ["name"] = p.Key, ["ms"] = p.Value }).ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    internal readonly struct PhaseScope : IDisposable
    {
        private readonly PhaseRecorder recorder;
        private readonly string name;
        private readonly long startTicks;

        internal PhaseScope(PhaseRecorder recorder, string name)
        {
            this.recorder = recorder;
            this.name = name;
            startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose() => recorder.Add(name, (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds);
    }
}
