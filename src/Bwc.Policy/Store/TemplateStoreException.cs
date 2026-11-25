namespace Bwc.Policy.Store;

/// <summary>Why a template store operation failed (docs/failure-modes/template-store.md).</summary>
public enum TemplateStoreFailure
{
    /// <summary>The file system refused access. The process is probably not elevated.</summary>
    AccessDenied,

    /// <summary>The owner of a new folder could not be set. The token cannot hold the owner the ACL rules ask for.</summary>
    OwnerNotAllowed,

    /// <summary>Another process holds the file open.</summary>
    InUse,

    /// <summary>The store, a language folder or a target file is a reparse point.</summary>
    ReparsePoint,

    /// <summary>The store or a language folder path is a file.</summary>
    NotADirectory,

    /// <summary>The store folder is a network or UNC path.</summary>
    StoreRootNotLocal,

    /// <summary>The store folder is the system folder, inside it or around it, or is not a usable path.</summary>
    StoreRootForbidden,

    /// <summary>The store folder has an owner or an access list that the ACL rules do not accept.</summary>
    StoreAclRejected,

    /// <summary>A file or culture name is not one safe name segment.</summary>
    UnsafeName,

    /// <summary>A file system error that fits no other reason.</summary>
    Io,
}

/// <summary>
/// The typed failure of the store writer. It carries the operation, the full path and the Win32 error (0 when none
/// applies). The engine formats no user text.
/// </summary>
public sealed class TemplateStoreException : Exception
{
    /// <summary>Creates a failure.</summary>
    /// <param name="reason">The reason code.</param>
    /// <param name="operation">The operation, for example <c>Create</c>, <c>Write</c>, <c>Replace</c>, <c>Delete</c>.</param>
    /// <param name="path">The full path involved.</param>
    /// <param name="win32Error">The Win32 error code, 0 when none.</param>
    /// <param name="message">A developer message.</param>
    /// <param name="inner">The cause, if any.</param>
    public TemplateStoreException(
        TemplateStoreFailure reason, string operation, string path, int win32Error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
        Operation = operation;
        Path = path;
        Win32Error = win32Error;
    }

    /// <summary>The reason code.</summary>
    public TemplateStoreFailure Reason { get; }

    /// <summary>The operation that failed.</summary>
    public string Operation { get; }

    /// <summary>The full path involved.</summary>
    public string Path { get; }

    /// <summary>The Win32 error code, 0 when none applies.</summary>
    public int Win32Error { get; }

    internal static TemplateStoreException Refused(TemplateStoreFailure reason, string operation, string path, string why) =>
        new(reason, operation, path, 0, $"{operation} of '{path}' refused: {why}.");

    internal static TemplateStoreException FromIo(string operation, string path, Exception exception)
    {
        var code = exception switch
        {
            UnauthorizedAccessException => 5,
            IOException io when (io.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) => io.HResult & 0xFFFF,
            _ => 0,
        };
        var reason = code switch
        {
            5 => TemplateStoreFailure.AccessDenied,
            32 or 33 => TemplateStoreFailure.InUse,
            1307 or 1308 => TemplateStoreFailure.OwnerNotAllowed,
            _ => TemplateStoreFailure.Io,
        };
        return new TemplateStoreException(reason, operation, path, code, $"{operation} failed for '{path}' (Win32 {code}): {exception.Message}", exception);
    }
}
