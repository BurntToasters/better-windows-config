using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

[TestClass]
public sealed class TaskExtensionsTests
{
    [TestMethod]
    public async Task FaultedTaskLogsOnceWithContextAndExceptionType()
    {
        using var temp = new TempDirectory();
        string context = UniqueContext("fault-once");
        try
        {
            Log.Initialize("task-fault", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;

            await TaskExtensions.LogOutcome(Task.FromException(new InvalidOperationException("boom")), context);

            Log.Shutdown();
            string text = File.ReadAllText(file);
            Assert.AreEqual(1, CountOccurrences(text, context), "a faulted task must log exactly once.");
            Assert.IsTrue(text.Contains(" ERROR ", StringComparison.Ordinal), "a fault must be logged at ERROR.");
            Assert.IsTrue(
                text.Contains("System.InvalidOperationException: boom", StringComparison.Ordinal),
                "the entry must carry the exception type and message.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public async Task CancelledTaskLogsNothing()
    {
        using var temp = new TempDirectory();
        string context = UniqueContext("cancelled");
        try
        {
            Log.Initialize("task-cancel", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;

            await TaskExtensions.LogOutcome(Task.FromCanceled(new CancellationToken(canceled: true)), context);

            Log.Shutdown();
            Assert.AreEqual(0, CountOccurrences(File.ReadAllText(file), context), "a cancelled task is not a fault.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public async Task CompletedTaskLogsNothing()
    {
        using var temp = new TempDirectory();
        string context = UniqueContext("completed");
        try
        {
            Log.Initialize("task-complete", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;

            await TaskExtensions.LogOutcome(Task.CompletedTask, context);

            Log.Shutdown();
            Assert.AreEqual(0, CountOccurrences(File.ReadAllText(file), context), "a completed task logs nothing.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public async Task TaskThatFaultsLaterLogsWhenItFaults()
    {
        using var temp = new TempDirectory();
        string context = UniqueContext("late");
        try
        {
            Log.Initialize("task-late", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;
            var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task observed = TaskExtensions.LogOutcome(source.Task, context);
            Assert.AreEqual(0, CountOccurrences(File.ReadAllText(file), context), "nothing is logged before the fault.");

            source.SetException(new IOException("late failure"));
            await observed;

            Log.Shutdown();
            string text = File.ReadAllText(file);
            Assert.AreEqual(1, CountOccurrences(text, context), "the late fault must be logged once.");
            Assert.IsTrue(text.Contains("System.IO.IOException: late failure", StringComparison.Ordinal),
                "the late entry must carry the exception type.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public async Task NullContextIsLoggedAsUnnamedAndDoesNotThrow()
    {
        using var temp = new TempDirectory();
        try
        {
            Log.Initialize("task-null-context", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;

            await TaskExtensions.LogOutcome(Task.FromException(new ApplicationException("no context")), null!);

            Log.Shutdown();
            string text = File.ReadAllText(file);
            Assert.IsTrue(text.Contains("unnamed task", StringComparison.Ordinal),
                "a null context must be replaced by a placeholder in the entry.");
            Assert.IsTrue(text.Contains("System.ApplicationException: no context", StringComparison.Ordinal),
                "the fault must still be logged with a null context.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void ForgetLogsAFaultWithoutBlockingTheCaller()
    {
        using var temp = new TempDirectory();
        string context = UniqueContext("forget");
        try
        {
            Log.Initialize("task-forget", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;

            Task.FromException(new TimeoutException("slow")).Forget(context);

            string text = WaitForText(file, context);
            Log.Shutdown();
            Assert.AreEqual(1, CountOccurrences(text, context), "Forget must log a fault exactly once.");
            Assert.IsTrue(text.Contains("System.TimeoutException: slow", StringComparison.Ordinal),
                "the Forget entry must carry the exception type.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void ForgetWithNullContextDoesNotThrow()
    {
        using var temp = new TempDirectory();
        try
        {
            Log.Initialize("task-forget-null", new LogOptions { Directory = temp.Path });
            string file = Log.CurrentFile!;

            Task.FromException(new NotSupportedException("null context")).Forget(null!);

            string text = WaitForText(file, "System.NotSupportedException");
            Log.Shutdown();
            Assert.IsTrue(text.Contains("unnamed task", StringComparison.Ordinal),
                "Forget with a null context must still log under the placeholder.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void ForgetOnNullTaskThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ((Task)null!).Forget("null task"));
    }

    private static string UniqueContext(string name)
    {
        return "ctx-" + name + "-" + Guid.NewGuid().ToString("N");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Polls the log file until the marker appears, so Forget (which returns nothing) can be observed.</summary>
    private static string WaitForText(string file, string marker)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(file))
            {
                string text = File.ReadAllText(file);
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    Thread.Sleep(50);
                    return File.ReadAllText(file);
                }
            }

            Thread.Sleep(10);
        }

        Assert.Fail("timed out waiting for '" + marker + "' in the log.");
        return string.Empty;
    }
}
