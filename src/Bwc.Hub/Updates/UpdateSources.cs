namespace Bwc.Hub.Updates;

/// <summary>
/// Where the update check looks and which channel it asks for. Each channel has its own manifest URL: a manifest
/// is signed for one channel, so one URL cannot serve both. Empty means none is configured.
/// </summary>
internal static class UpdateSources
{
    public const string ChannelStable = "stable";
    public const string ChannelBeta = "beta";

    /// <summary>
    /// The stable manifest: the bwc-stable.json asset of the latest stable GitHub release. GitHub's "latest"
    /// skips prereleases and drafts, so a beta publish never changes this URL.
    /// </summary>
    public const string BuildStableManifestUrl =
        "https://github.com/BurntToasters/better-windows-config/releases/latest/download/bwc-stable.json";

    /// <summary>The beta channel's manifest URL for this build. Empty: no beta channel is published yet.</summary>
    public const string BuildBetaManifestUrl = "";

    /// <summary>
    /// True when this build offers the beta channel. Without a beta manifest URL, Update Settings does not offer
    /// Beta, and a saved beta channel reads as stable.
    /// </summary>
    public static bool BetaAvailable => !string.IsNullOrWhiteSpace(BuildBetaManifestUrl);

#if BWC_E2E
    /// <summary>
    /// E2E-only. Replaces the manifest URL for one launch, whichever channel is selected. Never read in
    /// a ship build.
    /// </summary>
    public const string E2EManifestUrlVariable = "BWC_E2E_UPDATE_MANIFEST_URL";

    /// <summary>E2E-only. This value of <see cref="E2EManifestUrlVariable"/> means "no manifest URL is configured".</summary>
    public const string E2ENoManifestUrl = "none";
#endif

    /// <summary>
    /// The manifest URL to check for the selected channel, or null when none is configured. A URL the user
    /// set in Hub settings applies to the channel that is selected, so a mismatch shows as a channel error.
    /// </summary>
    public static string? ManifestUrl(HubSettings settings, string channel)
    {
#if BWC_E2E
        string? e2e = Environment.GetEnvironmentVariable(E2EManifestUrlVariable);
        if (!string.IsNullOrWhiteSpace(e2e))
        {
            // "none" stands for a build with no update server, so the not-configured path stays testable once the
            // build itself carries a release URL and key.
            return string.Equals(e2e.Trim(), E2ENoManifestUrl, StringComparison.Ordinal) ? null : e2e.Trim();
        }
#endif

        if (!string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
        {
            return settings.UpdateManifestUrl.Trim();
        }

        string build = string.Equals(channel, ChannelBeta, StringComparison.Ordinal) ? BuildBetaManifestUrl : BuildStableManifestUrl;
        return string.IsNullOrWhiteSpace(build) ? null : build;
    }

    /// <summary>The signature URL for a manifest URL: the same URL with ".minisig" before any query string.</summary>
    public static string SignatureUrl(string manifestUrl) => Bwc.Update.ManifestLocation.SignatureUrlFor(manifestUrl);

    /// <summary>
    /// "beta" only when the setting says beta and this build offers it; every other value is stable.
    /// </summary>
    public static string Channel(HubSettings settings) =>
        BetaAvailable && string.Equals(settings.UpdateChannel, ChannelBeta, StringComparison.Ordinal) ? ChannelBeta : ChannelStable;
}
