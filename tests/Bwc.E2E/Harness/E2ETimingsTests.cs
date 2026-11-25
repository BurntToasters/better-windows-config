using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bwc.E2E.AppServices;
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

    // The cases below are the ST0b failure modes (docs/failure-modes/e2e-harness.md, section 46). They were written
    // before the recorder code that makes them pass: nesting, the residual and the cost of a counter.

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_NestedWaitIsCountedOnceAndReportedSeparately()
    {
        bool outer = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.WaitUntil(() => true, TimeSpan.FromSeconds(1), FakeSite, 32),
            TimeSpan.FromSeconds(1),
            FakeSite,
            31);

        Assert.IsTrue(outer);
        IReadOnlyList<E2ETimings.WaitSite> waits = E2ETimings.Current.Waits();
        E2ETimings.WaitSite outerSite = Single(waits, "Fake/TimeoutSite.cs", 31);
        E2ETimings.WaitSite innerSite = Single(waits, "Fake/TimeoutSite.cs", 32);
        Assert.AreEqual(1, outerSite.Calls, "The outermost wait is the one that counts.");
        Assert.AreEqual(0, outerSite.NestedCalls);
        Assert.AreEqual(0, innerSite.Calls, "A wait inside another wait adds nothing to the calls.");
        Assert.AreEqual(0, innerSite.TotalMs, "A wait inside another wait adds nothing to the total.");
        Assert.AreEqual(1, innerSite.NestedCalls, "The nested call is reported on its own.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_WrapperHelperRecordsTheLineOfItsCaller()
    {
        bool satisfied = AppServicesSupport.WaitUntil(() => true, TimeSpan.FromSeconds(1)); int line = Here();

        Assert.IsTrue(satisfied);
        E2ETimings.WaitSite site = Single(E2ETimings.Current.Waits(), "Harness/E2ETimingsTests.cs", line);
        Assert.AreEqual(1, site.Calls, "The site is this line, not the wrapper line in AppServicesSupport.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_NestingEndsWithTheOuterWaitEvenWhenItThrows()
    {
        var recorder = new E2ETimings();

        try
        {
            long started = recorder.BeginWait();
            try
            {
                throw new InvalidOperationException("a condition that escapes");
            }
            finally
            {
                recorder.EndWait("A.cs", 1, started, timedOut: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The wait ended through finally, as the helpers do.
        }

        long next = recorder.BeginWait();
        recorder.EndWait("A.cs", 2, next, timedOut: false);

        E2ETimings.WaitSite after = Single(recorder.Waits(), "A.cs", 2);
        Assert.AreEqual(1, after.Calls, "A wait after a throwing wait is outermost again.");
        Assert.AreEqual(0, after.NestedCalls);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Wait_RecordWithoutBeginIsNestedOnlyInsideABegunWait()
    {
        var recorder = new E2ETimings();
        recorder.RecordWait("A.cs", 1, E2ETimings.Now(), timedOut: false);

        long outer = recorder.BeginWait();
        recorder.RecordWait("A.cs", 2, E2ETimings.Now(), timedOut: false);
        recorder.EndWait("A.cs", 3, outer, timedOut: false);

        IReadOnlyList<E2ETimings.WaitSite> waits = recorder.Waits();
        Assert.AreEqual(1, Single(waits, "A.cs", 1).Calls, "With no wait open it is the outermost.");
        Assert.AreEqual(1, Single(waits, "A.cs", 2).NestedCalls, "Inside a begun wait it is nested.");
        Assert.AreEqual(0, Single(waits, "A.cs", 2).Calls);
        Assert.AreEqual(1, Single(waits, "A.cs", 3).Calls, "A record without begin does not close the outer wait.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Json_NestedWaitIsNotInTheWaitTotalsOrTheTestWaitTime()
    {
        var recorder = new E2ETimings();
        recorder.StartTest("NestClass", "Nests");
        long outer = recorder.BeginWait();
        long inner = recorder.BeginWait();
        Thread.Sleep(60);
        recorder.EndWait("Inner.cs", 2, inner, timedOut: true);
        recorder.EndWait("Outer.cs", 1, outer, timedOut: false);
        recorder.FinishTest("NestClass", "Nests", "Passed");

        JsonObject timings = recorder.ToJson()!.AsObject();
        JsonObject test = timings["tests"]!.AsArray().Single()!.AsObject();
        JsonObject[] waits = timings["waits"]!.AsArray().Select(w => w!.AsObject()).ToArray();
        JsonObject outerWait = waits.Single(w => (string)w["file"]! == "Outer.cs");
        JsonObject innerWait = waits.Single(w => (string)w["file"]! == "Inner.cs");

        Assert.IsTrue((double)test["waitMs"]! >= 50, "The test wait time is the outer wait.");
        Assert.AreEqual((double)outerWait["totalMs"]!, (double)test["waitMs"]!, 0.001, "The inner wait is not added again.");
        Assert.AreEqual(1, (int)innerWait["nestedCalls"]!);
        Assert.AreEqual(0, (int)innerWait["calls"]!);
        Assert.AreEqual(0, (int)innerWait["timeouts"]!, "A nested timeout is not in the site timeouts.");
        Assert.IsTrue((double)innerWait["nestedMs"]! >= 50);
        JsonObject totals = timings["totals"]!.AsObject();
        Assert.AreEqual((double)outerWait["totalMs"]!, (double)totals["waitMs"]!, 0.001);
        Assert.AreEqual(1, (int)totals["nestedWaitCalls"]!);
        Assert.AreEqual(1, (int)totals["waitCalls"]!);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Residual_IsTheDurationMinusWaitSessionAndCapture()
    {
        var recorder = new E2ETimings();
        recorder.StartTest("ResClass", "Adds");
        Thread.Sleep(30);

        long waitStart = recorder.BeginWait();
        Thread.Sleep(40);
        recorder.EndWait("W.cs", 1, waitStart, timedOut: false);

        long sessionStart = E2ETimings.Now();
        Thread.Sleep(30);
        int id = recorder.SessionStarted("A.exe", sessionStart, startFailed: false);
        long closeStart = E2ETimings.Now();
        Thread.Sleep(20);
        recorder.SessionClosed(id, closeStart, killed: false, viaClose: true);
        recorder.SessionDisposed(id);

        long captureStart = E2ETimings.Now();
        Thread.Sleep(25);
        recorder.RecordCapture("C.cs", 1, captureStart, unsettled: false);
        Thread.Sleep(30);
        recorder.FinishTest("ResClass", "Adds", "Passed");

        JsonObject timings = recorder.ToJson()!.AsObject();
        JsonObject test = timings["tests"]!.AsArray().Single()!.AsObject();
        double duration = (double)test["durationMs"]!;
        double wait = (double)test["waitMs"]!;
        double session = (double)test["sessionMs"]!;
        double capture = (double)test["captureMs"]!;
        double untimed = (double)test["untimedMs"]!;

        Assert.IsTrue(wait >= 30, $"waitMs {wait}");
        Assert.IsTrue(session >= 40, $"sessionMs {session} is the start plus the close.");
        Assert.IsTrue(capture >= 20, $"captureMs {capture}");
        Assert.AreEqual(duration - wait - session - capture, untimed, 0.5, "The residual is what the parts leave.");
        Assert.IsTrue(untimed >= 50, $"The two plain sleeps of 30 ms are untimed, got {untimed}.");
        Assert.IsFalse((bool)test["untimedClamped"]!);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Residual_NeverNegativeAndTheClampIsVisible()
    {
        var recorder = new E2ETimings();
        long early = E2ETimings.Now();
        Thread.Sleep(80);

        // The wait started before the test clock, so its time is larger than the test duration.
        recorder.StartTest("SkewClass", "Overlaps");
        recorder.RecordWait("W.cs", 1, early, timedOut: false);
        recorder.FinishTest("SkewClass", "Overlaps", "Passed");

        JsonObject timings = recorder.ToJson()!.AsObject();
        JsonObject test = timings["tests"]!.AsArray().Single()!.AsObject();
        Assert.AreEqual(0.0, (double)test["untimedMs"]!, "A negative residual is clamped at zero.");
        Assert.IsTrue((bool)test["untimedClamped"]!, "The clamp is marked on the row.");
        Assert.AreEqual(1, (int)timings["totals"]!["untimedClamped"]!);
        Assert.IsTrue(recorder.Problems().Any(p => p.Contains("untimed", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Residual_TestWithoutAClockHasNoResidual()
    {
        var recorder = new E2ETimings();
        recorder.FinishTest("Class", "NoStart", "Failed");

        JsonObject test = recorder.ToJson()!["tests"]!.AsArray().Single()!.AsObject();
        Assert.IsNull(test["untimedMs"], "No duration means no residual, never zero.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Capture_SitesAggregateAndCountUnsettled()
    {
        var recorder = new E2ETimings();
        recorder.StartTest("CapClass", "Captures");
        long first = E2ETimings.Now();
        Thread.Sleep(20);
        recorder.RecordCapture(@"C:\r\tests\Bwc.E2E\Dialogs\Shots.cs", 10, first, unsettled: false);
        long second = E2ETimings.Now();
        Thread.Sleep(20);
        recorder.RecordCapture(@"C:\r\tests\Bwc.E2E\Dialogs\Shots.cs", 10, second, unsettled: true);
        recorder.RecordCapture(@"C:\r\tests\Bwc.E2E\Dialogs\Shots.cs", 11, E2ETimings.Now(), unsettled: false);

        // A capture inside a timed wait is in the capture rows, but the test does not subtract it twice.
        long wait = recorder.BeginWait();
        long inside = E2ETimings.Now();
        Thread.Sleep(30);
        recorder.RecordCapture("Dialogs/Shots.cs", 12, inside, unsettled: false);
        recorder.EndWait("W.cs", 1, wait, timedOut: false);
        recorder.FinishTest("CapClass", "Captures", "Passed");

        JsonObject timings = recorder.ToJson()!.AsObject();
        JsonObject[] capture = timings["capture"]!.AsArray().Select(c => c!.AsObject()).ToArray();
        JsonObject site = capture.Single(c => (int)c["line"]! == 10);
        Assert.AreEqual("Dialogs/Shots.cs", (string)site["file"]!);
        Assert.AreEqual(2, (int)site["calls"]!);
        Assert.AreEqual(1, (int)site["unsettled"]!);
        Assert.IsTrue((double)site["totalMs"]! >= 30);
        Assert.IsTrue((double)site["maxMs"]! <= (double)site["totalMs"]!);
        Assert.AreEqual(1, (int)capture.Single(c => (int)c["line"]! == 11)["calls"]!);

        JsonObject totals = timings["totals"]!.AsObject();
        Assert.AreEqual(4, (int)totals["captureCalls"]!);
        Assert.AreEqual(1, (int)totals["captureUnsettled"]!);
        Assert.AreEqual(capture.Sum(c => (double)c["totalMs"]!), (double)totals["captureMs"]!, 0.001);

        JsonObject test = timings["tests"]!.AsArray().Single()!.AsObject();
        Assert.IsTrue((double)test["captureMs"]! >= 30, "The captures outside a wait are the test capture time.");
        Assert.IsTrue((double)test["captureMs"]! < (double)totals["captureMs"]!, "The capture inside the wait is left out.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Lookup_CountsCallsAndTimePerName()
    {
        var recorder = new E2ETimings();
        for (int i = 0; i < 3; i++)
        {
            long started = E2ETimings.Now();
            Thread.Sleep(10);
            recorder.RecordLookup("EditingDriver.FindElement", started);
        }

        recorder.RecordLookup("ContextMenuDriver.FindAllDescendants", E2ETimings.Now());

        JsonObject timings = recorder.ToJson()!.AsObject();
        JsonObject[] lookups = timings["lookups"]!.AsArray().Select(l => l!.AsObject()).ToArray();
        JsonObject find = lookups.Single(l => (string)l["name"]! == "EditingDriver.FindElement");
        Assert.AreEqual(3, (int)find["calls"]!);
        Assert.IsTrue((double)find["totalMs"]! >= 25);
        Assert.AreEqual(1, (int)lookups.Single(l => (string)l["name"]! == "ContextMenuDriver.FindAllDescendants")["calls"]!);
        Assert.AreEqual(4, (int)timings["totals"]!["lookupCalls"]!);
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Recorder_ExceptionDoesNotEscapeFromTheNewEntryPoints()
    {
        var recorder = new E2ETimings();
        recorder.Fault = () => throw new InvalidOperationException("injected fault");

        long started = recorder.BeginWait();
        recorder.EndWait("A.cs", 1, started, timedOut: false);
        recorder.RecordCapture("A.cs", 2, E2ETimings.Now(), unsettled: false);
        recorder.RecordLookup("X", E2ETimings.Now());

        Assert.IsTrue(recorder.Problems().Count >= 3, "Each swallowed failure is noted as a problem.");
        recorder.Fault = null;

        // A failed begin and end must not leave the thread inside a wait.
        long again = recorder.BeginWait();
        recorder.EndWait("A.cs", 3, again, timedOut: false);
        Assert.AreEqual(1, Single(recorder.Waits(), "A.cs", 3).Calls, "The depth is back at zero.");
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void Counters_CostFarLessThanAMillisecondPerCall()
    {
        var recorder = new E2ETimings();
        const int Rounds = 20000;
        long started = E2ETimings.Now();
        for (int i = 0; i < Rounds; i++)
        {
            long wait = recorder.BeginWait();
            recorder.EndWait("Hot.cs", 1 + (i % 50), wait, timedOut: false);
            recorder.RecordLookup("EditingDriver.FindElement", E2ETimings.Now());
            recorder.RecordCapture("Hot.cs", 100, E2ETimings.Now(), unsettled: false);
        }

        double perRound = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / Rounds;
        Assert.IsTrue(perRound < 0.25, $"One wait, one lookup and one capture counter took {perRound:0.0000} ms.");
        Assert.AreEqual(Rounds, recorder.Waits().Sum(w => w.Calls));
    }

    private static int Here([CallerLineNumber] int line = 0) => line;

    private static E2ETimings.WaitSite Single(IReadOnlyList<E2ETimings.WaitSite> waits, string file, int line)
    {
        var matches = waits.Where(w => w.File == file && w.Line == line).ToArray();
        Assert.AreEqual(1, matches.Length, $"Expected one wait site at {file}:{line}.");
        return matches[0];
    }
}
