using Bwc.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Single-instance gate (docs/failure-modes/core.md, SingleInstance: SI1).
[TestClass]
public sealed class SingleInstanceTests
{
    [TestMethod]
    public async Task Second_Instance_Message_Reaches_First()
    {
        using var first = SingleInstance.Create("update");
        Assert.IsTrue(first.IsFirstInstance);
        using var second = SingleInstance.Create("update");
        Assert.IsFalse(second.IsFirstInstance);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.MessageReceived += message => received.TrySetResult(message);

        var result = second.TrySendMessage("activate-test");

        Assert.AreEqual(SingleInstanceSendResult.Sent, result);
        Assert.AreEqual("activate-test", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
