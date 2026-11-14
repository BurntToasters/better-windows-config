namespace Bwc.Core;

/// <summary>Resolves per-tool data locations under a single base directory.</summary>
public static class AppPaths
{
    public const string DataRootEnvironmentVariable = "BWC_DATA_ROOT";

    private const string ProductFolderName = "BetterWindowsConfig";

    /// <summary>
    /// Gets the base data directory. <c>BWC_DATA_ROOT</c> wins when it contains a fully
    /// qualified path; otherwise the per-user local application data folder is used.
    /// </summary>
    public static string BaseDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string candidate = configured.Trim();
                try
                {
                    if (Path.IsPathFullyQualified(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A malformed override is ignored in favor of the per-user default.
                }
            }

            return Path.Combine(UserFallbackRoot(), ProductFolderName);
        }
    }

    /// <summary>
    /// The per-user root when the override is not used. An empty or relative LocalApplicationData is replaced
    /// by the temp folder, which lives under the same profile. Only a machine where both are empty throws.
    /// </summary>
    private static string UserFallbackRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData) && Path.IsPathFullyQualified(localAppData))
        {
            return localAppData;
        }

        string temp = Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(temp) && Path.IsPathFullyQualified(temp))
        {
            return temp;
        }

        throw new InvalidOperationException(
            "No per-user data folder is available: LocalApplicationData and the temp folder are both empty.");
    }

    /// <summary>Gets the directory for a tool without creating it.</summary>
    public static string ForTool(string toolId)
    {
        return Combine(BaseDirectory, toolId);
    }

    /// <summary>Gets the settings file path for a tool without creating it.</summary>
    public static string SettingsFile(string toolId)
    {
        return Path.Combine(ForTool(toolId), "settings.json");
    }

    /// <summary>Gets the log directory for a tool without creating it.</summary>
    public static string LogDirectory(string toolId)
    {
        return Path.Combine(ForTool(toolId), "logs");
    }

    /// <summary>
    /// Combines a base directory and a validated tool id. This method has no environment or
    /// filesystem side effects, which makes it usable from tests and diagnostics.
    /// </summary>
    public static string Combine(string baseDir, string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);
        ValidateToolId(toolId);
        return Path.Combine(baseDir, toolId);
    }

    internal static void ValidateToolId(string toolId)
    {
        if (string.IsNullOrEmpty(toolId))
        {
            throw new ArgumentException("Tool id must not be empty.", nameof(toolId));
        }

        char first = toolId[0];
        if (!IsLowercaseLetterOrDigit(first))
        {
            throw new ArgumentException(
                "Tool id must start with a lowercase letter or digit.",
                nameof(toolId));
        }

        for (int i = 1; i < toolId.Length; i++)
        {
            char current = toolId[i];
            if (!IsLowercaseLetterOrDigit(current) && current != '-')
            {
                throw new ArgumentException(
                    "Tool id may contain only lowercase letters, digits, and hyphens.",
                    nameof(toolId));
            }
        }
    }

    private static bool IsLowercaseLetterOrDigit(char value)
    {
        return (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
    }
}
