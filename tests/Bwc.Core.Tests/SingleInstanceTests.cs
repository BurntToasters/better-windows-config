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

    private static string NewToolId()
    {
        return "bwc-test-" + Guid.NewGuid().ToString("N");
    }
}
