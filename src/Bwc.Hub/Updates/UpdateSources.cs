namespace Bwc.Hub.Updates;

/// <summary>
/// Where the update check looks and which channel it asks for. The build default is empty until a
/// release server is chosen, so a build with no server reports "not configured" instead of failing.
/// </summary>
internal static class UpdateSources
{
    public const string ChannelStable = "stable";
    public const string ChannelBeta = "beta";

    /// <summary>The release manifest URL for this build. Empty means none is configured.</summary>
    public const string BuildManifestUrl = "";

#if BWC_E2E
    /// <summary>E2E-only. Replaces the manifest URL for one launch. Never read in a ship build.</summary>
    public const string E2EManifestUrlVariable = "BWC_E2E_UPDATE_MANIFEST_URL";
#endif

    /// <summary>The manifest URL to check, or null when none is configured.</summary>
    public static string? ManifestUrl(HubSettings settings)
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

        return string.IsNullOrWhiteSpace(BuildManifestUrl) ? null : BuildManifestUrl;
    }

    /// <summary>"beta" only when the setting says beta; every other value is stable.</summary>
    public static string Channel(HubSettings settings) =>
        string.Equals(settings.UpdateChannel, ChannelBeta, StringComparison.Ordinal) ? ChannelBeta : ChannelStable;
}
