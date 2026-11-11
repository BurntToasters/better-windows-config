namespace Bwc.Update;

/// <summary>Decides whether a verified manifest may be offered to this installed version.</summary>
public static class UpdatePolicy
{
    /// <summary>
    /// Channel must match. Stable never offers a prerelease. The version must be newer than the
    /// installed one (SemVer precedence, build metadata ignored). The architecture must have an entry.
    /// </summary>
    public static Outcome<InstallerEntry> SelectInstaller(
        UpdateManifest manifest,
        UpdateChannel requested,
        SemVer current,
        string architecture)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(current);

        if (manifest.Channel != requested)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ChannelMismatch, "manifest channel is not the requested channel");
        }

        if (requested == UpdateChannel.Stable && manifest.Version.IsPrerelease)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.PrereleaseOnStable, "a stable channel never offers a prerelease");
        }

        var order = manifest.Version.CompareTo(current);
        if (order == 0)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.SameVersion, "the offered version is the installed version");
        }

        if (order < 0)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.Downgrade, "the offered version is older than the installed version");
        }

        if (!manifest.Installers.TryGetValue(architecture, out var entry))
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.NoInstallerForArchitecture, "the manifest has no installer for this architecture");
        }

        return Outcome<InstallerEntry>.Success(entry);
    }
}
