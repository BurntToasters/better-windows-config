namespace Bwc.Oracle.Capture.Gpedit.Session;

// The session refuses to start. The command maps this to exit code 2 and prints the message as is.
internal sealed class GpeditRefusal : Exception
{
    internal GpeditRefusal(string message)
        : base(message)
    {
    }
}

// Restore did not finish. The manifest stays in place. The command maps this to exit code 1.
internal sealed class GpeditRestoreException : Exception
{
    internal GpeditRestoreException(IReadOnlyList<string> errors)
        : base("restore FAILED, the manifest was kept. Fix the cause and run `gpedit restore` again:\n  " + string.Join("\n  ", errors))
    {
        Errors = errors;
    }

    internal IReadOnlyList<string> Errors { get; }
}
