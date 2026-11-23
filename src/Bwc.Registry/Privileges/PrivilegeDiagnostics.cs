namespace Bwc.Registry.Privileges;

/// <summary>
/// Receives diagnostics from the privilege scopes. <see cref="RestoreFailed"/> is set once at application startup
/// (the apps route it to their log). Bwc.Registry has no logger of its own, so this hook is the only way out.
/// </summary>
public static class PrivilegeDiagnostics
{
    /// <summary>
    /// Called when a scope cannot put a privilege back to disabled on dispose. The arguments are the privilege name and
    /// the Win32 error. The hook may be null. A hook that throws is ignored, so Dispose never throws.
    /// </summary>
    public static Action<string, int>? RestoreFailed { get; set; }

    internal static void ReportRestoreFailed(string privilegeName, int win32Error)
    {
        Action<string, int>? hook = RestoreFailed;
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(privilegeName, win32Error);
        }
        catch
        {
            // A diagnostic hook must never make Dispose throw.
        }
    }
}
