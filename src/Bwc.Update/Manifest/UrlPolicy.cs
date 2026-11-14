namespace Bwc.Update;

/// <summary>
/// Accepts only plain, absolute http(s) URLs. The path (the text before the first '?') gets every
/// check on the raw text, before Uri normalizes it, so a "..", "%2e%2e", or "%2F" cannot be removed
/// silently. The query gets only the character-class check, so signed release URLs keep their %2F, %3D, +.
/// </summary>
public static class UrlPolicy
{
    private const int MaxLength = 4096;
    private const int MaxRedirects = 5;

    private static readonly string[] EncodedDangers = ["%2e", "%2f", "%5c", "%00"];

    /// <summary>Checks a URL from a manifest, a request, or the command line.</summary>
    public static Outcome<Uri> Validate(string? text, bool requireHttps)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Refuse("URL is empty");
        }

        if (text.Length > MaxLength)
        {
            return Refuse("URL is longer than 4096 characters");
        }

        foreach (var c in text)
        {
            if (c <= 0x20 || c >= 0x7F || c is '\\' or '"' or '#')
            {
                return Refuse("URL has a space, control character, non-ASCII character, backslash, quote, or fragment");
            }
        }

        var cut = text.IndexOf('?', StringComparison.Ordinal);
        var lowerPath = (cut >= 0 ? text[..cut] : text).ToLowerInvariant();
        if (EncodedDangers.Any(danger => lowerPath.Contains(danger, StringComparison.Ordinal)))
        {
            return Refuse("URL path has an encoded path separator, dot, or NUL");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return Refuse("URL is not an absolute URI");
        }

        if (uri.Scheme is not ("https" or "http"))
        {
            return Refuse("URL scheme must be https or http");
        }

        if (requireHttps && uri.Scheme != "https")
        {
            return Refuse("https is required");
        }

        if (uri.UserInfo.Length > 0)
        {
            return Refuse("URL carries user information");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return Refuse("URL has no host");
        }

        if (HasDotSegment(PathOf(text)))
        {
            return Refuse("URL path has a dot segment");
        }

        return Outcome<Uri>.Success(uri);
    }

    /// <summary>Checks a redirect target by the same rules. A relative target resolves against <paramref name="from"/>.
    /// </summary>
    public static Outcome<Uri> ValidateRedirect(Uri from, string location, bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (string.IsNullOrEmpty(location))
        {
            return Refuse("redirect has no location");
        }

        // The raw Location is checked too: Uri would escape a space or a non-ASCII character silently.
        if (location.Length > MaxLength || location.Any(c => c <= 0x20 || c >= 0x7F || c is '\\' or '"' or '#'))
        {
            return Refuse("redirect target has a space, control character, non-ASCII character, backslash, quote, or fragment");
        }

        if (HasDotSegment(PathOf(location)))
        {
            return Refuse("redirect path has a dot segment");
        }

        if (!Uri.TryCreate(from, location, out var target))
        {
            return Refuse("redirect target is not a URI");
        }

        return Validate(target.AbsoluteUri, requireHttps);
    }

    /// <summary>The redirect limit. A chain longer than this is a loop or an abuse.</summary>
    public static int RedirectLimit => MaxRedirects;

    private static bool HasDotSegment(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    // The raw path of an absolute or relative URL, without query or fragment.
    private static string PathOf(string text)
    {
        var rest = text;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            rest = text[(scheme + 3)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                return string.Empty;
            }

            rest = rest[slash..];
        }

        var cut = rest.IndexOfAny(['?', '#']);
        return cut >= 0 ? rest[..cut] : rest;
    }

    private static Outcome<Uri> Refuse(string detail) => Outcome<Uri>.Fail(UpdateFailure.UrlRejected, detail);
}
