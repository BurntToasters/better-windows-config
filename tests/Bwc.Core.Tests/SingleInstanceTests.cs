using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

[TestClass]
public sealed class SingleInstanceTests
{
    private const int MaxMessageBytes = 64 * 1024;

    [TestMethod]
    public void FirstInstanceIsFirstAndSecondIsNot()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);

        Assert.IsTrue(first.IsFirstInstance);
        Assert.IsFalse(second.IsFirstInstance);
        Assert.AreEqual(first.InstanceName, second.InstanceName);
        Assert.IsTrue(first.InstanceName.Contains(toolId, StringComparison.Ordinal),
            "the diagnostic instance name should include the tool id.");
    }

    [TestMethod]
    public void EffectiveDefaultAndIgnoredRootsShareScope()
    {
        string? originalRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        string toolId = NewToolId();
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);
            string effectiveDefault = AppPaths.BaseDirectory;
            using var defaultRoot = SingleInstance.Create(toolId);
            Assert.IsTrue(defaultRoot.IsFirstInstance);

            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, "relative-bwc-test-root");
            using var ignoredRelative = SingleInstance.Create(toolId);
            Assert.IsFalse(ignoredRelative.IsFirstInstance,
                "a relative value ignored by AppPaths must share the effective default scope.");
            Assert.AreEqual(defaultRoot.InstanceName, ignoredRelative.InstanceName);

            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, effectiveDefault);
            using var explicitDefault = SingleInstance.Create(toolId);
            Assert.IsFalse(explicitDefault.IsFirstInstance,
                "an explicit accepted root equal to AppPaths.BaseDirectory must share the default scope.");
            Assert.AreEqual(defaultRoot.InstanceName, explicitDefault.InstanceName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, originalRoot);
        }
    }

    [TestMethod]
    public void DifferentEffectiveRootsHaveDifferentScopesAndEquivalentRootsShare()
    {
        string? originalRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        using var firstRoot = new TempDirectory();
        using var secondRoot = new TempDirectory();
        string toolId = NewToolId();
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, firstRoot.Path);
            using var first = SingleInstance.Create(toolId);
            Assert.IsTrue(first.IsFirstInstance);

            Environment.SetEnvironmentVariable(
                AppPaths.DataRootEnvironmentVariable,
                Path.TrimEndingDirectorySeparator(firstRoot.Path) + Path.DirectorySeparatorChar);
            using var sameRootWithSeparator = SingleInstance.Create(toolId);
            Assert.IsFalse(sameRootWithSeparator.IsFirstInstance,
                "a trailing separator must not create another scope.");
            Assert.AreEqual(first.InstanceName, sameRootWithSeparator.InstanceName);

            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, secondRoot.Path);
            using var second = SingleInstance.Create(toolId);
            Assert.IsTrue(second.IsFirstInstance,
                "a different accepted root must not activate an instance using the first root.");
            Assert.AreNotEqual(first.InstanceName, second.InstanceName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, originalRoot);
        }
    }

    [TestMethod]
    public void MessageRoundTripsToTheFirstInstance()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);
        string? receivedMessage = null;

        first.MessageReceived += message =>
        {
            receivedMessage = message;
            received.Set();
        };

        Assert.IsTrue(second.SendMessage("hello first instance"));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(5)), "the first instance did not receive the message.");
        Assert.AreEqual("hello first instance", receivedMessage);
    }

    [TestMethod]
    public void ClientWithServerReturnsSentResult()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);

        first.MessageReceived += _ => received.Set();

        Assert.AreEqual(
            SingleInstanceSendResult.Sent,
            second.TrySendMessage("named result"));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void ClientWithoutServerReturnsTimedOutResult()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);
        first.Dispose();

        var watch = Stopwatch.StartNew();
        SingleInstanceSendResult result = second.TrySendMessage("nobody is listening");
        watch.Stop();

        Assert.AreEqual(
            SingleInstanceSendResult.TimedOut,
            result,
            "a missing server must produce a named result, not an exception.");
        Assert.IsTrue(
            watch.Elapsed < TimeSpan.FromSeconds(5),
            "the client must stop at its bounded connect timeout.");
    }

    [TestMethod]
    public void MessageOverTheLimitThrowsWithoutConnecting()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);

        string oversized = new('a', MaxMessageBytes + 1);

        Assert.ThrowsExactly<ArgumentException>(() => second.SendMessage(oversized));
    }

    [TestMethod]
    public void DisposeThenRecreateBecomesFirstAgain()
    {
        string toolId = NewToolId();
        SingleInstance first = SingleInstance.Create(toolId);
        Assert.IsTrue(first.IsFirstInstance);
        first.Dispose();

        using var recreated = SingleInstance.Create(toolId);
        Assert.IsTrue(recreated.IsFirstInstance, "recreating after dispose should create a new first instance.");
    }

    [TestMethod]
    public void PipeNameTakenBacksOffTheAcceptLoop()
    {
        // The schedule is 50, 100, 200, 400, 800 and 1000 ms (capped). Attempts fall at 0, 50, 150, 350, 750 and
        // 1550 ms, so at most five fall inside the 1.5 s window and one more with timer slack. A spinning loop
        // makes thousands. The bound is an attempt count, not CPU time, so JIT and GC cannot move it. See
        // docs/failure-modes/core.md, "Accept attempts bound".
        const int MaxAttemptsInWindow = 7;
        const int MinAttemptsInWindow = 2;

        string toolId = NewToolId();
        string pipeName = LearnPipeName(toolId);

        // Hold the pipe the mutex-owning instance will try to create. The instance must back off instead of
        // retrying in a tight loop.
        using var hostile = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using var loser = SingleInstance.Create(toolId);
        Assert.IsTrue(loser.IsFirstInstance);

        var wall = Stopwatch.StartNew();
        Thread.Sleep(1500);
        wall.Stop();
        Assert.IsTrue(
            wall.Elapsed < TimeSpan.FromSeconds(5),
            "the observation window should stay bounded.");

        int attempts = loser.AcceptAttempts;
        Assert.IsTrue(
            attempts <= MaxAttemptsInWindow,
            "the accept loop made " + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " attempts in 1500 ms while the pipe name was taken; the backoff allows at most " +
            MaxAttemptsInWindow.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        Assert.IsTrue(
            attempts >= MinAttemptsInWindow,
            "the accept loop made " + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " attempts in 1500 ms; it should retry while the pipe name is taken.");

        var disposal = Stopwatch.StartNew();
        loser.Dispose();
        disposal.Stop();
        Assert.IsTrue(
            disposal.Elapsed < TimeSpan.FromSeconds(3),
            "dispose must cancel the retry delay and return promptly.");

        int afterDispose = loser.AcceptAttempts;
        Thread.Sleep(300);
        Assert.AreEqual(afterDispose, loser.AcceptAttempts, "the accept loop must not retry after disposal.");

        hostile.Dispose();
        using var recreated = SingleInstance.Create(toolId);
        Assert.IsTrue(recreated.IsFirstInstance, "the mutex should be free after the losing instance is disposed.");
    }

    [TestMethod]
    public void ClientThatConnectsAndNeverSendsDoesNotWedgeTheServer()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);
        string? receivedMessage = null;

        first.MessageReceived += message =>
        {
            receivedMessage = message;
            received.Set();
        };

        using var silent = new NamedPipeClientStream(
            ".",
            PipeNameOf(first),
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        silent.Connect(2000);

        Assert.IsTrue(second.SendMessage("after silent client"),
            "the client should reach the server once the read timeout expires.");
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(8)),
            "the server did not recover from a client that connected and sent nothing.");
        Assert.AreEqual("after silent client", receivedMessage);
    }

    [TestMethod]
    public void AbnormalClientDisconnectDoesNotWedgeTheServer()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId);
        using var second = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);
        string? receivedMessage = null;

        first.MessageReceived += message =>
        {
            receivedMessage = message;
            received.Set();
        };

        using (var rude = new NamedPipeClientStream(
            ".",
            PipeNameOf(first),
            PipeDirection.Out,
            PipeOptions.Asynchronous))
        {
            rude.Connect(2000);
        }

        Assert.IsTrue(second.SendMessage("after rude client"));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(8)),
            "the server did not recover from a client that disconnected before sending.");
        Assert.AreEqual("after rude client", receivedMessage);
    }

    [TestMethod]
    public void ClientBelowServerLevelIsRefusedAndNothingIsDelivered()
    {
        // The server compares against an injected level above this process's level, so this client counts as lower.
        IntegrityLevel own = IntegrityNative.ReadCurrent();
        if (own is IntegrityLevel.Unknown or IntegrityLevel.System)
        {
            Assert.Inconclusive("this test process has no level a higher server level can be injected above.");
        }

        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId, own + 1, readClientLevel: null);
        using var second = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);
        first.MessageReceived += _ => received.Set();

        // The client may see Sent or a broken pipe, depending on whether the server drops the connection first.
        _ = second.TrySendMessage("from below");
        Assert.IsTrue(
            SpinWait.SpinUntil(() => first.RefusedClients >= 1, TimeSpan.FromSeconds(5)),
            "the server did not refuse the lower-level client.");
        Assert.IsFalse(received.Wait(TimeSpan.FromMilliseconds(1500)), "a refused client must deliver nothing.");
    }

    [TestMethod]
    public void ClientWithUnknownLevelIsRefused()
    {
        string toolId = NewToolId();
        using var first = SingleInstance.Create(toolId, IntegrityNative.ReadCurrent(), _ => IntegrityLevel.Unknown);
        using var second = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);
        first.MessageReceived += _ => received.Set();

        _ = second.TrySendMessage("unreadable client");
        Assert.IsTrue(
            SpinWait.SpinUntil(() => first.RefusedClients >= 1, TimeSpan.FromSeconds(5)),
            "the server did not refuse the client whose level could not be read.");
        Assert.IsFalse(received.Wait(TimeSpan.FromMilliseconds(1500)), "an unreadable client must deliver nothing.");
    }

    [TestMethod]
    public void ClientAtOrAboveServerLevelIsAccepted()
    {
        IntegrityLevel own = IntegrityNative.ReadCurrent();
        if (own == IntegrityLevel.Unknown)
        {
            Assert.Inconclusive("this test process level cannot be read.");
        }

        // The real client reader runs against this process, so the process id and token reads are exercised.
        // A server level equal to, or below, this process's level accepts it.
        foreach (IntegrityLevel serverLevel in new[] { own, IntegrityLevel.Low })
        {
            string toolId = NewToolId();
            using var first = SingleInstance.Create(toolId, serverLevel, readClientLevel: null);
            using var second = SingleInstance.Create(toolId);
            using var received = new ManualResetEventSlim(false);
            first.MessageReceived += _ => received.Set();

            Assert.AreEqual(SingleInstanceSendResult.Sent, second.TrySendMessage("from level " + serverLevel));
            Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(5)), "a client at or above the server level was not delivered.");
            Assert.AreEqual(0, first.RefusedClients);
        }
    }

    [TestMethod]
    public void MutexHeldWithoutAnswerDoesNotBlockTheStart()
    {
        string toolId = NewToolId();
        string mutexName = LearnMutexName(toolId);

        // A same-user process created the mutex first and serves no pipe.
        using var squatter = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        Assert.IsTrue(createdNew, "the test must create the mutex first.");

        var watch = Stopwatch.StartNew();
        using var real = SingleInstance.Create(toolId);
        watch.Stop();
        Assert.IsTrue(real.IsFirstInstance, "a mutex held without an answering instance must not block the start.");
        Assert.IsTrue(
            watch.Elapsed < TimeSpan.FromSeconds(10),
            "the wait for an answer must stop at the connect timeout.");

        // A later launch finds the real instance answering, so it is a second instance and delivers its message.
        using var later = SingleInstance.Create(toolId);
        using var received = new ManualResetEventSlim(false);
        string? receivedMessage = null;
        real.MessageReceived += message =>
        {
            receivedMessage = message;
            received.Set();
        };

        Assert.IsFalse(later.IsFirstInstance, "an answering instance makes the next launch a second instance.");
        Assert.IsTrue(later.SendMessage("hello real instance"));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("hello real instance", receivedMessage);

        // Disposing the instance that never owned the mutex must leave the squatter's mutex alone.
        real.Dispose();
        squatter.ReleaseMutex();
    }

    private static string LearnMutexName(string toolId)
    {
        using SingleInstance probe = SingleInstance.Create(toolId);
        Assert.IsTrue(probe.IsFirstInstance);
        return probe.InstanceName;
    }

    private static string LearnPipeName(string toolId)
    {
        using SingleInstance probe = SingleInstance.Create(toolId);
        Assert.IsTrue(probe.IsFirstInstance);
        return PipeNameOf(probe);
    }

    private static string PipeNameOf(SingleInstance instance)
    {
        const string localPrefix = @"Local\";
        Assert.IsTrue(instance.InstanceName.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase),
            "the instance name should use the local namespace.");
        // The pipe name is the instance name plus the session id, so another session cannot reach this instance.
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return instance.InstanceName[localPrefix.Length..] + ".s" + session.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NewToolId()
    {
        return "bwc-test-" + Guid.NewGuid().ToString("N");
    }
}
