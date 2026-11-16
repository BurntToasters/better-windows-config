using System.Reflection;

namespace Bwc.Update;

/// <summary>
/// The folder that holds the running helper must be inside Program Files before it is run elevated: Program Files is
/// admin-only, so a user-writable <c>BwcUpdate.exe</c> is never started with the runas verb.
/// </summary>
public static class ProgramFilesRule
{
    /// <summary>True when <paramref name="dir"/> is inside <paramref name="programFiles"/> (any depth). Fails closed.</summary>
    public static bool IsUnder(string? dir, string? programFiles)
    {
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(programFiles)
            || !Path.IsPathFullyQualified(dir) || !Path.IsPathFullyQualified(programFiles))
        {
            return false;
        }

        return RunningImageRule.IsUnderDirectory(dir, programFiles);
    }
}

/// <summary>Compares two folder paths. Used to check that a caller's install dir is the helper's own folder.</summary>
public static class PathRules
{
    /// <summary>True when both name the same folder, ignoring case, a trailing separator, and dot parts.</summary>
    public static bool SameDirectory(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)
            || !Path.IsPathFullyQualified(a) || !Path.IsPathFullyQualified(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// The only way a non-elevated process may run the elevated helper: the E2E flavor, with a protected root that is a
/// strict subfolder of the process's own temp folder. The seam that reads the variable is compiled only into the E2E
/// flavor (<c>UpdateE2ESeam</c>). This rule is pure and is also applied by the elevated helper itself.
/// </summary>
public static class ProtectedRootRule
{
    /// <summary>
    /// True when <paramref name="candidate"/> is absolute, lies strictly inside <paramref name="tempRoot"/>, and is neither
    /// <paramref name="stagingRoot"/> nor inside it. <paramref name="full"/> is the normalized path when accepted.
    /// </summary>
    public static bool AcceptE2ERoot(string? candidate, string? tempRoot, string? stagingRoot, out string? full)
    {
        full = null;
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(tempRoot) || string.IsNullOrWhiteSpace(stagingRoot)
            || !Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(tempRoot) || !Path.IsPathFullyQualified(stagingRoot))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!RunningImageRule.IsUnderDirectory(normalized, tempRoot)
            || PathRules.SameDirectory(normalized, stagingRoot)
            || RunningImageRule.IsUnderDirectory(normalized, stagingRoot))
        {
            return false;
        }

        full = normalized;
        return true;
    }
}

/// <summary>The helper's own version: the informational version of its assembly, without build metadata.</summary>
public static class HelperVersion
{
    /// <summary>"0.1.2+abc" becomes "0.1.2", "0.2.0-beta.1+sha" becomes "0.2.0-beta.1". Null when there is no text.</summary>
    public static string? FromInformational(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? value : value[..plus].Trim();
    }

    /// <summary>The version of <paramref name="assembly"/>, or the Major.Minor.Build of its assembly version.</summary>
    public static string? Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = FromInformational(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        if (informational is not null)
        {
            return informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
