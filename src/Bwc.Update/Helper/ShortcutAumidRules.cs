namespace Bwc.Update;

/// <summary>
/// The rules of <c>--set-aumid &lt;shortcut&gt; &lt;app id&gt;</c> (docs/failure-modes/update-elevation.md,
/// section SA). The mode
/// runs inside the elevated installer and writes into a Start menu shortcut, so it accepts only the shortcuts of this
/// product (<see cref="Shortcuts"/>), with their own ids, directly in the folder the installer used. Everything here is
/// pure except <see cref="FileProblem"/>, which reads attributes.
/// </summary>
public static class ShortcutAumidRules
{
    public const string HubShortcutName = "Better Windows Config.lnk";
    public const string RegeditShortcutName = "Registry Editor (BWC).lnk";
    public const string GpeditShortcutName = "Local Group Policy Editor (BWC).lnk";

    // Must match Bwc.Core AppIdentity (HubAppUserModelId, RegeditAppUserModelId, GpeditAppUserModelId) and the installer
    // defines.
    public const string HubAppId = "BetterWindowsConfig.Hub";
    public const string RegeditAppId = "BetterWindowsConfig.Regedit";
    public const string GpeditAppId = "BetterWindowsConfig.Gpedit";

    /// <summary>The one table of the shortcuts the helper writes: each app id with the file name that belongs to it.</summary>
    public static IReadOnlyList<(string AppId, string ShortcutName)> Shortcuts { get; } =
    [
        (HubAppId, HubShortcutName),
        (RegeditAppId, RegeditShortcutName),
        (GpeditAppId, GpeditShortcutName),
    ];

    /// <summary>
    /// Null when the two values may be passed on, else the usage problem (SA2, SA3). The path must be a drive path in its
    /// normalized spelling (no dot parts, no doubled or forward slashes, no trailing dot or space), without a quote, a
    /// control character, or a stream name, and must end in <c>.lnk</c>. The id must be one of the two exact ids.
    /// </summary>
    public static string? ArgumentProblem(string? shortcut, string? appId)
    {
        if (string.IsNullOrEmpty(shortcut) || string.IsNullOrEmpty(appId))
        {
            return "--set-aumid needs a shortcut path and an app id";
        }

        if (ExpectedShortcutName(appId) is null)
        {
            return "the app id must be one of " + string.Join(", ", Shortcuts.Select(row => row.AppId));
        }

        if (shortcut.Length < 8 || !char.IsAsciiLetter(shortcut[0]) || shortcut[1] != ':' || shortcut[2] != '\\')
        {
            return "the shortcut must be a path that starts with a drive letter and a backslash";
        }

        for (var i = 0; i < shortcut.Length; i++)
        {
            var c = shortcut[i];
            if (c < ' ' || c == '"' || c == '/' || c is '*' or '?' or '<' or '>' or '|' || (c == ':' && i != 1))
            {
                return "the shortcut path has a character that is not allowed";
            }
        }

        if (!shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "the shortcut must be a .lnk file";
        }

        try
        {
            if (!string.Equals(Path.GetFullPath(shortcut), shortcut, StringComparison.OrdinalIgnoreCase))
            {
                return "the shortcut path must be written in its normal form (no dot parts, doubled separators, or trailing dots)";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "the shortcut path is not valid";
        }

        return null;
    }

    /// <summary>
    /// Null when the shortcut may be written, else the refusal (SA4, SA5). The file name must be the one that belongs to
    /// the id, and its folder must be accepted by <paramref name="folderAllowed"/>.
    /// </summary>
    public static string? ScopeProblem(string shortcut, string appId, Func<string, bool> folderAllowed)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(folderAllowed);

        var name = Path.GetFileName(shortcut);
        var expected = ExpectedShortcutName(appId);
        if (expected is null || !string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
        {
            return "the shortcut name does not belong to the app id";
        }

        var folder = Path.GetDirectoryName(shortcut);
        if (string.IsNullOrEmpty(folder) || !folderAllowed(folder))
        {
            return "the shortcut is not in the folder the installer writes to";
        }

        return null;
    }

    private static string? ExpectedShortcutName(string appId)
    {
        foreach (var (id, name) in Shortcuts)
        {
            if (string.Equals(id, appId, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The ship scope: the shortcut is directly in the all-users Programs folder, compared by exact folder.</summary>
    public static Func<string, bool> ShipScope(string commonPrograms) =>
        folder => !string.IsNullOrEmpty(commonPrograms) && PathRules.SameDirectory(folder, commonPrograms);

    /// <summary>
    /// Null when the shortcut is a plain file in a plain folder (SA6). A link, a folder, or a missing file is
    /// refused.
    /// </summary>
    public static string? FileProblem(string shortcut)
    {
        try
        {
            var file = new FileInfo(shortcut);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return "the shortcut is missing, is a folder, or is a link";
            }

            var folder = file.Directory;
            if (folder is null || !folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "the shortcut folder is missing or is a link";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "the shortcut cannot be inspected: " + ex.GetType().Name;
        }
    }
}
