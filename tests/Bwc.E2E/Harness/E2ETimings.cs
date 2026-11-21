using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// In-memory record of where the run spends its time: one row per test, one per app session, and one per wait call
/// site. It only measures. Every entry point swallows its own failure and adds a problem entry instead, so a timing
/// bug can never fail a test (docs/failure-modes/e2e-harness.md, section 46). <see cref="E2EResults.Finish"/> writes
/// <see cref="ToJson"/> as the top-level <c>timings</c> object of the result file.
/// </summary>
internal sealed class E2ETimings
{
    /// <summary>The test name of a session or a wait that happens while no test is running.</summary>
    internal const string NoTest = "(none)";

    private const int MaxProblems = 20;
    private const string UnknownSite = "(unknown)";

    private readonly object _sync = new();
    private readonly List<TestRow> _tests = [];
    private readonly Dictionary<string, TestRow> _open = new(StringComparer.Ordinal);
    private readonly List<SessionRow> _sessions = [];
    private readonly Dictionary<(string File, int Line), WaitRow> _waits = [];
    private readonly List<string> _problems = [];
    private string _currentTest = NoTest;
    private int _testsWithoutStart;

    /// <summary>The recorder the harness writes to.</summary>
    internal static E2ETimings Current { get; } = new();

    /// <summary>The largest serialized size of the timings object. A larger one is reduced to its totals.</summary>
    internal int MaxJsonBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Runs inside every guarded entry point. Only the tests of the recorder set it.</summary>
    internal Action? Fault { get; set; }

    /// <summary>One wait call site with the calls made from it.</summary>
    internal readonly record struct WaitSite(string File, int Line, int Calls, double TotalMs, double MaxMs, int Timeouts);

    internal static long Now() => Stopwatch.GetTimestamp();

    /// <summary>The source file of a wait site relative to tests/Bwc.E2E, with forward slashes.</summary>
    internal static string RelativeSite(string? callerFilePath)
    {
        if (string.IsNullOrWhiteSpace(callerFilePath))
        {
            return UnknownSite;
        }

        string normalized = callerFilePath.Replace('\\', '/');
        const string Root = "/Bwc.E2E/";
        int index = normalized.LastIndexOf(Root, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? normalized[(index + Root.Length)..] : normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The class and method of a test, the same key style as the <c>test.&lt;Class&gt;.&lt;Test&gt;.outcome</c> note.
    /// </summary>
    internal static (string Class, string Method) TestKeyOf(TestContext context)
    {
        string className = context.FullyQualifiedTestClassName ?? "UnknownClass";
        return (className[(className.LastIndexOf('.') + 1)..], context.TestName ?? "UnknownTest");
    }

    internal void TestStarted(TestContext context) =>
        Guard(() =>
        {
            (string cls, string method) = TestKeyOf(context);
            StartTest(cls, method);
        });

    internal void TestFinished(TestContext context) =>
        Guard(() =>
        {
            (string cls, string method) = TestKeyOf(context);
            FinishTest(cls, method, context.CurrentTestOutcome.ToString());
        });

    internal void StartTest(string cls, string method) =>
        Guard(() =>
        {
            var row = new TestRow(cls, method) { StartTimestamp = Now() };
            lock (_sync)
            {
                _tests.Add(row);
                _open[row.Key] = row;
                _currentTest = row.Key;
            }
        });

    internal void FinishTest(string cls, string method, string outcome) =>
        Guard(() =>
        {
            var key = $"{cls}.{method}";
            lock (_sync)
            {
                if (_open.Remove(key, out TestRow? row))
                {
                    row.DurationMs = MsSince(row.StartTimestamp);
                    row.Outcome = outcome;
                }
                else
                {
                    _tests.Add(new TestRow(cls, method) { Outcome = outcome });
                    _testsWithoutStart++;
                }

                if (string.Equals(_currentTest, key, StringComparison.Ordinal))
                {
                    _currentTest = NoTest;
                }
            }
        });

    /// <summary>
    /// Registers a session whose process started at <paramref name="startTimestamp"/>. Returns its id, or 0 when not recorded.
    /// </summary>
    internal int SessionStarted(string exe, long startTimestamp, bool startFailed) =>
        Guard(
            () =>
            {
                lock (_sync)
                {
                    int id = _sessions.Count + 1;
                    _sessions.Add(new SessionRow(id, exe, _currentTest)
                    {
                        StartMs = MsSince(startTimestamp),
                        StartTimestamp = startTimestamp,
                        StartFailed = startFailed,
                    });
                    if (_open.TryGetValue(_currentTest, out TestRow? test))
                    {
                        test.Sessions.Add(id);
                    }

                    return id;
                }
            },
            0);

    /// <summary>
    /// Adds one close to a session. <paramref name="viaClose"/> is true when a test called Close, and false for the
    /// close inside Dispose.
    /// </summary>
    internal void SessionClosed(int id, long closeStartTimestamp, bool killed, bool viaClose) =>
        Guard(() =>
        {
            double ms = MsSince(closeStartTimestamp);
            lock (_sync)
            {
                SessionRow? row = FindSession(id);
                if (row is null)
                {
                    return;
                }

                row.CloseMs = (row.CloseMs ?? 0) + ms;
                row.Killed |= killed;
                row.ClosedViaClose |= viaClose;
            }
        });

    internal void SessionDisposed(int id) =>
        Guard(() =>
        {
            lock (_sync)
            {
                SessionRow? row = FindSession(id);
                if (row is null)
                {
                    return;
                }

                row.AliveMs = MsSince(row.StartTimestamp);
                row.Disposed = true;
            }
        });

    /// <summary>Adds one call to the wait site. The file is the caller file path as the compiler gave it.</summary>
    internal void RecordWait(string? file, int line, long startTimestamp, bool timedOut) =>
        Guard(() =>
        {
            double ms = MsSince(startTimestamp);
            var key = (RelativeSite(file), line);
            lock (_sync)
            {
                if (!_waits.TryGetValue(key, out WaitRow? row))
                {
                    row = new WaitRow();
                    _waits[key] = row;
                }

                row.Calls++;
                row.TotalMs += ms;
                row.MaxMs = Math.Max(row.MaxMs, ms);
                if (timedOut)
                {
                    row.Timeouts++;
                }
            }
        });

    /// <summary>The wait sites, the slowest total first.</summary>
    internal IReadOnlyList<WaitSite> Waits()
    {
        lock (_sync)
        {
            return _waits
                .Select(pair => new WaitSite(pair.Key.File, pair.Key.Line, pair.Value.Calls, Round(pair.Value.TotalMs), Round(pair.Value.MaxMs), pair.Value.Timeouts))
                .OrderByDescending(site => site.TotalMs)
                .ThenBy(site => site.File, StringComparer.Ordinal)
                .ThenBy(site => site.Line)
                .ToArray();
        }
    }

    /// <summary>The failures the recorder swallowed.</summary>
    internal IReadOnlyList<string> Problems()
    {
        lock (_sync)
        {
            return _problems.ToArray();
        }
    }

    /// <summary>
    /// The <c>timings</c> object, or null when it cannot be built. An object above <see cref="MaxJsonBytes"/> is
    /// reduced to its counts and totals with <c>truncated</c> set.
    /// </summary>
    internal JsonNode? ToJson() =>
        Guard<JsonNode?>(
            () =>
            {
                object full = BuildFull(out object totals, out int sessionsNotClosed, out int sessionsNeverDisposed, out int testsWithoutStart);
                JsonNode? node = JsonSerializer.SerializeToNode(full);
                if (node is not null && node.ToJsonString().Length <= MaxJsonBytes)
                {
                    return node;
                }

                AddProblem($"truncated: the timings object was larger than {MaxJsonBytes} bytes and was reduced to its totals.");
                return JsonSerializer.SerializeToNode(new
                {
                    truncated = true,
                    sessionsNotClosed,
                    sessionsNeverDisposed,
                    testsWithoutStart,
                    totals,
                    problems = Problems(),
                });
            },
            null);

    private object BuildFull(out object totals, out int sessionsNotClosed, out int sessionsNeverDisposed, out int testsWithoutStart)
    {
        TestRow[] tests;
        SessionRow[] sessions;
        lock (_sync)
        {
            tests = _tests.ToArray();
            sessions = _sessions.ToArray();
            testsWithoutStart = _testsWithoutStart;
        }

        IReadOnlyList<WaitSite> waits = Waits();
        sessionsNotClosed = sessions.Count(s => s.Disposed && !s.ClosedViaClose && !s.StartFailed);
        sessionsNeverDisposed = sessions.Count(s => !s.Disposed && !s.StartFailed);

        totals = new
        {
            testMs = Round(tests.Sum(t => t.DurationMs ?? 0)),
            sessionStartMs = Round(sessions.Sum(s => s.StartMs)),
            sessionCloseMs = Round(sessions.Sum(s => s.CloseMs ?? 0)),
            waitMs = Round(waits.Sum(w => w.TotalMs)),
            tests = tests.Length,
            sessions = sessions.Length,
            waitCalls = waits.Sum(w => w.Calls),
            waitTimeouts = waits.Sum(w => w.Timeouts),
        };

        return new
        {
            tests = tests.Select(t => new
            {
                test = t.Key,
                @class = t.Class,
                method = t.Method,
                durationMs = t.DurationMs,
                outcome = t.Outcome,
                sessions = t.Sessions.ToArray(),
            }).ToArray(),
            sessions = sessions.Select(s => new
            {
                id = s.Id,
                exe = s.Exe,
                test = s.Test,
                startMs = s.StartMs,
                startFailed = s.StartFailed,
                closeMs = s.CloseMs is { } close ? Round(close) : (double?)null,
                killed = s.Killed,
                closedViaClose = s.ClosedViaClose,
                aliveMs = s.Disposed ? (double?)s.AliveMs : null,
            }).ToArray(),
            sessionsNotClosed,
            sessionsNeverDisposed,
            testsWithoutStart,
            waits = waits.Select(w => new
            {
                file = w.File,
                line = w.Line,
                calls = w.Calls,
                totalMs = w.TotalMs,
                maxMs = w.MaxMs,
                timeouts = w.Timeouts,
            }).ToArray(),
            totals,
            problems = Problems(),
        };
    }

    private SessionRow? FindSession(int id) => id >= 1 && id <= _sessions.Count ? _sessions[id - 1] : null;

    private static double MsSince(long timestamp) => Round(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);

    private static double Round(double value) => Math.Round(value, 1);

    private T Guard<T>(Func<T> body, T fallback)
    {
        try
        {
            Fault?.Invoke();
            return body();
        }
        catch (Exception ex)
        {
            AddProblem($"{ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }

    private void Guard(Action body) =>
        Guard(
            () =>
            {
                body();
                return 0;
            },
            0);

    private void AddProblem(string text)
    {
        try
        {
            lock (_sync)
            {
                if (_problems.Count < MaxProblems)
                {
                    _problems.Add(text);
                }
            }
        }
        catch (Exception)
        {
            // Nothing is left to record a failure of the problem list itself.
        }
    }

    private sealed class TestRow(string cls, string method)
    {
        internal string Class { get; } = cls;

        internal string Method { get; } = method;

        internal string Key => $"{Class}.{Method}";

        internal long StartTimestamp { get; init; }

        internal double? DurationMs { get; set; }

        internal string? Outcome { get; set; }

        internal List<int> Sessions { get; } = [];
    }

    private sealed class SessionRow(int id, string exe, string test)
    {
        internal int Id { get; } = id;

        internal string Exe { get; } = exe;

        internal string Test { get; } = test;

        internal long StartTimestamp { get; init; }

        internal double StartMs { get; init; }

        internal bool StartFailed { get; init; }

        internal double? CloseMs { get; set; }

        internal bool Killed { get; set; }

        internal bool ClosedViaClose { get; set; }

        internal bool Disposed { get; set; }

        internal double AliveMs { get; set; }
    }

    private sealed class WaitRow
    {
        internal int Calls { get; set; }

        internal double TotalMs { get; set; }

        internal double MaxMs { get; set; }

        internal int Timeouts { get; set; }
    }
}
