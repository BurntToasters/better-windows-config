namespace Bwc.Update;

/// <summary>
/// The minisign public keys this build trusts for manifests and installers. Written by
/// scripts/embed-update-key.js (npm run release:embed-key). The matching secret key is the release VM's
/// TAURI_SIGNING_PRIVATE_KEY. Rotation: run the script with --rotate so the old and the new key are both
/// listed during the overlap, then drop the old one with --replace in a later release.
/// </summary>
public static class TrustedKeys
{
    /// <summary>True while <see cref="PlaceholderKey"/> is in place. A release build must not ship with it.</summary>
    public const bool IsPlaceholder = false;

    private const string PlaceholderKey = "untrusted comment: PLACEHOLDER replace with the release public key\nPLACEHOLDER-NOT-A-KEY";

    private const string ReleaseKey0 = "untrusted comment: minisign public key: C1CF14730CFB8427\nRWQnhPsMcxTPwSN9+gKGfRU7y+vU+4x9KHzXVtojWuEyHVFsa3vIKCgB";

    public static readonly IReadOnlyList<string> Embedded = [ReleaseKey0];

    /// <summary>True only for the shipped placeholder list, which means no release key is configured yet.</summary>
    public static bool IsPlaceholderList(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return IsPlaceholder && keys.Count == 1 && keys[0] == PlaceholderKey;
    }

    /// <summary>
    /// The failure for a key list that does not validate. The placeholder is "not configured" (the Hub
    /// says so); any other invalid list is <see cref="UpdateFailure.TrustedKeysInvalid"/>.
    /// </summary>
    internal static UpdateFailure FailureFor(IReadOnlyList<string> keys) =>
        IsPlaceholderList(keys) ? UpdateFailure.TrustedKeysNotConfigured : UpdateFailure.TrustedKeysInvalid;
}
