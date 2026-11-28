using System.ComponentModel;

namespace Bwc.Update;

/// <summary>
/// <c>--launch-hub</c>: starts <c>&lt;helper folder&gt;\BetterWindowsConfig.exe</c> at the integrity level of the desktop
/// shell, so an installer that runs elevated never leaves an elevated Hub. The path is never an argument.
/// When the helper is elevated and the desktop shell cannot be used, nothing is started.
/// </summary>
public static class HubLauncher
{
    public const string HubExecutableName = "BetterWindowsConfig.exe";
    public const string UpdateResultArgument = "--update-result";

    /// <summary>
    /// The last-resort relaunch of <c>--apply</c> after an unexpected error: the Hub from the helper's own folder with
    /// <c>--update-result Unexpected</c>. True when a Hub was started.
    /// </summary>
    internal static bool RelaunchAfterUnexpected(IUpdateProcessHost host) =>
        Launch(host, host.HelperDirectory, UpdateFailure.Unexpected).Ok;

    /// <summary>
    /// Returns Success, or <see cref="UpdateFailure.HubMissing"/> (no Hub exe, or the start failed), or
    /// <see cref="UpdateFailure.DeElevationFailed"/> (elevated, no way to start the Hub unelevated).
    /// </summary>
    public static Outcome<string> Launch(IUpdateProcessHost host, string ownDirectory, UpdateFailure? updateResult)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(ownDirectory);

        var exe = Path.Combine(ownDirectory, HubExecutableName);
        IReadOnlyList<string> arguments = updateResult is { } code ? [UpdateResultArgument, code.ToString()] : [];
        ShellStart started;
        try
        {
            started = host.StartAtShellIntegrity(exe, arguments, ownDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return Outcome<string>.Fail(UpdateFailure.HubMissing, "the Hub could not be started: " + ex.GetType().Name);
        }

        return started switch
        {
            ShellStart.Started => Outcome<string>.Success(exe),
            ShellStart.FileMissing => Outcome<string>.Fail(UpdateFailure.HubMissing, "BetterWindowsConfig.exe is not in the helper folder"),
            _ => Outcome<string>.Fail(UpdateFailure.DeElevationFailed, "the Hub cannot be started at the shell's integrity level; nothing was started"),
        };
    }
}
