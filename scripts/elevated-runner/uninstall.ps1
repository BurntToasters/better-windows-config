<#
.SYNOPSIS
  Removes the "BWC Elevated E2E" scheduled task and %ProgramData%\BwcElevatedRunner.
  Run from an elevated PowerShell. This script never elevates itself.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$TaskName = 'BWC Elevated E2E'

$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'uninstall.ps1 must run from an elevated PowerShell (Run as administrator).'
    Write-Host 'Nothing was changed. This script does not elevate itself.'
    exit 1
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    if ([string]$task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $TaskName
    }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host ('Removed task "{0}".' -f $TaskName)
} else {
    Write-Host ('Task "{0}" is not installed.' -f $TaskName)
}

# The folder name is fixed. Refuse to delete anything else.
$RunnerRoot = Join-Path $env:ProgramData 'BwcElevatedRunner'
if ((Split-Path -Leaf $RunnerRoot) -ne 'BwcElevatedRunner') {
    Write-Host ('Refusing to delete an unexpected folder: {0}' -f $RunnerRoot)
    exit 1
}
if (Test-Path -LiteralPath $RunnerRoot) {
    Remove-Item -LiteralPath $RunnerRoot -Recurse -Force
    Write-Host ('Removed {0}.' -f $RunnerRoot)
} else {
    Write-Host ('{0} does not exist.' -f $RunnerRoot)
}
exit 0
