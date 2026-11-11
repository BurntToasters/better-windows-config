# Closes Better Windows Config processes that run from one install folder.
#
# Called by installer/include/bwc-common.nsh. Exit codes:
#   0  no BWC process runs from the folder (or all of them exited)
#   3  at least one process is still running; its name and id are printed
#   4  internal error (for example, the native helper did not compile)
#
# A process is matched by image name and by an image path inside -InstallDir. A process whose
# image path cannot be read is reported as still running, so the installer fails closed.
# WM_CLOSE goes to each visible top-level window of the process. A process with no visible
# window cannot be closed this way and is reported after the timeout.

param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$imageNames = @('BetterWindowsConfig', 'BwcRegedit', 'BwcUpdate')
$WM_CLOSE = 0x0010

$root = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'

try {
    Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BwcWindowClose
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Posts msg to every visible top-level window owned by processId. Returns the count posted.
    public static int PostToVisibleWindows(uint processId, uint msg)
    {
        int posted = 0;
        EnumWindows((hWnd, lParam) =>
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner == processId && IsWindowVisible(hWnd))
            {
                if (PostMessage(hWnd, msg, IntPtr.Zero, IntPtr.Zero))
                {
                    posted++;
                }
            }
            return true;
        }, IntPtr.Zero);
        return posted;
    }
}
'@
}
catch {
    [Console]::Error.WriteLine("bwc-close-apps: could not load the window helper: $($_.Exception.Message)")
    exit 4
}

function Get-RunningFromFolder {
    $found = @()
    foreach ($name in $imageNames) {
        foreach ($process in [System.Diagnostics.Process]::GetProcessesByName($name)) {
            $path = $null
            try {
                $path = $process.MainModule.FileName
            }
            catch {
                # Elevated or protected processes do not expose their image path.
                $path = $null
            }
            if ($null -eq $path) {
                $found += [pscustomobject]@{ Name = $name; Id = $process.Id; Known = $false; Process = $process }
                continue
            }
            if ($path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                $found += [pscustomobject]@{ Name = $name; Id = $process.Id; Known = $true; Process = $process }
            }
        }
    }
    return , $found
}

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$lastPost = [DateTime]::MinValue
$remaining = Get-RunningFromFolder

while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
    # Post again every two seconds: an app can ignore a close while a dialog is open.
    if (([DateTime]::UtcNow - $lastPost).TotalSeconds -ge 2) {
        foreach ($entry in $remaining) {
            if ($entry.Known) {
                [void][BwcWindowClose]::PostToVisibleWindows([uint32]$entry.Id, [uint32]$WM_CLOSE)
            }
        }
        $lastPost = [DateTime]::UtcNow
    }
    Start-Sleep -Milliseconds 250
    $remaining = Get-RunningFromFolder
}

if ($remaining.Count -eq 0) {
    Write-Output "bwc-close-apps: no Better Windows Config process runs from $root"
    exit 0
}

foreach ($entry in $remaining) {
    $reason = if ($entry.Known) { 'did not exit' } else { 'image path unreadable' }
    Write-Output "bwc-close-apps: still running: $($entry.Name) (pid $($entry.Id)), $reason"
}
exit 3
