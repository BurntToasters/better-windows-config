namespace Bwc.Update;

/// <summary>
/// Limits, timeouts, and trust for the check and apply steps. The defaults are the production
/// values. Tests change RequireHttps and the timeouts, and never touch the default trust list.
/// </summary>
public sealed record UpdateOptions
{
    /// <summary>Minisign public keys that may sign manifests and installers. See <see cref="TrustedKeys"/>.</summary>
    public IReadOnlyList<string> TrustedPublicKeys { get; init; } = TrustedKeys.Embedded;

    /// <summary>When true, every manifest, signature, installer, and redirect must use https.</summary>
    public bool RequireHttps { get; init; } = true;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The longest allowed wait for one read. Exceeding it is a stalled download.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan ManifestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan InstallerDownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the helper waits for the installer. The installer is never killed.</summary>
    public TimeSpan InstallerRunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public long MaxManifestBytes { get; init; } = 256 * 1024;

    public long MaxSignatureBytes { get; init; } = 8 * 1024;

    public long MaxInstallerBytes { get; init; } = 1L << 30;

    /// <summary>How far a manifest's pubDate may lead the local clock before it is refused.</summary>
    public TimeSpan MaxFutureSkew { get; init; } = TimeSpan.FromHours(24);
}
