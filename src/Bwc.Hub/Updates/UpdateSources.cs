namespace Bwc.Hub.Updates;

/// <summary>
/// Where the update check looks and which channel it asks for. The build default is empty until a
/// release server is chosen, so a build with no server reports "not configured" instead of failing.
/// Each channel has its own manifest URL: a manifest is signed for one channel, so one URL cannot serve both.
/// </summary>
internal static class UpdateSources
{
    public const string ChannelStable = "stable";
    public const string ChannelBeta = "beta";

    /// <summary>The stable channel's manifest URL for this build. Empty means none is configured.</summary>
    public const string BuildStableManifestUrl = "";

    /// <summary>The beta channel's manifest URL for this build. Empty means none is configured.</summary>
    public const string BuildBetaManifestUrl = "";

#if BWC_E2E
    /// <summary>
    /// E2E-only. Replaces the manifest URL for one launch, whichever channel is selected. Never read in
    /// a ship build.
    /// </summary>
    public const string E2EManifestUrlVariable = "BWC_E2E_UPDATE_MANIFEST_URL";
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
            return e2e.Trim();
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

    /// <summary>"beta" only when the setting says beta; every other value is stable.</summary>
    public static string Channel(HubSettings settings) =>
        string.Equals(settings.UpdateChannel, ChannelBeta, StringComparison.Ordinal) ? ChannelBeta : ChannelStable;
}
