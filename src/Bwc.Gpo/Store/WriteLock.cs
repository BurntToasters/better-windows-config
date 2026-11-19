using System.Diagnostics;

namespace Bwc.Gpo.Store;

/// <summary>
/// The write lock that serializes saves across threads and BWC processes: a file in the protected backup root, opened
/// with no sharing from the re-read to the last write. A second holder retries until the timeout. The operating system
/// closes the handle when its holder dies, so a leftover file with no holder is free, and the file is never deleted (no
/// process can lose a lock to a delete). The file is opened only after the write policy has checked the whole chain down
/// to it (for the system policy: the protected data root, <c>gpedit</c> and the backup root, created when missing), so a
/// standard user can neither create it first nor hold it (docs/failure-modes/gpo-store.md, section F).
/// </summary>
internal sealed class WriteLock : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    private FileStream? _stream;

    private WriteLock(FileStream stream) => _stream = stream;

    /// <summary>Takes the lock of a policy, waiting up to <paramref name="timeout"/>.</summary>
    /// <param name="policy">The write policy; asked before the lock file or its folder is created or opened.</param>
    /// <param name="timeout">How long to wait for another holder.</param>
    /// <returns>The held lock; dispose it to release the file.</returns>
    /// <exception cref="GpoStoreException">
    /// The chain or the path is refused (<see cref="GpoStoreFailure.PolicyDenied"/>), the file cannot be created or opened
    /// (<see cref="GpoStoreFailure.LockUnavailable"/>), or another holder kept it past the timeout
    /// (<see cref="GpoStoreFailure.LockTimeout"/>).
    /// </exception>
    internal static WriteLock Acquire(IGpoStorePolicy policy, TimeSpan timeout)
    {
        // The system policy creates and checks the protected chain first; nothing below runs through a folder that fails.
        (policy as SystemGpoStorePolicy)?.EnsureBackupRoot("Lock");

        var path = Path.GetFullPath(policy.LockPath);
        policy.EnsureWriteAllowed(path);
        EnsureFolder(path);

        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Read access only: the lock needs no data, and a link that slips in cannot be written through.
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None, 1, FileOptions.None);
                return Verified(stream, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (GpoStoreException.Win32Of(e) is not (32 or 33))
                {
                    throw LockUnavailable(path, e);
                }
            }

            var left = timeout == Timeout.InfiniteTimeSpan ? RetryDelay : timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                throw new GpoStoreException(GpoStoreFailure.LockTimeout, "Lock", path, 0, $"The write lock '{path}' was not free within {timeout}.");
            }

            Thread.Sleep(left < RetryDelay ? left : RetryDelay);
        }
    }

    public void Dispose()
    {
        // The handle closes, the file stays.
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private static WriteLock Verified(FileStream stream, string path)
    {
        // The policy checked the path before the open. A link swapped in since then is refused here, never held.
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stream.Dispose();
            throw LockUnavailable(path, e);
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            stream.Dispose();
            throw GpoPathChecks.Denied("Lock", path, "the lock file is a link or not a plain file");
        }

        return new WriteLock(stream);
    }

    private static void EnsureFolder(string lockPath)
    {
        var folder = Path.GetDirectoryName(lockPath)!;
        if (Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw LockUnavailable(lockPath, e);
        }
    }

    private static GpoStoreException LockUnavailable(string path, Exception e)
    {
        var code = GpoStoreException.Win32Of(e);
        return new GpoStoreException(
            GpoStoreFailure.LockUnavailable, "Lock", path, code, $"The write lock '{path}' cannot be opened (Win32 {code}): {e.Message}", e);
    }
}
