using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Reads the RemoteRegistry service state for the remote tests. Read only: nothing here starts, stops or
/// configures the service. A refused connect is "not run" only when the service is readable and not Running.
/// A refused connect with the service Running, or with an unreadable state, is a failed check.
/// </summary>
public static class RemoteRegistryService
{
    public const string Name = "RemoteRegistry";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopped = 0x00000001;

    /// <summary>The service state. <see cref="Known"/> is false when the status could not be read.</summary>
    public sealed record ServiceReading(bool Known, bool Running, string State);

    public static ServiceReading Read()
    {
        IntPtr manager = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
            {
                return Unreadable(Marshal.GetLastWin32Error());
            }

            service = OpenService(manager, Name, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return Unreadable(Marshal.GetLastWin32Error());
            }

            if (!QueryServiceStatus(service, out ServiceStatus status))
            {
                return Unreadable(Marshal.GetLastWin32Error());
            }

            return status.CurrentState switch
            {
                ServiceRunning => new ServiceReading(Known: true, Running: true, State: "Running"),
                ServiceStopped => new ServiceReading(Known: true, Running: false, State: "Stopped"),
                _ => new ServiceReading(Known: true, Running: false, State: $"state {status.CurrentState}"),
            };
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                _ = CloseServiceHandle(service);
            }

            if (manager != IntPtr.Zero)
            {
                _ = CloseServiceHandle(manager);
            }
        }
    }

    /// <summary>
    /// Handles a connect that the system refused. The refusal is recorded as a note and the test is Inconclusive
    /// only when the service is readable and not Running. Otherwise it is a failed check and the test fails.
    /// </summary>
    public static void RefusedConnect(string connectCheckId, string notRunNoteId, string error)
    {
        ServiceReading reading = Read();
        if (reading.Known && !reading.Running)
        {
            string note = $"not run: RemoteRegistry is {reading.State}, so the connect was refused ({error}).";
            E2EResults.Note(notRunNoteId, note);
            Assert.Inconclusive(note);
        }

        string detail = $"connect refused while RemoteRegistry is {reading.State}: {error}";
        E2EResults.Record(connectCheckId, false, detail);
        Assert.Fail(detail);
    }

    private static ServiceReading Unreadable(int win32Error) =>
        new(Known: false, Running: false, State: $"not readable ({new Win32Exception(win32Error).Message})");

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

#pragma warning disable SYSLIB1054 // A few advapi32 calls do not justify LibraryImport plumbing.
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
