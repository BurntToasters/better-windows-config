<#
.SYNOPSIS
  Installs the "BWC Elevated E2E" scheduled task. Run once from an elevated PowerShell.

.DESCRIPTION
  This script never elevates itself. It refuses to run unless the shell is already elevated.

  It copies run-elevated.ps1 to %ProgramData%\BwcElevatedRunner, where only Administrators and
  SYSTEM can change it, and records this checkout and the node path in config.json. Then it
  registers the task for the current user only, interactive, with highest privileges. The task
  has no triggers, so it runs only when started with schtasks /Run. It is disabled unless -Enable
  is given.

.PARAMETER Enable
  Enable the task after it is registered.

.PARAMETER Disable
  Disable an installed task and leave the runner files in place. Needs an elevated shell.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-runner\install.ps1
#>
[CmdletBinding()]
param(
    [switch]$Enable,
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
$TaskName = 'BWC Elevated E2E'

$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'install.ps1 must run from an elevated PowerShell (Run as administrator).'
    Write-Host 'Nothing was changed. This script does not elevate itself.'
    exit 1
}

if ($Enable -and $Disable) {
    Write-Host 'Use either -Enable or -Disable, not both.'
    exit 2
}

$RunnerRoot = Join-Path $env:ProgramData 'BwcElevatedRunner'
$LogDir = Join-Path $RunnerRoot 'logs'
$RunnerTarget = Join-Path $RunnerRoot 'run-elevated.ps1'
$ConfigPath = Join-Path $RunnerRoot 'config.json'
$RunnerSource = Join-Path $PSScriptRoot 'run-elevated.ps1'
$PowerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Get-TaskState {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) { return $null }
    return [string]$task.State
}

if ($Disable) {
    if ($null -eq (Get-TaskState)) {
        Write-Host ('Task "{0}" is not installed. Nothing to disable.' -f $TaskName)
        exit 0
    }
    Disable-ScheduledTask -TaskName $TaskName | Out-Null
    Write-Host ('Task "{0}" is disabled. Runner files were kept. Uninstall with uninstall.ps1.' -f $TaskName)
    exit 0
}

# --- locate this checkout and node ---------------------------------------------------------

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
foreach ($required in @('scripts\test-e2e.js', 'scripts\flavor-metadata.js', 'BetterWindowsConfig.slnx')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $required) -PathType Leaf)) {
        Write-Host ('This does not look like a BWC checkout: {0} is missing.' -f $required)
        exit 1
    }
}

$nodeCommand = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $nodeCommand) {
    Write-Host 'node was not found on PATH in this elevated shell. Install Node.js, then run install.ps1 again.'
    exit 1
}
$NodePath = $nodeCommand.Source

# --- copy the fixed runner and write config --------------------------------------------------

function Set-RunnerAcl([string]$Path, [bool]$IsDirectory) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User

    if ($IsDirectory) {
        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    } else {
        $acl = New-Object System.Security.AccessControl.FileSecurity
        $inherit = [System.Security.AccessControl.InheritanceFlags]::None
    }
    $acl.SetAccessRuleProtection($true, $false)
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $inherit, $propagation, 'Allow')))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $inherit, $propagation, 'Allow')))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($currentUser, 'ReadAndExecute', $inherit, $propagation, 'Allow')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

New-Item -ItemType Directory -Force -Path $RunnerRoot | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Set-RunnerAcl $RunnerRoot $true

Copy-Item -LiteralPath $RunnerSource -Destination $RunnerTarget -Force
$config = [ordered]@{
    schema = 1
    repo = $RepoRoot
    node = $NodePath
    installedUtc = (Get-Date).ToUniversalTime().ToString('o')
    installedFor = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}
$json = $config | ConvertTo-Json
[System.IO.File]::WriteAllText($ConfigPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

foreach ($file in @($RunnerTarget, $ConfigPath)) {
    Set-RunnerAcl $file $false
}

# --- register the task ------------------------------------------------------------------------

$userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $PowerShellExe `
    -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $RunnerTarget) `
    -WorkingDirectory $RunnerRoot
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $taskPrincipal -Settings $settings `
    -Description 'Runs the BWC elevated E2E tests from the checkout recorded in config.json. Manual start only.' `
    -Force | Out-Null

if ($Enable) {
    Enable-ScheduledTask -TaskName $TaskName | Out-Null
} else {
    Disable-ScheduledTask -TaskName $TaskName | Out-Null
}

Write-Host ('Installed task "{0}" for {1}.' -f $TaskName, $userId)
Write-Host ('  repo:   {0}' -f $RepoRoot)
Write-Host ('  node:   {0}' -f $NodePath)
Write-Host ('  runner: {0}' -f $RunnerTarget)
Write-Host ('  state:  {0}' -f (Get-TaskState))
if ($Enable) {
    Write-Host 'The task is enabled. Disable it with: install.ps1 -Disable'
} else {
    Write-Host 'The task is disabled. Enable it with: install.ps1 -Enable'
}
exit 0
