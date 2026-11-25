namespace Bwc.Core;

/// <summary>
/// The pure decisions behind the elevated data folder rules (docs/failure-modes/core.md, "Elevated data folder and
/// links"). Every input is a parameter so no test needs an elevated process or a real link.
/// </summary>
internal static class DataPathPolicy
{
#if BWC_E2E
    /// <summary>True in the E2E flavor, which keeps honoring the data root override when elevated.</summary>
    public const bool IsE2EFlavor = true;
#else
    /// <summary>False in the ship flavor, which ignores the data root override when elevated.</summary>
    public const bool IsE2EFlavor = false;
#endif

    /// <summary>
    /// Picks the data root. A valid configured root wins, except in the ship flavor while elevated: a non-elevated
    /// process controls the variable that reaches an elevated child, so it is ignored there. The default is computed
    /// only when it is used, so an unelevated run with a valid override behaves as before.
    /// </summary>
    public static string ChooseRoot(bool elevated, bool e2eFlavor, string? configuredRoot, Func<string> userDefaultRoot)
    {
        ArgumentNullException.ThrowIfNull(userDefaultRoot);
        if (configuredRoot is not null && (e2eFlavor || !elevated))
        {
            return configuredRoot;
        }

        return userDefaultRoot();
    }

    /// <summary>
    /// Whether a folder or file that was opened for use is safe. Not elevated: always (behavior is unchanged).
    /// Elevated: it must not be a reparse point, its final path must be the expected one, and it must have exactly
    /// one link (a count of 0 means the count is unknown and is refused).
    /// </summary>
    public static bool AllowFile(bool elevated, bool reparsePoint, bool finalPathMatches, uint linkCount)
    {
        if (!elevated)
        {
            return true;
        }

        return !reparsePoint && finalPathMatches && linkCount == 1;
    }
}
