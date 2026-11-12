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
    public void PipeNameTakenDoesNotSpinTheCpuAndDisposeReturns()
    {
        string toolId = NewToolId();
        string pipeName = LearnPipeName(toolId);

        // Hold the pipe the mutex-owning instance will try to create. The instance must
        // back off instead of retrying in a tight loop.
        using var hostile = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using var loser = SingleInstance.Create(toolId);
        Assert.IsTrue(loser.IsFirstInstance);

        Process process = Process.GetCurrentProcess();
        TimeSpan before = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        Thread.Sleep(1500);
        wall.Stop();
        process.Refresh();
        TimeSpan used = process.TotalProcessorTime - before;

        Assert.IsTrue(
            wall.Elapsed < TimeSpan.FromSeconds(5),
            "the observation window should stay bounded.");
        Assert.IsTrue(
            used < TimeSpan.FromMilliseconds(900),
            "the accept loop used " + used.TotalMilliseconds.ToString("0") +
            " ms of CPU in 1500 ms while the pipe name was taken; it must back off.");

        var disposal = Stopwatch.StartNew();
        loser.Dispose();
        disposal.Stop();
        Assert.IsTrue(
            disposal.Elapsed < TimeSpan.FromSeconds(3),
            "dispose must cancel the retry delay and return promptly.");

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
        return instance.InstanceName[localPrefix.Length..];
    }

    private static string NewToolId()
    {
        return "bwc-test-" + Guid.NewGuid().ToString("N");
    }
}
