namespace Bwc.Gpo.Store;

/// <summary>
/// The named mutex that serializes saves across threads and BWC processes. Held from the re-read to the last write. A
/// mutex that its previous owner abandoned is taken (the state is re-read from disk anyway). Must be disposed on the
/// thread that acquired it.
/// </summary>
internal sealed class WriteLock : IDisposable
{
    private Mutex? _mutex;

    private WriteLock(Mutex mutex) => _mutex = mutex;

    internal static WriteLock Acquire(string name, TimeSpan timeout)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, name);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            throw new GpoStoreException(
                GpoStoreFailure.LockUnavailable, "Lock", name, GpoStoreException.Win32Of(e), $"The write lock '{name}' cannot be opened: {e.Message}", e);
        }

        bool taken;
        try
        {
            taken = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            taken = true;
        }

        if (!taken)
        {
            mutex.Dispose();
            throw new GpoStoreException(GpoStoreFailure.LockTimeout, "Lock", name, 0, $"The write lock '{name}' was not free within {timeout}.");
        }

        return new WriteLock(mutex);
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
