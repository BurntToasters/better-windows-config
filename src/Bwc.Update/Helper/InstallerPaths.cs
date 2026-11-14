using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

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

    // File.Exists and Directory.Exists answer false when access is denied, so they cannot prove a path is absent.
    // Only a not-found error counts as absent. Any other failure is unknown, and unknown counts as a link.
    private static bool IsLinkOrJunction(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The path Windows resolves for an open file (GetFinalPathNameByHandle), with links followed. The
    /// result may carry a <c>\\?\</c> prefix; compare it with <see cref="SamePath"/>. Null when it cannot be read.
    /// </summary>
    public static string? FinalPathOf(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var buffer = new StringBuilder(MaxFinalPathLength);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        return buffer.ToString(0, (int)length);
    }

    /// <summary>True when both paths name the same file, ignoring case and any device prefix.</summary>
    public static bool SamePath(string expected, string actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        try
        {
            return string.Equals(
                Path.GetFullPath(StripPrefix(expected)),
                Path.GetFullPath(StripPrefix(actual)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Removes the <c>\\?\</c> (or <c>\\?\UNC\</c>) prefix that the final-path call adds.</summary>
    public static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// True when <paramref name="filePath"/> (a final path) is the file <see cref="InstallerName"/> directly inside the
    /// folder that <paramref name="folder"/> was opened from. Compares final paths, so 8.3 names and junctions resolve.
    /// </summary>
    public static bool InFolder(SafeFileHandle folder, string filePath)
    {
        var folderPath = FinalPathOf(folder);
        return folderPath is not null && SamePath(Path.Combine(StripPrefix(folderPath), InstallerName), filePath);
    }

    /// <summary>Opens a folder with backup semantics, sharing read, write, and delete. Null when it cannot be opened.</summary>
    public static SafeFileHandle? OpenFolder(string folder)
    {
        var handle = CreateFileW(folder, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static Outcome<string> Refuse(string detail) => Outcome<string>.Fail(UpdateFailure.PathRejected, detail);

    private const int MaxFinalPathLength = 32768;
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

#pragma warning disable SYSLIB1054 // The StringBuilder buffer is marshalled directly.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);
#pragma warning restore SYSLIB1054
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
/// Builds the silent installer command line: /S /RELAUNCH /PARENTPID=n, then the caller's switches, then /D=
/// last. A switch is <c>/NAME</c> or <c>/NAME=value</c>. NAME is uppercase letters, digits, or underscores,
/// starting with a letter, and is never S or D. The value is 1 to 256 characters of letters, digits, and
/// <c>_ . - : \</c>, with no "..", so it cannot break the raw command line. RELAUNCH and PARENTPID are
/// always written by the helper, so a caller's copy of either is dropped.
/// </summary>
public static partial class InstallerArguments
{
    private const string RelaunchSwitch = "RELAUNCH";
    private const string ParentPidSwitch = "PARENTPID";

    [GeneratedRegex("^/(?<name>[A-Z][A-Z0-9_]{0,31})(=(?<value>[A-Za-z0-9_.:\\\\-]{1,256}))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SwitchPattern();

    public static Outcome<string> Build(string installDir, IReadOnlyList<string> switches, int parentPid)
    {
        ArgumentNullException.ThrowIfNull(switches);
        if (parentPid <= 0)
        {
            return Outcome<string>.Fail(UpdateFailure.InvalidOptions, "parent process id must be positive");
        }

        var directory = InstallDirectory.Validate(installDir);
        if (!directory.Ok)
        {
            return Outcome<string>.Fail(directory.Failure, directory.Detail);
        }

        var kept = new List<string>();
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

            if (name is not (RelaunchSwitch or ParentPidSwitch))
            {
                kept.Add(switchText);
            }
        }

        // /D= must be last and must not be quoted, even when the folder has spaces.
        var line = $"/S /{RelaunchSwitch} /{ParentPidSwitch}={parentPid}"
            + string.Concat(kept.Select(s => " " + s))
            + " /D=" + directory.Value;
        return Outcome<string>.Success(line);
    }
}
