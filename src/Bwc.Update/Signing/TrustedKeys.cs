namespace Bwc.Update;

/// <summary>
/// The minisign public keys this build trusts for manifests and installers.
///
/// PLACEHOLDER. The value below is not a key. It fails validation, so while it is in place every
/// manifest and installer is refused (fail closed). The user creates the real release key and
/// replaces <see cref="PlaceholderKey"/>, then sets <see cref="IsPlaceholder"/> to false. Rotation:
/// list the old key and the new key together during the overlap.
/// </summary>
public static class TrustedKeys
{
    /// <summary>True while <see cref="PlaceholderKey"/> is in place. A release build must not ship with it.</summary>
    public const bool IsPlaceholder = true;

    private const string PlaceholderKey = "untrusted comment: PLACEHOLDER replace with the release public key\nPLACEHOLDER-NOT-A-KEY";

    public static readonly IReadOnlyList<string> Embedded = [PlaceholderKey];
}
