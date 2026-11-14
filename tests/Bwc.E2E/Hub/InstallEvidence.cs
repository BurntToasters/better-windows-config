using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Hub;

/// <summary>
/// Evidence for the Hub install tests. It reads the stand-in's record file, the Hub and helper logs in the test's
/// own data root, and the staging folders that the update created. Staging folders are compared with a snapshot taken
/// before the test, so only folders the test made are removed.
/// </summary>
internal static class InstallEvidence
{
    public const string RecordFileName = "installer-record.txt";
    public const string HubLogLine = "Update install started. Helper pid ";
    public const string HelperStartedLine = "update installer started:";

    private const string StagingFolderName = "BWC-Update";
    private const string InstallerFileName = "installer.exe";
    private const string ReceiptFileName = "receipt.json";

    private static readonly Regex StagingFolderPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The staging root the update helper uses for a process launched with the scope's TEMP (see
    /// <see cref="E2EConfig.TempEnvironment"/>). It is inside the run's data folder, not the developer's %TEMP%.
    /// </summary>
    public static string StagingRoot(E2EConfig config, string scope) => Path.Combine(config.TempRootFor(scope), StagingFolderName);

    /// <summary>The names of the staging folders under the root that exist now.</summary>
    public static HashSet<string> StagingFolders(string root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return names;
        }

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(folder);
            if (StagingFolderPattern.IsMatch(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The staging folders under the root that were created after the snapshot.</summary>
    public static IReadOnlyList<string> StagingFoldersSince(string root, HashSet<string> before)
    {
        return StagingFolders(root).Where(name => !before.Contains(name)).Select(name => Path.Combine(root, name)).ToList();
    }

    /// <summary>True when a staging folder the test made holds a receipt. A refused install must leave none.</summary>
    public static bool AnyReceiptSince(string root, HashSet<string> before) =>
        StagingFoldersSince(root, before).Any(folder => File.Exists(Path.Combine(folder, ReceiptFileName)));

    /// <summary>Removes the staging folders the test made under the root, and nothing else. A locked file stays.</summary>
    public static void RemoveStagingSince(string root, HashSet<string> before)
    {
        foreach (string folder in StagingFoldersSince(root, before))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                E2EResults.Note("hub.install.staging.cleanup", $"{Path.GetFileName(folder)} was not removed: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>True when the path is installer.exe in a staging folder directly under the given staging root.</summary>
    public static bool IsStagedInstaller(string path, string root)
    {
        if (!string.Equals(Path.GetFileName(path), InstallerFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        string? parent = Path.GetDirectoryName(folder);
        return StagingFolderPattern.IsMatch(Path.GetFileName(folder)) &&
            parent is not null &&
            string.Equals(
                Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The helper pid that the Hub logged when it started the install, or 0 when no line is logged.</summary>
    public static int HelperPidFromHubLog(string dataRoot)
    {
        foreach (string line in ReadLogLines(Path.Combine(dataRoot, "hub", "logs")))
        {
            int at = line.IndexOf(HubLogLine, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            string rest = line[(at + HubLogLine.Length)..];
            int end = 0;
            while (end < rest.Length && char.IsAsciiDigit(rest[end]))
            {
                end++;
            }

            if (end > 0 && int.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return pid;
            }
        }

        return 0;
    }

    /// <summary>True when one of the Hub's own log lines (under the data root) contains the text.</summary>
    public static bool HubLogContains(string dataRoot, string text) =>
        ReadLogLines(Path.Combine(dataRoot, "hub", "logs")).Any(line => line.Contains(text, StringComparison.Ordinal));

    /// <summary>True when the helper's own log says it started the installer.</summary>
    public static bool HelperStartedInstaller(string dataRoot) =>
        ReadLogLines(Path.Combine(dataRoot, "update", "logs")).Any(line => line.Contains(HelperStartedLine, StringComparison.Ordinal));

    /// <summary>Reads a record of key=value lines. The first equals sign of a line separates key and value.</summary>
    public static Dictionary<string, string> ReadRecord(string path)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                fields.TryAdd(line[..equals], line[(equals + 1)..]);
            }
        }

        return fields;
    }

    /// <summary>Waits until the file exists, up to the timeout.</summary>
    public static bool WaitForFile(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (File.Exists(path))
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    /// <summary>
    /// Waits for a process with the Hub's image name, started after the given time, whose image is the Hub in the
    /// publish folder. <paramref name="exceptPid"/> is skipped, so the Hub that was closed does not match.
    /// </summary>
    public static Process? WaitForStartedProcess(string processName, string exePath, int exceptPid, DateTime notBeforeUtc, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            foreach (Process candidate in Process.GetProcessesByName(processName))
            {
                if (candidate.Id == exceptPid || !IsStartedSince(candidate, notBeforeUtc) || !HasImage(candidate, exePath))
                {
                    candidate.Dispose();
                    continue;
                }

                return candidate;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>True when the process's image file is the given path, compared without regard to case.</summary>
    public static bool HasImage(Process process, string exePath)
    {
        try
        {
            string? image = process.MainModule?.FileName;
            return image is not null && string.Equals(Path.GetFullPath(image), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the process handle while the process runs. Process.ExitTime opens a handle when it is first read, and a
    /// process that has already exited cannot be opened, so the exit time is read from this cached handle later.
    /// </summary>
    public static void CacheHandle(Process process)
    {
        _ = process.Handle;
    }

    /// <summary>The UTC time the process exited, or null when it has not exited or the time cannot be read.</summary>
    public static DateTime? ExitTimeUtc(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitTime.ToUniversalTime() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Stops a process the test started: closes its main window, and kills it only after a timeout.</summary>
    public static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                _ = process.CloseMainWindow();
                if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exited while it was being stopped.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsStartedSince(Process process, DateTime notBeforeUtc)
    {
        try
        {
            return process.StartTime.ToUniversalTime() >= notBeforeUtc.AddSeconds(-2);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>All lines of the daily log files (log-yyyymmdd.txt) in a folder. Read without a write lock.</summary>
    private static IEnumerable<string> ReadLogLines(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "log-*.txt"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                yield return line;
            }
        }
    }
}

/// <summary>
/// Samples the running processes with one image name, every 10 ms, and keeps the pid of each one whose image is the
/// given path. The helper runs for a fraction of a second, so it is caught by sampling rather than by one lookup.
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    private readonly string _processName;
    private readonly string _exePath;
    private readonly Dictionary<int, DateTime> _seen = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public ProcessSampler(string processName, string exePath)
    {
        _processName = processName;
        _exePath = exePath;
        _loop = Task.Run(SampleAsync);
    }

    /// <summary>The pids seen running from the path, and when each was first seen (UTC).</summary>
    public IReadOnlyDictionary<int, DateTime> Seen
    {
        get
        {
            lock (_seen)
            {
                return new Dictionary<int, DateTime>(_seen);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends with a cancellation.
        }

        _stop.Dispose();
    }

    private async Task SampleAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (Process process in Process.GetProcessesByName(_processName))
            {
                using (process)
                {
                    lock (_seen)
                    {
                        if (_seen.ContainsKey(process.Id))
                        {
                            continue;
                        }
                    }

                    if (InstallEvidence.HasImage(process, _exePath))
                    {
                        lock (_seen)
                        {
                            _seen[process.Id] = DateTime.UtcNow;
                        }
                    }
                }
            }

            try
            {
                await Task.Delay(Interval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
