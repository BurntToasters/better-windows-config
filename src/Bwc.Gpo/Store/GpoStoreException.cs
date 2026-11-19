namespace Bwc.Gpo.Store;

/// <summary>Why a store operation failed.</summary>
public enum GpoStoreFailure
{
    /// <summary>The write policy refused the path.</summary>
    PolicyDenied,

    /// <summary>A file system error that fits no other reason.</summary>
    Io,

    /// <summary>Another process holds the file open (sharing or lock violation), after the retries.</summary>
    InUse,

    /// <summary>The target file has the read-only attribute. The attribute is never cleared.</summary>
    ReadOnlyTarget,

    /// <summary>The pre-session backup could not be made, so nothing was written.</summary>
    BackupFailed,

    /// <summary>The write lock file could not be taken in time.</summary>
    LockTimeout,

    /// <summary>The write lock file cannot be opened or created by this process.</summary>
    LockUnavailable,

    /// <summary>A file on disk is not valid and the operation needed it.</summary>
    Malformed,

    /// <summary>The store was told it is read-only (not elevated, no write access, or Home).</summary>
    ReadOnlyMode,

    /// <summary>The policy refresh after a save failed. The save itself is on disk.</summary>
    RefreshFailed,
}

/// <summary>
/// The typed failure of the store. It carries the operation, the full path involved and the Win32 error code
/// (0 when none applies); the engine never formats user-facing text.
/// </summary>
public sealed class GpoStoreException : Exception
{
    /// <summary>Creates a failure.</summary>
    /// <param name="reason">The reason code.</param>
    /// <param name="operation">The operation, for example <c>Replace</c>, <c>Backup</c>, <c>Read</c>.</param>
    /// <param name="path">The full path involved (the lock file path for a lock failure).</param>
    /// <param name="win32Error">The Win32 error code, 0 when none.</param>
    /// <param name="message">A developer message.</param>
    /// <param name="inner">The cause, if any.</param>
    public GpoStoreException(GpoStoreFailure reason, string operation, string path, int win32Error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
        Operation = operation;
        Path = path;
        Win32Error = win32Error;
    }

    /// <summary>The reason code.</summary>
    public GpoStoreFailure Reason { get; }

    /// <summary>The operation that failed.</summary>
    public string Operation { get; }

    /// <summary>The full path involved.</summary>
    public string Path { get; }

    /// <summary>The Win32 error code, 0 when none applies.</summary>
    public int Win32Error { get; }

    /// <summary>
    /// The full paths of the GPO files a save had already written when it failed, in write order. There is no rollback, so
    /// these files stay on disk. Empty when nothing was written (every failure before the first write, and every
    /// failure outside a save).
    /// </summary>
    public IReadOnlyList<string> WrittenFiles { get; private set; } = [];

    internal void SetWrittenFiles(IReadOnlyList<string> files) => WrittenFiles = files;

    internal static int Win32Of(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => 5,
            IOException io when (io.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) => io.HResult & 0xFFFF,
            _ => 0,
        };

    internal static GpoStoreException FromIo(string operation, string path, Exception exception, GpoStoreFailure? reason = null)
    {
        var code = Win32Of(exception);
        var chosen = reason ?? (code is 32 or 33 ? GpoStoreFailure.InUse : GpoStoreFailure.Io);
        return new GpoStoreException(chosen, operation, path, code, $"{operation} failed for '{path}' (Win32 {code}): {exception.Message}", exception);
    }
}
