using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Tests of <see cref="E2ETimings"/> without a window (docs/failure-modes/e2e-harness.md, section 46). The wait
/// cases go through the real <c>WaitUntil</c> helpers and the shared recorder, with explicit site arguments so no
/// other test can collide with them. The other cases use their own recorder instance.
/// </summary>
[TestClass]
public sealed class E2ETimingsTests
{
    private const string FakeSite = @"C:\work\tests\Bwc.E2E\Fake\TimeoutSite.cs";

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_ThatTimesOutIsCountedAsATimeout()
    {
        bool control = ControlTestSupport.WaitUntil(() => false, TimeSpan.FromMilliseconds(80), FakeSite, 11);
        bool shell = ShellRun.WaitUntil(() => false, TimeSpan.FromMilliseconds(80), FakeSite, 12);
        bool satisfied = ControlTestSupport.WaitUntil(() => true, TimeSpan.FromSeconds(1), FakeSite, 13);

        Assert.IsFalse(control, "A condition that never holds must still return false.");
        Assert.IsFalse(shell, "A condition that never holds must still return false.");
        Assert.IsTrue(satisfied, "A condition that holds must still return true.");

        IReadOnlyList<E2ETimings.WaitSite> waits = E2ETimings.Current.Waits();
        E2ETimings.WaitSite timedOutControl = Single(waits, "Fake/TimeoutSite.cs", 11);
        E2ETimings.WaitSite timedOutShell = Single(waits, "Fake/TimeoutSite.cs", 12);
        E2ETimings.WaitSite ok = Single(waits, "Fake/TimeoutSite.cs", 13);

        Assert.AreEqual(1, timedOutControl.Calls);
        Assert.AreEqual(1, timedOutControl.Timeouts);
        Assert.IsTrue(timedOutControl.TotalMs >= 50, $"A timed out wait of 80 ms recorded {timedOutControl.TotalMs} ms.");
        Assert.AreEqual(1, timedOutShell.Timeouts);
        Assert.AreEqual(1, ok.Calls);
        Assert.AreEqual(0, ok.Timeouts);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_TwoCallsFromTheSameLineAggregate()
    {
        for (int i = 0; i < 2; i++) { _ = ControlTestSupport.WaitUntil(() => i == 0, TimeSpan.FromMilliseconds(60), FakeSite, 21); }

        E2ETimings.WaitSite site = Single(E2ETimings.Current.Waits(), "Fake/TimeoutSite.cs", 21);
        Assert.AreEqual(2, site.Calls);
        Assert.AreEqual(1, site.Timeouts);
        Assert.IsTrue(site.MaxMs <= site.TotalMs, "The longest call cannot exceed the total.");
        Assert.IsTrue(site.MaxMs > 0, "The longest call of a polled wait is above zero.");

        // Another line of the same file is a separate site.
        _ = ControlTestSupport.WaitUntil(() => true, TimeSpan.FromSeconds(1), FakeSite, 22);
        Assert.AreEqual(1, Single(E2ETimings.Current.Waits(), "Fake/TimeoutSite.cs", 22).Calls);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_CallerFileAndLineComeFromTheCompiler()
    {
        bool satisfied = ControlTestSupport.WaitUntil(() => true, TimeSpan.FromSeconds(1)); int line = Here();

        Assert.IsTrue(satisfied);
        E2ETimings.WaitSite site = Single(E2ETimings.Current.Waits(), "Harness/E2ETimingsTests.cs", line);
        Assert.AreEqual(1, site.Calls);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Site_PathIsRelativeToTheTestProject()
    {
        Assert.AreEqual("Controls/ControlTestSupport.cs", E2ETimings.RelativeSite(@"C:\a\b\tests\Bwc.E2E\Controls\ControlTestSupport.cs"));
        Assert.AreEqual("Console/ConsoleShellSupport.cs", E2ETimings.RelativeSite("/_/tests/Bwc.E2E/Console/ConsoleShellSupport.cs"));
        Assert.AreEqual("Other.cs", E2ETimings.RelativeSite(@"D:\elsewhere\Other.cs"));
        Assert.AreEqual("(unknown)", E2ETimings.RelativeSite(string.Empty));
        Assert.AreEqual("(unknown)", E2ETimings.RelativeSite(null!));
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Recorder_ExceptionDoesNotEscape()
    {
        var recorder = new E2ETimings();
        recorder.Fault = () => throw new InvalidOperationException("injected fault");

        recorder.StartTest("Class", "Method");
        int id = recorder.SessionStarted("Bwc.exe", E2ETimings.Now(), startFailed: false);
        recorder.SessionClosed(id, E2ETimings.Now(), killed: false, viaClose: true);
        recorder.SessionDisposed(id);
        recorder.RecordWait("Any.cs", 1, E2ETimings.Now(), timedOut: true);
        recorder.FinishTest("Class", "Method", "Passed");
        JsonNode? json = recorder.ToJson();

        Assert.IsNull(json, "A recorder that cannot build its JSON returns nothing.");
        Assert.IsTrue(recorder.Problems().Count >= 6, "Each swallowed failure is noted as a problem.");
        Assert.IsTrue(recorder.Problems().All(p => p.Contains("injected fault", StringComparison.Ordinal)));

        string result = E2EResults.ToResultJson(new { schema = 2, pass = true }, recorder);
        JsonObject parsed = JsonNode.Parse(result)!.AsObject();
        Assert.AreEqual(2, (int)parsed["schema"]!);
        Assert.IsFalse(parsed.ContainsKey("timings"), "A failed recorder leaves the artifact without timings.");

        recorder.Fault = null;
        recorder.RecordWait("Any.cs", 2, E2ETimings.Now(), timedOut: false);
        Assert.AreEqual(1, recorder.Waits().Count, "The recorder works again once the fault is gone.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Json_HasTheDocumentedFields()
    {
        var recorder = new E2ETimings();
        recorder.StartTest("ShellTests", "OpensAndCloses");
        int id = recorder.SessionStarted("BwcRegedit.exe", E2ETimings.Now(), startFailed: false);
        recorder.SessionClosed(id, E2ETimings.Now(), killed: true, viaClose: true);
        recorder.SessionDisposed(id);
        recorder.RecordWait(@"C:\r\tests\Bwc.E2E\Controls\ControlTestSupport.cs", 70, E2ETimings.Now(), timedOut: true);
        recorder.FinishTest("ShellTests", "OpensAndCloses", "Passed");

        string result = E2EResults.ToResultJson(new { schema = 2, pass = true, checks = new[] { new { id = "a", pass = true } } }, recorder);
        JsonObject root = JsonNode.Parse(result)!.AsObject();

        Assert.AreEqual(2, (int)root["schema"]!, "The schema stays 2.");
        Assert.AreEqual("timings", root.Last().Key, "The new object is added after the existing fields.");
        JsonObject timings = root["timings"]!.AsObject();

        JsonObject test = timings["tests"]!.AsArray().Single()!.AsObject();
        Assert.AreEqual("ShellTests.OpensAndCloses", (string)test["test"]!);
        Assert.AreEqual("ShellTests", (string)test["class"]!);
        Assert.AreEqual("OpensAndCloses", (string)test["method"]!);
        Assert.AreEqual("Passed", (string)test["outcome"]!);
        Assert.IsNotNull(test["durationMs"]);
        Assert.AreEqual(id, (int)test["sessions"]!.AsArray().Single()!);

        JsonObject session = timings["sessions"]!.AsArray().Single()!.AsObject();
        Assert.AreEqual(id, (int)session["id"]!);
        Assert.AreEqual("BwcRegedit.exe", (string)session["exe"]!);
        Assert.AreEqual("ShellTests.OpensAndCloses", (string)session["test"]!);
        Assert.IsNotNull(session["startMs"]);
        Assert.IsNotNull(session["closeMs"]);
        Assert.IsTrue((bool)session["killed"]!);
        Assert.IsTrue((bool)session["closedViaClose"]!);
        Assert.IsNotNull(session["aliveMs"]);
        Assert.AreEqual(0, (int)timings["sessionsNotClosed"]!);

        JsonObject wait = timings["waits"]!.AsArray().Single()!.AsObject();
        Assert.AreEqual("Controls/ControlTestSupport.cs", (string)wait["file"]!);
        Assert.AreEqual(70, (int)wait["line"]!);
        Assert.AreEqual(1, (int)wait["calls"]!);
        Assert.AreEqual(1, (int)wait["timeouts"]!);
        Assert.IsNotNull(wait["totalMs"]);
        Assert.IsNotNull(wait["maxMs"]);

        JsonObject totals = timings["totals"]!.AsObject();
        foreach (string name in new[] { "testMs", "sessionStartMs", "sessionCloseMs", "waitMs" })
        {
            Assert.IsNotNull(totals[name], $"totals.{name} is missing.");
        }

        Assert.AreEqual(0, timings["problems"]!.AsArray().Count);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Session_NeverClosedAndNeverDisposedAreCounted()
    {
        var recorder = new E2ETimings();
        recorder.StartTest("Class", "Method");
        int disposedOnly = recorder.SessionStarted("A.exe", E2ETimings.Now(), startFailed: false);
        recorder.SessionDisposed(disposedOnly);
        int closed = recorder.SessionStarted("B.exe", E2ETimings.Now(), startFailed: false);
        recorder.SessionClosed(closed, E2ETimings.Now(), killed: false, viaClose: true);
        recorder.SessionDisposed(closed);
        _ = recorder.SessionStarted("C.exe", E2ETimings.Now(), startFailed: false);
        recorder.FinishTest("Class", "Method", "Passed");

        JsonObject timings = recorder.ToJson()!.AsObject();
        Assert.AreEqual(1, (int)timings["sessionsNotClosed"]!, "One session was only disposed.");
        Assert.AreEqual(1, (int)timings["sessionsNeverDisposed"]!, "One session was never disposed.");
        JsonObject never = timings["sessions"]!.AsArray().Select(s => s!.AsObject()).Single(s => (string)s["exe"]! == "C.exe");
        Assert.IsNull(never["aliveMs"], "A session that is never disposed has no end time.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Test_WithoutAStartedClockHasNoDurationAndIsCounted()
    {
        var recorder = new E2ETimings();
        int id = recorder.SessionStarted("Early.exe", E2ETimings.Now(), startFailed: false);
        recorder.FinishTest("Class", "NoStart", "Failed");

        JsonObject timings = recorder.ToJson()!.AsObject();
        JsonObject test = timings["tests"]!.AsArray().Single()!.AsObject();
        Assert.IsNull(test["durationMs"], "No start means no duration, never zero.");
        Assert.AreEqual("Failed", (string)test["outcome"]!);
        Assert.AreEqual(1, (int)timings["testsWithoutStart"]!);
        Assert.AreEqual(0.0, (double)timings["totals"]!["testMs"]!, "A test without a start adds nothing to the total.");
        Assert.AreEqual(E2ETimings.NoTest, (string)timings["sessions"]!.AsArray().Single()!["test"]!);
        Assert.IsTrue(id > 0);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Json_TooLargeIsReducedToTotals()
    {
        var recorder = new E2ETimings { MaxJsonBytes = 3000 };
        for (int i = 0; i < 100; i++)
        {
            recorder.StartTest("BigClass", "Method" + i);
            recorder.FinishTest("BigClass", "Method" + i, "Passed");
        }

        JsonObject timings = recorder.ToJson()!.AsObject();
        Assert.IsTrue((bool)timings["truncated"]!, "An oversized record says it was reduced.");
        Assert.IsNull(timings["tests"], "The long lists are dropped.");
        Assert.IsNotNull(timings["totals"], "The totals stay.");
        Assert.IsTrue(JsonSerializer.Serialize(timings).Length < 3000);
        Assert.IsTrue(recorder.Problems().Any(p => p.Contains("truncated", StringComparison.Ordinal)));
    }

    private static int Here([CallerLineNumber] int line = 0) => line;

    private static E2ETimings.WaitSite Single(IReadOnlyList<E2ETimings.WaitSite> waits, string file, int line)
    {
        var matches = waits.Where(w => w.File == file && w.Line == line).ToArray();
        Assert.AreEqual(1, matches.Length, $"Expected one wait site at {file}:{line}.");
        return matches[0];
    }
}
