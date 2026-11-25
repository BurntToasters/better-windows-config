namespace Bwc.Update;

/// <summary>
/// Where a manifest's signature lives. The signature sits beside the manifest as <c>&lt;manifest&gt;.minisig</c>,
/// and a query string stays on the manifest, so the suffix goes before the query. This file is compiled
/// into the Hub too (a source link), so it has no other dependency in this project.
/// </summary>
public static class ManifestLocation
{
    public const string SignatureSuffix = ".minisig";

    public static string SignatureUrlFor(string manifestUrl)
    {
        ArgumentNullException.ThrowIfNull(manifestUrl);

        var query = manifestUrl.IndexOf('?', StringComparison.Ordinal);
        return query < 0
            ? manifestUrl + SignatureSuffix
            : manifestUrl[..query] + SignatureSuffix + manifestUrl[query..];
    }

    /// <summary>
    /// True only for a plain absolute https URL with a host and no user information. The release notes link
    /// is opened by the shell, so anything else (http, javascript, file, relative, spaces, control characters)
    /// is refused and nothing is launched.
    /// </summary>
    public static bool IsOpenableNotesUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        foreach (var c in url)
        {
            if (c <= 0x20 || c >= 0x7F)
            {
                return false;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && !string.IsNullOrEmpty(uri.Host);
    }
}
