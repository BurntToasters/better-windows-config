namespace Bwc.Update;

/// <summary>
/// When the helper writes a file log. The log folder comes from <c>BWC_DATA_ROOT</c>, which a medium-integrity process can
/// point at a junction, so an elevated process that opened a log there would write where the caller chose. An elevated
/// mode therefore never opens the file log. Its results go to stdout and the exit code (update-cli.md).
/// </summary>
public static class UpdateLogging
{
    /// <summary>
    /// True when the file log is initialized for the mode. <c>--apply-elevated</c> never logs, with or without elevation. Every
    /// other mode logs only when the token is not elevated (an unreadable token counts as elevated).
    /// </summary>
    public static bool ShouldInitialize(UpdateMode mode, bool elevated) => mode switch
    {
        UpdateMode.Help or UpdateMode.Invalid => false,
        UpdateMode.ApplyElevated => false,
        _ => !elevated,
    };
}
