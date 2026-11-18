<#
.SYNOPSIS
Starts one program with a restricted copy of the caller's token, at medium integrity, and waits for it.

.DESCRIPTION
Used by scripts/updater-client-live.js. The release VM is reached over SSH, which gives an administrator the full
token, and the shipped BwcUpdate.exe refuses to check or download when it is elevated. This script makes a restricted
copy of its own token (LUA_TOKEN and DISABLE_MAX_PRIVILEGE: the Administrators group becomes deny-only and every
privilege except SeChangeNotifyPrivilege is removed), lowers it to medium integrity, and starts the program with
CreateProcessAsUser. No desktop shell, password, or scheduled task is involved.

The child starts suspended. Its token is read before it runs: TokenElevation must be 0 and the integrity below high.
Otherwise the child is terminated before it runs and the result says why (quality-gate.md C21 to C26).

The child's stdout and stderr go to the given files. The result file is JSON: started, exitCode, timedOut,
elevated, integrityRid, failedStep, error. This script exits 0 once it has written the result file.
Each argument is passed as base64 of its UTF-8 bytes, comma separated, so no quoting is lost on the way.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$ArgsBase64,
    [Parameter(Mandatory = $true)][string]$WorkingDirectory,
    [Parameter(Mandatory = $true)][string]$StdoutPath,
    [Parameter(Mandatory = $true)][string]$StderrPath,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][int]$TimeoutMs
)

$ErrorActionPreference = 'Stop'

$source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public sealed class BwcRestrictedResult
{
    public bool Started;
    public int ExitCode;
    public bool TimedOut;
    public bool Elevated;
    public uint IntegrityRid;
    public string FailedStep;
    public string Error;
    public string Desktop;
}

public static class BwcRestricted
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint DisableMaxPrivilege = 0x1;
    private const uint LuaToken = 0x4;
    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const uint SeGroupIntegrity = 0x20;
    private const uint SecurityMandatoryHighRid = 0x3000;
    private const uint CreateSuspended = 0x4;
    private const uint DetachedProcess = 0x00000008;
    private const uint WinstaAllAccess = 0x37F;
    private const uint CwfCreateOnly = 0x1;
    private const int UoiName = 2;
    private const uint DesktopAllAccess = 0x000F01FF;
    private const int StartfUseStdHandles = 0x100;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint WaitTimeout = 0x102;
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount, IntPtr sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSidToSid(string sid, out IntPtr sidPointer);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes,
        uint disposition, uint flags, IntPtr template);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, out uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowStationW(string name, uint flags, uint access, ref SecurityAttributes attributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr station);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access,
        ref SecurityAttributes attributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseWindowStation(IntPtr station);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder info, int length, out int needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    private sealed class StepFailed : Exception
    {
        public readonly string Step;

        public StepFailed(string step, string message) : base(message)
        {
            Step = step;
        }
    }

    private static void Check(bool ok, string step)
    {
        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            throw new StepFailed(step, "Win32 " + error + ": " + new Win32Exception(error).Message);
        }
    }

    /// <summary>Quotes one argument with the CommandLineToArgvW rules.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        for (int index = 0; ; index++)
        {
            int backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                index++;
                backslashes++;
            }

            if (index == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(argument[index]);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static IntPtr OpenInheritable(string path, uint access, uint disposition, string step)
    {
        var attributes = new SecurityAttributes();
        attributes.Length = Marshal.SizeOf(typeof(SecurityAttributes));
        attributes.InheritHandle = 1;
        IntPtr handle = CreateFileW(path, access, FileShareRead | FileShareWrite, ref attributes, disposition, FileAttributeNormal, IntPtr.Zero);
        Check(handle != InvalidHandle, step);
        return handle;
    }

    /// <summary>
    /// C27: the desktop the restricted child starts on. Over SSH the session's own window station was made for the full
    /// administrator token, and the child dies at start (0xC0000142) when it cannot open it. So the child gets a private
    /// window station and desktop that only the current user and SYSTEM may open, labeled medium. The station is
    /// unnamed first (Windows names it after the logon session), then named with a GUID. CWF_CREATE_ONLY makes sure an
    /// existing station is never reused. Only an unelevated caller may fall back to its own desktop, which its
    /// medium-integrity child can already open. Returns the lpDesktop value, or null for the caller's own desktop.
    /// </summary>
    private static string ChooseDesktop(bool callerElevated, out IntPtr station, out IntPtr desktop, out string mode)
    {
        station = IntPtr.Zero;
        desktop = IntPtr.Zero;
        mode = null;
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
        string sddl = "D:P(A;;GA;;;" + user + ")(A;;GA;;;SY)S:(ML;;NW;;;ME)";
        IntPtr descriptor;
        uint size;
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptor, out size), "desktop-descriptor");
        try
        {
            var attributes = new SecurityAttributes();
            attributes.Length = Marshal.SizeOf(typeof(SecurityAttributes));
            attributes.SecurityDescriptor = descriptor;
            attributes.InheritHandle = 0;

            station = CreateWindowStationW(null, CwfCreateOnly, WinstaAllAccess, ref attributes);
            int unnamedError = station == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            mode = "private-unnamed";
            int namedError = 0;
            if (station == IntPtr.Zero)
            {
                station = CreateWindowStationW("BwcRestricted-" + Guid.NewGuid().ToString("N"), CwfCreateOnly, WinstaAllAccess, ref attributes);
                namedError = station == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                mode = "private-named";
            }

            if (station == IntPtr.Zero)
            {
                if (!callerElevated)
                {
                    mode = "caller, unelevated (private station refused: unnamed Win32 " + unnamedError + ", named Win32 " + namedError + ")";
                    return null;
                }

                throw new StepFailed("create-window-station", "no private window station for an elevated caller (unnamed: Win32 "
                    + unnamedError + ", named: Win32 " + namedError + ")");
            }

            string stationName = ObjectName(station);

            // CreateDesktop makes the desktop on the calling process's window station, so switch to it for the call.
            IntPtr previous = GetProcessWindowStation();
            Check(previous != IntPtr.Zero, "current-window-station");
            Check(SetProcessWindowStation(station), "enter-window-station");
            try
            {
                desktop = CreateDesktopW("Default", IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, ref attributes);
                Check(desktop != IntPtr.Zero, "create-desktop");
            }
            finally
            {
                SetProcessWindowStation(previous);
            }

            return stationName + "\\Default";
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string ObjectName(IntPtr handle)
    {
        int needed;
        GetUserObjectInformationW(handle, UoiName, null, 0, out needed);
        Check(needed > 0, "window-station-name");
        var buffer = new StringBuilder(needed / 2 + 1);
        Check(GetUserObjectInformationW(handle, UoiName, buffer, needed, out needed), "window-station-name");
        return buffer.ToString();
    }

    /// <summary>Reads TokenElevation and the integrity RID. A read that fails counts as elevated and high.</summary>
    private static void ReadIdentity(IntPtr token, BwcRestrictedResult result)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            int length;
            result.Elevated = !GetTokenInformation(token, TokenElevationClass, buffer, 4, out length) || Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        result.IntegrityRid = uint.MaxValue;
        int needed;
        GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out needed);
        if (needed <= 0)
        {
            return;
        }

        IntPtr label = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, label, needed, out needed))
            {
                return;
            }

            IntPtr sid = Marshal.ReadIntPtr(label);
            if (sid == IntPtr.Zero)
            {
                return;
            }

            int count = Marshal.ReadByte(sid, 1);
            if (count > 0)
            {
                result.IntegrityRid = unchecked((uint)Marshal.ReadInt32(sid, 8 + ((count - 1) * 4)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(label);
        }
    }

    public static BwcRestrictedResult Run(string exe, string[] arguments, string workingDirectory, string stdoutPath,
        string stderrPath, int timeoutMs)
    {
        var result = new BwcRestrictedResult();
        IntPtr ownToken = IntPtr.Zero;
        IntPtr restricted = IntPtr.Zero;
        IntPtr mediumSid = IntPtr.Zero;
        IntPtr label = IntPtr.Zero;
        IntPtr stdin = IntPtr.Zero;
        IntPtr stdout = IntPtr.Zero;
        IntPtr stderr = IntPtr.Zero;
        IntPtr station = IntPtr.Zero;
        IntPtr desktop = IntPtr.Zero;
        var process = new ProcessInformation();
        bool resumed = false;
        try
        {
            Check(OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault, out ownToken), "open-token");
            Check(CreateRestrictedToken(ownToken, DisableMaxPrivilege | LuaToken, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted), "restrict-token");

            Check(ConvertStringSidToSid("S-1-16-8192", out mediumSid), "medium-sid");
            var mandatory = new TokenMandatoryLabel();
            mandatory.Sid = mediumSid;
            mandatory.Attributes = SeGroupIntegrity;
            int labelSize = Marshal.SizeOf(typeof(TokenMandatoryLabel)) + GetLengthSid(mediumSid);
            label = Marshal.AllocHGlobal(labelSize);
            Marshal.StructureToPtr(mandatory, label, false);
            Check(SetTokenInformation(restricted, TokenIntegrityLevelClass, label, labelSize), "medium-integrity");

            stdin = OpenInheritable("NUL", GenericRead, OpenExisting, "open-stdin");
            stdout = OpenInheritable(stdoutPath, GenericWrite, CreateAlways, "open-stdout");
            stderr = OpenInheritable(stderrPath, GenericWrite, CreateAlways, "open-stderr");

            var startup = new StartupInfo();
            startup.cb = Marshal.SizeOf(typeof(StartupInfo));
            startup.dwFlags = StartfUseStdHandles;
            var caller = new BwcRestrictedResult();
            ReadIdentity(ownToken, caller);
            bool callerElevated = caller.Elevated || caller.IntegrityRid >= SecurityMandatoryHighRid;
            string desktopMode;
            startup.lpDesktop = ChooseDesktop(callerElevated, out station, out desktop, out desktopMode);
            result.Desktop = desktopMode;
            startup.hStdInput = stdin;
            startup.hStdOutput = stdout;
            startup.hStdError = stderr;

            var commandLine = new StringBuilder(Quote(exe));
            foreach (string argument in arguments)
            {
                commandLine.Append(' ').Append(Quote(argument));
            }

            Check(CreateProcessAsUserW(restricted, exe, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                CreateSuspended | DetachedProcess, IntPtr.Zero, workingDirectory, ref startup, out process), "create-process");

            // C23: the child has not run yet. It runs only when its token is neither elevated nor high integrity.
            IntPtr childToken;
            Check(OpenProcessToken(process.hProcess, TokenQuery, out childToken), "child-token");
            try
            {
                ReadIdentity(childToken, result);
            }
            finally
            {
                CloseHandle(childToken);
            }

            if (result.Elevated || result.IntegrityRid >= SecurityMandatoryHighRid)
            {
                throw new StepFailed("child-identity", "the child token is still elevated or at high integrity (elevated="
                    + result.Elevated + ", integrity=0x" + result.IntegrityRid.ToString("X") + "); it was not run");
            }

            Check(ResumeThread(process.hThread) != uint.MaxValue, "resume");
            resumed = true;
            result.Started = true;

            uint waited = WaitForSingleObject(process.hProcess, (uint)timeoutMs);
            if (waited == WaitTimeout)
            {
                result.TimedOut = true;
                TerminateProcess(process.hProcess, 1);
                WaitForSingleObject(process.hProcess, 10000);
                return result;
            }

            uint exitCode;
            Check(GetExitCodeProcess(process.hProcess, out exitCode), "exit-code");
            result.ExitCode = unchecked((int)exitCode);
            return result;
        }
        catch (StepFailed failed)
        {
            result.FailedStep = failed.Step;
            result.Error = failed.Message;
            return result;
        }
        finally
        {
            if (process.hProcess != IntPtr.Zero)
            {
                if (!resumed)
                {
                    TerminateProcess(process.hProcess, 1);
                }

                CloseHandle(process.hProcess);
            }

            if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
            if (stdin != IntPtr.Zero) CloseHandle(stdin);
            if (stdout != IntPtr.Zero) CloseHandle(stdout);
            if (stderr != IntPtr.Zero) CloseHandle(stderr);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            if (station != IntPtr.Zero) CloseWindowStation(station);
            if (label != IntPtr.Zero) Marshal.FreeHGlobal(label);
            if (mediumSid != IntPtr.Zero) LocalFree(mediumSid);
            if (restricted != IntPtr.Zero) CloseHandle(restricted);
            if (ownToken != IntPtr.Zero) CloseHandle(ownToken);
        }
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp

$utf8 = New-Object System.Text.UTF8Encoding $false
$clientArgs = New-Object 'System.Collections.Generic.List[string]'
if ($ArgsBase64.Length -gt 0) {
    foreach ($part in $ArgsBase64.Split(',')) {
        $clientArgs.Add($utf8.GetString([Convert]::FromBase64String($part)))
    }
}

$run = [BwcRestricted]::Run($Exe, $clientArgs.ToArray(), $WorkingDirectory, $StdoutPath, $StderrPath, $TimeoutMs)
$exitCode = $null
if ($run.Started -and -not $run.TimedOut) {
    $exitCode = $run.ExitCode
}

$integrity = $null
if ($run.IntegrityRid -ne [uint32]::MaxValue -and $run.IntegrityRid -ne 0) {
    $integrity = '0x{0:X}' -f $run.IntegrityRid
}

$result = [ordered]@{
    schema       = 1
    launch       = 'restricted-token'
    started      = $run.Started
    exitCode     = $exitCode
    timedOut     = $run.TimedOut
    elevated     = $run.Elevated
    integrityRid = $integrity
    desktop      = $run.Desktop
    failedStep   = $run.FailedStep
    error        = $run.Error
}
[System.IO.File]::WriteAllText($ResultPath, (ConvertTo-Json $result -Compress), $utf8)
exit 0
