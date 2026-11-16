using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Bwc.Core;
using Bwc.Update;
using Microsoft.Win32;

namespace Bwc.Hub.Migration;

/// <summary>
/// Removes the per-user install that 0.1.1 left behind (docs/failure-modes/hub.md, "Legacy per-user cleanup (0.1.2)").
/// 0.1.2 installs per machine, so a user who updated by hand still has the old copy, its uninstall entry in HKCU, and two
/// shortcuts in their own Start menu. The Hub runs unelevated as that user, so it runs the old uninstaller, which is a
/// per-user program, without a prompt. It runs once per process in the background and never holds up the window. It reads
/// one value (InstallLocation), starts one fixed file name (Uninstall.exe in that folder), and never runs the
/// UninstallString. A failure is logged and tried again at the next start.
/// </summary>
internal static partial class LegacyInstallCleanup
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterWindowsConfig";
    private const string UninstallerFileName = "Uninstall.exe";
    private const string ManifestFileName = "installed-files.txt";
    private const string HubExeFileName = "BetterWindowsConfig.exe";
    private const string HubShortcutName = "Better Windows Config.lnk";
    private const string RegeditShortcutName = "Registry Editor (BWC).lnk";
    private const long MaxUninstallerBytes = 16L * 1024 * 1024;
    private const int MaxPathLength = 240;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(120);
    private static int _started;

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePrefix();

    /// <summary>Starts the cleanup on a pool thread, at most once per process. Never throws.</summary>
    public static void StartInBackground()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(RunSafelyAsync);
    }

    private static async Task RunSafelyAsync()
    {
        try
        {
            await RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Legacy cleanup failed (exception): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task RunAsync()
    {
        if (ProcessIdentity.IsElevatedOrHighIntegrity())
        {
            Log.Info("Legacy cleanup skipped (elevated).");
            return;
        }

        if (!TryResolveTarget(out Target target, out string skipCode))
        {
            Log.Info($"Legacy cleanup skipped ({skipCode}).");
            return;
        }

        string? location;
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(target.KeyPath))
        {
            if (key is null)
            {
                Log.Info("Legacy cleanup skipped (no-key).");
                return;
            }

            location = key.GetValue("InstallLocation") as string;
        }

        if (string.IsNullOrEmpty(location))
        {
            Log.Warn("Legacy cleanup refused (no-install-location).");
            return;
        }

        string hubFolder = AppContext.BaseDirectory;
        string? refusal = CheckLocation(location, hubFolder, out string folder);
        if (refusal is not null)
        {
            Log.Warn($"Legacy cleanup refused ({refusal}): {location}");
            return;
        }

        string uninstaller = Path.Combine(folder, UninstallerFileName);
        if (Directory.Exists(uninstaller))
        {
            Log.Warn("Legacy cleanup refused (uninstaller-invalid): the uninstaller is a folder.");
            return;
        }

        if (!File.Exists(uninstaller))
        {
            if (Directory.Exists(folder))
            {
                Log.Warn("Legacy cleanup refused (uninstaller-missing): the old folder is there without Uninstall.exe.");
                return;
            }

            // A folder is only "gone" when its drive is there. An offline drive may still hold the install.
            string? driveRoot = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot))
            {
                Log.Warn($"Legacy cleanup refused (drive-missing): the drive of {folder} is not available. Nothing was removed.");
                return;
            }

            RemoveStaleEntry(target);
            return;
        }

        string? problem = CheckUninstaller(uninstaller);
        if (problem is not null)
        {
            Log.Warn($"Legacy cleanup refused ({problem}): {uninstaller}");
            return;
        }

        // The key is a user-writable value: only a folder that holds a BWC install may have its uninstaller run.
        if (!File.Exists(Path.Combine(folder, ManifestFileName)) || !File.Exists(Path.Combine(folder, HubExeFileName)))
        {
            Log.Warn($"Legacy cleanup refused (not-bwc-install): {folder} has no {ManifestFileName} or no {HubExeFileName}.");
            return;
        }

        if (!TryStart(uninstaller, target))
        {
            return;
        }

        await WaitForKeyGoneAsync(target).ConfigureAwait(false);
    }

    private readonly record struct Target(string KeyPath, string ShortcutFolder, string? E2ERunId);

    /// <summary>The key and the shortcut folder to work on. Ship: the real ones. E2E: the sandbox the
    /// seam names, or off.</summary>
    private static bool TryResolveTarget(out Target target, out string skipCode)
    {
        skipCode = string.Empty;
#if BWC_E2E
        // E2E flavor only. Without a valid seam the cleanup is off, so no E2E Hub can read the real key or touch the
        // real Start menu. The ship build has none of these names (the publish audit refuses BWC_E2E_LEGACY_).
        string? runId = Environment.GetEnvironmentVariable("BWC_E2E_LEGACY_RUNID");
        string? shortcuts = Environment.GetEnvironmentVariable("BWC_E2E_LEGACY_SHORTCUTS");
        if (string.IsNullOrEmpty(runId) && string.IsNullOrEmpty(shortcuts))
        {
            target = default;
            skipCode = "e2e-no-seam";
            return false;
        }

        if (string.IsNullOrEmpty(runId) || !IsValidRunId(runId) ||
            string.IsNullOrEmpty(shortcuts) || !DrivePrefix().IsMatch(shortcuts) || !Path.IsPathFullyQualified(shortcuts))
        {
            target = default;
            skipCode = "e2e-seam-invalid";
            return false;
        }

        target = new Target($@"Software\BWC-E2E\{runId}\Uninstall\BetterWindowsConfig", Path.GetFullPath(shortcuts), runId);
        return true;
#else
        target = new Target(UninstallKeyPath, Environment.GetFolderPath(Environment.SpecialFolder.Programs), null);
        return true;
#endif
    }

#if BWC_E2E
    private static bool IsValidRunId(string value) =>
        value.Length is > 0 and <= 64 &&
        value is not ("." or "..") &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
#endif

    /// <summary>
    /// Null when the InstallLocation is a place the old uninstaller may run from, else the refusal
    /// code. <paramref name="folder"/>
    /// is the normalized folder (no trailing separator) when the result is null.
    /// </summary>
    internal static string? CheckLocation(string location, string hubFolder, out string folder)
    {
        folder = string.Empty;
        if (location.Length > MaxPathLength || !DrivePrefix().IsMatch(location) || !IsPlainPath(location))
        {
            return "bad-path";
        }

        string trimmed = location.Length > 3 && location[^1] == '\\' ? location[..^1] : location;
        if (trimmed.Length == 3)
        {
            return "drive-root";
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (IsSame(full, hubFolder))
        {
            return "own-folder";
        }

        if (IsSameOrUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ||
            IsSame(full, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) ||
            IsSame(full, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ||
            IsSame(full, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ||
            IsSame(full, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) ||
            IsSame(full, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) ||
            IsSame(full, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))
        {
            return "system-folder";
        }

        if (IsSameOrUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
            IsSameOrUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
        {
            return "program-files";
        }

        // The Hub runs inside the location, so that uninstaller would remove the Hub itself.
        if (IsSameOrUnder(hubFolder, full))
        {
            return "own-folder";
        }

        folder = full;
        return null;
    }

    /// <summary>
    /// No forward slash, quote, wildcard, angle bracket, pipe, control character, or colon after the drive letter; no empty,
    /// dot, or dot-dot segment; no segment that ends in a dot or a space.
    /// </summary>
    private static bool IsPlainPath(string path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c < ' ' || c is '/' or '"' or '*' or '?' or '<' or '>' or '|' || (c == ':' && i != 1))
            {
                return false;
            }
        }

        string rest = path[3..];
        if (rest.Length == 0)
        {
            return true;
        }

        string[] segments = (rest[^1] == '\\' ? rest[..^1] : rest).Split('\\');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment[^1] is '.' or ' ')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSame(string a, string b) =>
        b.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    private static bool IsSameOrUnder(string path, string root)
    {
        if (root.Length == 0)
        {
            return false;
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        string normalizedPath = Path.TrimEndingDirectorySeparator(path);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Null when the file is a plain file of a sane size whose manifest requests asInvoker, else the refusal code. The bytes
    /// are read and never executed. A link is refused.
    /// </summary>
    private static string? CheckUninstaller(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                info.Length < 1 || info.Length > MaxUninstallerBytes)
            {
                return "uninstaller-invalid";
            }

            byte[] bytes = File.ReadAllBytes(path);
            return UninstallerManifest.RequestsOnlyAsInvoker(bytes) ? null : "uninstaller-elevation";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "uninstaller-invalid";
        }
    }

    /// <summary>
    /// Starts the old uninstaller with /S and does not wait for it. UseShellExecute is false, so an exe that asks for
    /// elevation fails with error 740 and shows no prompt.
    /// </summary>
    private static bool TryStart(string uninstaller, Target target)
    {
        var info = new ProcessStartInfo(uninstaller)
        {
            Arguments = "/S",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        if (target.E2ERunId is not null)
        {
            // What the E2E build of the old uninstaller reads in place of /RUNID=.
            info.Environment["BWC_E2E_INSTALLER_RUNID"] = target.E2ERunId;
        }

        try
        {
            using Process? process = Process.Start(info);
            Log.Info($"Legacy cleanup started: {uninstaller} /S");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Legacy cleanup failed (start-failed): {ex.Message}");
            return false;
        }
    }

    /// <summary>The NSIS uninstaller copies itself to %TEMP% and removes the key last, so the key going
    /// away is "done".</summary>
    private static async Task WaitForKeyGoneAsync(Target target)
    {
        DateTime deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(target.KeyPath);
            if (key is null)
            {
                Log.Info("Legacy cleanup finished: the uninstall entry is gone.");
                return;
            }
        }

        Log.Warn("Legacy cleanup timeout: the uninstall entry is still there. It is tried again at the next start.");
    }

    /// <summary>The old folder is gone, so only its uninstall key and its two fixed-name shortcuts are
    /// left. Remove just those.</summary>
    private static void RemoveStaleEntry(Target target)
    {
        try
        {
            foreach (string name in new[] { HubShortcutName, RegeditShortcutName })
            {
                string path = Path.Combine(target.ShortcutFolder, name);
                var info = new FileInfo(path);
                if (info.Exists && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                {
                    info.Delete();
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(target.KeyPath, throwOnMissingSubKey: false);
            Log.Info("Legacy cleanup stale entry removed: the old folder is gone.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Legacy cleanup failed (stale-entry): {ex.Message}");
        }
    }
}
