using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>
/// Checks that the helper's installer and receipt come from the staging root and nowhere else:
/// the file is named installer.exe, sits in a 32-hex-digit folder directly under the root, has a
/// receipt.json beside it, and no folder or file on the way is a link or junction.
/// </summary>
public static partial class InstallerPaths
{
    public const string InstallerName = "installer.exe";

    [GeneratedRegex("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StagingIdPattern();

    /// <summary>Returns the staging folder that holds the installer.</summary>
    public static Outcome<string> Check(string installerPath, string receiptPath, string stagingRoot)
    {
        if (string.IsNullOrEmpty(installerPath) || string.IsNullOrEmpty(receiptPath) || !Path.IsPathFullyQualified(stagingRoot))
        {
            return Refuse("installer, receipt, and staging root must be absolute paths");
        }

        if (!Directory.Exists(stagingRoot))
        {
            return Refuse("staging root does not exist");
        }

        string root;
        string installer;
        string receipt;
        try
        {
            root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar);
            installer = Path.GetFullPath(installerPath);
            receipt = Path.GetFullPath(receiptPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse("path cannot be normalized");
        }

        if (Path.GetFileName(installer) != InstallerName)
        {
            return Refuse("installer must be named installer.exe");
        }

        var folder = Path.GetDirectoryName(installer)!;
        if (!StagingIdPattern().IsMatch(Path.GetFileName(folder)))
        {
            return Refuse("installer folder is not a staging folder");
        }

        if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("installer is outside the staging root");
        }

        if (!string.Equals(receipt, Path.Combine(folder, UpdateReceipt.FileName), StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("receipt must be receipt.json beside the installer");
        }

        foreach (var path in new[] { root, folder, installer })
        {
            if (IsLinkOrJunction(path))
            {
                return Refuse("a folder or file on the installer path is a link or junction");
            }
        }

        return Outcome<string>.Success(folder);
    }

    private static bool IsLinkOrJunction(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static Outcome<string> Refuse(string detail) => Outcome<string>.Fail(UpdateFailure.PathRejected, detail);
}

/// <summary>The install folder that the installer is told to use.</summary>
public static class InstallDirectory
{
    /// <summary>
    /// The folder is passed after /D= as the raw command line. NSIS reads that to the end of the line,
    /// so the folder may not hold a double quote or a control character, may not end with a
    /// separator, and must be an existing absolute folder.
    /// </summary>
    public static Outcome<string> Validate(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return Refuse("install folder is empty");
        }

        foreach (var c in installDir)
        {
            if (c == '"' || c < 0x20)
            {
                return Refuse("install folder has a quote or control character");
            }
        }

        if (!Path.IsPathFullyQualified(installDir))
        {
            return Refuse("install folder must be absolute");
        }

        if (installDir.EndsWith('\\') || installDir.EndsWith('/'))
        {
            return Refuse("install folder must not end with a separator");
        }

        if (!Directory.Exists(installDir))
        {
            return Refuse("install folder does not exist");
        }

        return Outcome<string>.Success(installDir);
    }

    private static Outcome<string> Refuse(string detail) => Outcome<string>.Fail(UpdateFailure.InstallDirUnsafe, detail);
}

/// <summary>
/// Builds the silent installer command line: /S, then allowed switches, then /D= last. A switch is
/// <c>/NAME</c> or <c>/NAME=value</c>. NAME is uppercase letters, digits, or underscores, starting with a
/// letter, and is never S or D. The value is 1 to 256 characters of letters, digits, and <c>_ . - : \</c>,
/// with no "..", so it cannot break the raw command line.
/// </summary>
public static partial class InstallerArguments
{
    [GeneratedRegex("^/(?<name>[A-Z][A-Z0-9_]{0,31})(=(?<value>[A-Za-z0-9_.:\\\\-]{1,256}))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SwitchPattern();

    public static Outcome<string> Build(string installDir, IReadOnlyList<string> switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        var directory = InstallDirectory.Validate(installDir);
        if (!directory.Ok)
        {
            return Outcome<string>.Fail(directory.Failure, directory.Detail);
        }

        foreach (var switchText in switches)
        {
            var match = SwitchPattern().Match(switchText);
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"];
            if (!match.Success
                || name is "S" or "D"
                || (value.Success && value.Value.Contains("..", StringComparison.Ordinal)))
            {
                return Outcome<string>.Fail(
                    UpdateFailure.InstallerArgumentRejected,
                    "installer switch is not allowed: an uppercase name (never /S or /D), and an optional value of letters, digits, _ . - : or \\ with no '..'");
            }
        }

        // /D= must be last and must not be quoted, even when the folder has spaces.
        var line = "/S" + string.Concat(switches.Select(s => " " + s)) + " /D=" + directory.Value;
        return Outcome<string>.Success(line);
    }
}
