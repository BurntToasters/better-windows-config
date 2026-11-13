namespace Bwc.Oracle.Capture;

// Serialises GUI work across workers on the shared desktop via the named mutex agreed in the project rules.
internal static class GuiLock
{
    private const string MutexName = @"Local\BwcGuiTestLock";

    internal static T Run<T>(Func<T> body)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool acquired = false;
        try
        {
            Console.WriteLine($"gui-lock: waiting for {MutexName}");
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromMinutes(15));
            }
            catch (AbandonedMutexException)
            {
                // A previous owner exited without releasing. We now own it, so proceed.
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException($"Timed out after 15 minutes waiting for {MutexName}.");
            }

            Console.WriteLine($"gui-lock: acquired {MutexName}");
            return body();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
                Console.WriteLine($"gui-lock: released {MutexName}");
            }
        }
    }

    internal static void Run(Action body) => Run<bool>(() => { body(); return true; });
}
