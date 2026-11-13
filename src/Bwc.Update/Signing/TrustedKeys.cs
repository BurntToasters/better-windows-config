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
