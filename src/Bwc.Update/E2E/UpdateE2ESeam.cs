#if BWC_E2E
using System.Text;

namespace Bwc.Update;

/// <summary>
/// E2E-only overrides for the check, download, and apply commands. Compiled only into the E2E flavor, so a ship build
/// has no environment switch that changes trust or transport. A variable that does not decode
/// leaves the default options in place, so the placeholder key still refuses every manifest.
/// </summary>
internal static class UpdateE2ESeam
{
    /// <summary>Base64 of a minisign public key text. When set, the check trusts only this key.</summary>
    public const string TrustedKeyVariable = "BWC_E2E_UPDATE_TRUSTED_KEY";

    /// <summary>"1" lets the check use http URLs, so the E2E test can serve from loopback.</summary>
    public const string AllowHttpVariable = "BWC_E2E_UPDATE_ALLOW_HTTP";

    public static UpdateOptions ApplyCheckOverrides(UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        UpdateOptions result = options;
        string? keyBase64 = Environment.GetEnvironmentVariable(TrustedKeyVariable);
        if (!string.IsNullOrWhiteSpace(keyBase64) && TryDecodeKey(keyBase64, out string keyText))
        {
            result = result with { TrustedPublicKeys = [keyText] };
        }

        if (Environment.GetEnvironmentVariable(AllowHttpVariable) == "1")
        {
            result = result with { RequireHttps = false };
        }

        return result;
    }

    private static bool TryDecodeKey(string base64, out string keyText)
    {
        try
        {
            keyText = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            return true;
        }
        catch (FormatException)
        {
            keyText = string.Empty;
            return false;
        }
    }
}
#endif
