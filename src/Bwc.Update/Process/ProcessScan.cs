using System.ComponentModel;
using System.Diagnostics;

namespace Bwc.Update;

/// <summary>One process found by name. SessionId is null when the session of the process cannot be read.</summary>
public sealed record ProcessEntry(int Pid, int? SessionId);

/// <summary>Finds processes by executable name. Windows implementation: <see cref="SystemProcessSource"/>.</summary>
public interface IProcessSource
{
    IReadOnlyList<ProcessEntry> ByName(string processName);
}

/// <summary>Lists processes with <see cref="Process.GetProcessesByName"/>. Disposes every process it opens.</summary>
public sealed class SystemProcessSource : IProcessSource
{
    public IReadOnlyList<ProcessEntry> ByName(string processName)
    {
        var found = new List<ProcessEntry>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                found.Add(new ProcessEntry(process.Id, SessionOf(process)));
            }
        }

        return found;
    }

    private static int? SessionOf(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The running-process scan used by the helper and by --close-running. <see cref="RunningFrom"/> considers only
/// processes of the current session: a process of another session or another user is not ours to wait for or to
/// close. A process whose session is unknown is still considered, so the fail-closed rule applies.
/// <see cref="RunningFromOtherSessions"/> lists the others, so --close-running can refuse before it changes anything.
/// </summary>
public static class ProcessScan
{
    public static int CurrentSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    public static bool InCurrentSession(ProcessEntry entry, int currentSession) =>
        entry.SessionId is null || entry.SessionId == currentSession;

    /// <summary>
    /// BWC processes named in <see cref="ProcessCommands.ProcessNames"/> that run from <paramref name="installDir"/>,
    /// in the current session, except the pids in <paramref name="skip"/>.
    /// </summary>
    public static List<(int Pid, string Name)> RunningFrom(
        string installDir,
        IReadOnlySet<int> skip,
        IProcessSource source,
        int currentSession)
    {
        var found = new List<(int Pid, string Name)>();
        foreach (var name in ProcessCommands.ProcessNames)
        {
            foreach (var entry in source.ByName(name))
            {
                if (skip.Contains(entry.Pid) || !InCurrentSession(entry, currentSession))
                {
                    continue;
                }

                if (RunningImageRule.IsRunningFrom(entry.Pid, installDir))
                {
                    found.Add((entry.Pid, name));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// BWC processes named in <see cref="ProcessCommands.ProcessNames"/> that run from <paramref name="installDir"/> in a
    /// session other than <paramref name="currentSession"/>, except the pids in <paramref name="skip"/>. A process whose
    /// session is unknown is not listed here: <see cref="RunningFrom"/> already counts it. Each pid is listed once.
    /// </summary>
    public static List<(int Pid, string Name, int Session)> RunningFromOtherSessions(
        string installDir,
        IReadOnlySet<int> skip,
        IProcessSource source,
        int currentSession)
    {
        var found = new List<(int Pid, string Name, int Session)>();
        var seen = new HashSet<int>();
        foreach (var name in ProcessCommands.ProcessNames)
        {
            foreach (var entry in source.ByName(name))
            {
                if (entry.SessionId is not { } session || session == currentSession
                    || skip.Contains(entry.Pid) || !seen.Add(entry.Pid))
                {
                    continue;
                }

                if (RunningImageRule.IsRunningFrom(entry.Pid, installDir))
                {
                    found.Add((entry.Pid, name, session));
                }
            }
        }

        return found;
    }
}
