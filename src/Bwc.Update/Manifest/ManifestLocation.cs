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
}
