using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// In-memory record of where the run spends its time: one row per test, one per app session, one per wait call
/// site, one per screenshot call site and one per counted element lookup. It only measures.
/// Every entry point swallows its own failure and adds a problem entry instead, so a timing
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
    private readonly Dictionary<(string File, int Line), CaptureRow> _captures = [];
    private readonly Dictionary<string, LookupRow> _lookups = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];
    private string _currentTest = NoTest;
    private int _testsWithoutStart;
    private bool _clampNoted;

    // How many timed waits are open on this thread. Only the outermost one adds to the totals.
    [ThreadStatic]
    private static int t_waitDepth;

    /// <summary>The recorder the harness writes to.</summary>
    internal static E2ETimings Current { get; } = new();

    /// <summary>The largest serialized size of the timings object. A larger one is reduced to its totals.</summary>
    internal int MaxJsonBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Runs inside every guarded entry point. Only the tests of the recorder set it.</summary>
    internal Action? Fault { get; set; }

    /// <summary>One wait call site with the calls made from it.</summary>
    internal readonly record struct WaitSite(
        string File,
        int Line,
        int Calls,
        double TotalMs,
        double MaxMs,
        int Timeouts,
        int NestedCalls = 0,
        double NestedMs = 0);

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

    /// <summary>
    /// Opens a timed wait on this thread and returns its start. The depth change is outside the guarded part, so a
    /// failing recorder cannot leave it unbalanced.
    /// </summary>
    internal long BeginWait()
    {
        t_waitDepth++;
        return Now();
    }

    /// <summary>
    /// Closes the wait opened by <see cref="BeginWait"/> and records it. It runs in a finally, so a throwing wait
    /// still lowers the depth. A wait that ends while another is still open is nested.
    /// </summary>
    internal void EndWait(string? file, int line, long startTimestamp, bool timedOut)
    {
        t_waitDepth = Math.Max(0, t_waitDepth - 1);
        AddWait(file, line, startTimestamp, timedOut, nested: t_waitDepth > 0);
    }

    /// <summary>
    /// Adds one call to the wait site without opening a scope (a wait that was not begun). It is nested when it
    /// runs inside a begun wait. The file is the caller file path as the compiler gave it.
    /// </summary>
    internal void RecordWait(string? file, int line, long startTimestamp, bool timedOut) =>
        AddWait(file, line, startTimestamp, timedOut, nested: t_waitDepth > 0);

    private void AddWait(string? file, int line, long startTimestamp, bool timedOut, bool nested) =>
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

                if (nested)
                {
                    row.NestedCalls++;
                    row.NestedMs += ms;
                    return;
                }

                row.Calls++;
                row.TotalMs += ms;
                row.MaxMs = Math.Max(row.MaxMs, ms);
                if (timedOut)
                {
                    row.Timeouts++;
                }

                if (_open.TryGetValue(_currentTest, out TestRow? test))
                {
                    test.WaitMs += ms;
                }
            }
        });

    /// <summary>
    /// Adds one screenshot call. A capture inside a timed wait stays in the capture rows but is left out of the
    /// test capture time, because the wait already holds it.
    /// </summary>
    internal void RecordCapture(string? file, int line, long startTimestamp, bool unsettled)
    {
        bool insideWait = t_waitDepth > 0;
        Guard(() =>
        {
            double ms = MsSince(startTimestamp);
            var key = (RelativeSite(file), line);
            lock (_sync)
            {
                if (!_captures.TryGetValue(key, out CaptureRow? row))
                {
                    row = new CaptureRow();
                    _captures[key] = row;
                }

                row.Calls++;
                row.TotalMs += ms;
                row.MaxMs = Math.Max(row.MaxMs, ms);
                if (unsettled)
                {
                    row.Unsettled++;
                }

                if (!insideWait && _open.TryGetValue(_currentTest, out TestRow? test))
                {
                    test.CaptureMs += ms;
                }
            }
        });
    }

    /// <summary>Adds one element lookup to the counter of that name.</summary>
    internal void RecordLookup(string name, long startTimestamp) =>
        Guard(() =>
        {
            double ms = MsSince(startTimestamp);
            lock (_sync)
            {
                if (!_lookups.TryGetValue(name, out LookupRow? row))
                {
                    row = new LookupRow();
                    _lookups[name] = row;
                }

                row.Calls++;
                row.TotalMs += ms;
            }
        });

    /// <summary>The wait sites, the slowest total first.</summary>
    internal IReadOnlyList<WaitSite> Waits()
    {
        lock (_sync)
        {
            return _waits
                .Select(pair => new WaitSite(
                    pair.Key.File,
                    pair.Key.Line,
                    pair.Value.Calls,
                    Round(pair.Value.TotalMs),
                    Round(pair.Value.MaxMs),
                    pair.Value.Timeouts,
                    pair.Value.NestedCalls,
                    Round(pair.Value.NestedMs)))
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
        KeyValuePair<(string File, int Line), CaptureRow>[] captureRows;
        KeyValuePair<string, LookupRow>[] lookupRows;
        lock (_sync)
        {
            tests = _tests.ToArray();
            sessions = _sessions.ToArray();
            captureRows = _captures.ToArray();
            lookupRows = _lookups.ToArray();
            testsWithoutStart = _testsWithoutStart;
        }

        IReadOnlyList<WaitSite> waits = Waits();
        var capture = captureRows
            .Select(pair => new
            {
                file = pair.Key.File,
                line = pair.Key.Line,
                calls = pair.Value.Calls,
                totalMs = Round(pair.Value.TotalMs),
                maxMs = Round(pair.Value.MaxMs),
                unsettled = pair.Value.Unsettled,
            })
            .OrderByDescending(c => c.totalMs)
            .ThenBy(c => c.file, StringComparer.Ordinal)
            .ThenBy(c => c.line)
            .ToArray();
        var lookups = lookupRows
            .Select(pair => new { name = pair.Key, calls = pair.Value.Calls, totalMs = Round(pair.Value.TotalMs) })
            .OrderByDescending(l => l.totalMs)
            .ThenBy(l => l.name, StringComparer.Ordinal)
            .ToArray();
        var testRows = tests.Select(t => BuildTest(t, sessions)).ToArray();
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
            nestedWaitCalls = waits.Sum(w => w.NestedCalls),
            nestedWaitMs = Round(waits.Sum(w => w.NestedMs)),
            captureMs = Round(capture.Sum(c => c.totalMs)),
            captureCalls = capture.Sum(c => c.calls),
            captureUnsettled = capture.Sum(c => c.unsettled),
            lookupMs = Round(lookups.Sum(l => l.totalMs)),
            lookupCalls = lookups.Sum(l => l.calls),
            untimedMs = Round(testRows.Sum(t => t.untimedMs ?? 0)),
            untimedClamped = testRows.Count(t => t.untimedClamped),
        };

        return new
        {
            tests = testRows,
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
                nestedCalls = w.NestedCalls,
                nestedMs = w.NestedMs,
            }).ToArray(),
            capture,
            lookups,
            totals,
            problems = Problems(),
        };
    }

    /// <summary>
    /// One test row. The residual is the duration minus its waits, its sessions and its captures, never below zero. A
    /// value below minus one millisecond is more than rounding: the row says so and one problem entry names the test.
    /// </summary>
    private TestRowJson BuildTest(TestRow test, SessionRow[] sessions)
    {
        double sessionMs = 0;
        foreach (SessionRow session in sessions)
        {
            if (string.Equals(session.Test, test.Key, StringComparison.Ordinal))
            {
                sessionMs += session.StartMs + (session.CloseMs ?? 0);
            }
        }

        double? untimed = null;
        bool clamped = false;
        if (test.DurationMs is { } duration)
        {
            double residual = duration - test.WaitMs - sessionMs - test.CaptureMs;
            clamped = residual < -1;
            untimed = Round(Math.Max(0, residual));
            if (clamped)
            {
                NoteClamp(test.Key, residual);
            }
        }

        return new TestRowJson(
            test.Key,
            test.Class,
            test.Method,
            test.DurationMs,
            test.Outcome,
            test.Sessions.ToArray(),
            Round(test.WaitMs),
            Round(sessionMs),
            Round(test.CaptureMs),
            untimed,
            clamped);
    }

    private void NoteClamp(string test, double residual)
    {
        bool first;
        lock (_sync)
        {
            first = !_clampNoted;
            _clampNoted = true;
        }

        if (first)
        {
            AddProblem($"untimed: the residual of {test} was {Round(residual)} ms and is clamped at 0 (the parts overlap or the clocks differ).");
        }
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

        internal double WaitMs { get; set; }

        internal double CaptureMs { get; set; }
    }

    private sealed record TestRowJson(
        string test,
        string @class,
        string method,
        double? durationMs,
        string? outcome,
        int[] sessions,
        double waitMs,
        double sessionMs,
        double captureMs,
        double? untimedMs,
        bool untimedClamped);

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

        internal int NestedCalls { get; set; }

        internal double NestedMs { get; set; }
    }

    private sealed class CaptureRow
    {
        internal int Calls { get; set; }

        internal double TotalMs { get; set; }

        internal double MaxMs { get; set; }

        internal int Unsettled { get; set; }
    }

    private sealed class LookupRow
    {
        internal int Calls { get; set; }

        internal double TotalMs { get; set; }
    }
}
