using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

[TestClass]
public sealed class CrashHandlerTests
{
    [TestMethod]
    public void ReportWritesContextAndExceptionTypeWithoutShowingDialog()
    {
        using var temp = new TempDirectory();
        string? file = null;
        try
        {
            Log.Initialize("crash-test", new LogOptions { Directory = temp.Path });
            CrashHandler.Install("crash-test", showDialog: false);

            CrashHandler.Report(new InvalidOperationException("boom"), "unit-test-context");

            file = Log.CurrentFile;
        }
        finally
        {
            CrashHandler.Uninstall();
            Log.Shutdown();
        }

        Assert.IsNotNull(file);
        string text = File.ReadAllText(file);
        Assert.IsTrue(text.Contains("unit-test-context", StringComparison.Ordinal),
            "the log should contain the report context.");
        Assert.IsTrue(text.Contains("InvalidOperationException", StringComparison.Ordinal),
            "the log should contain the exception type.");
        Assert.IsTrue(text.Contains("boom", StringComparison.Ordinal),
            "the log should contain the exception message.");
    }

    [TestMethod]
    public void ReportAfterLogShutdownDoesNotThrowWhenTraceListenerThrows()
    {
        using var temp = new TempDirectory();
        var listener = new ThrowingTraceListener();
        try
        {
            Log.Initialize("crash-throwing", new LogOptions { Directory = temp.Path });
            CrashHandler.Install("crash-throwing", showDialog: false);
            Log.Shutdown();

            Trace.Listeners.Add(listener);
            CrashHandler.Report(new InvalidOperationException("late boom"), "after-shutdown");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            CrashHandler.Uninstall();
            Log.Shutdown();
        }
    }
}
