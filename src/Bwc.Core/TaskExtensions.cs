namespace Bwc.Core;

/// <summary>Observes tasks that the caller starts without awaiting them.</summary>
public static class TaskExtensions
{
    private const string UnnamedContext = "unnamed task";

    /// <summary>
    /// Starts observing a task that nothing awaits. A faulted task is logged once as an error with the
    /// context and the exception. A cancelled or completed task logs nothing. The call never throws for a
    /// null context or a late fault; only a null task is rejected, at the call site.
    /// </summary>
    public static void Forget(this Task task, string context)
    {
        _ = LogOutcome(task, context);
    }

    /// <summary>The continuation that observes the task. It never throws, so it cannot fault itself.</summary>
    internal static Task LogOutcome(Task task, string context)
    {
        ArgumentNullException.ThrowIfNull(task);
        string label = string.IsNullOrWhiteSpace(context) ? UnnamedContext : context;
        return task.ContinueWith(
            done => ReportFault(done, label),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ReportFault(Task task, string label)
    {
        try
        {
            if (!task.IsFaulted)
            {
                return;
            }

            // Reading Exception marks the fault as observed, so it does not reach UnobservedTaskException.
            Exception? fault = task.Exception is { InnerExceptions.Count: 1 } aggregate
                ? aggregate.InnerException
                : task.Exception;
            Log.Exception("Background task failed: " + label, fault);
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("Forget could not log a faulted task for '" + label + "': " + ex);
        }
    }
}
