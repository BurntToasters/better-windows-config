<#
.SYNOPSIS
  Builds the S3 list spike for win-arm64 Release and runs the scripted 200,000-row bench at several row heights.

.DESCRIPTION
  Writes out/bench-rh<height>.json and out/shot-rh<height>.png for each height.
  -IncludePhysicalEquivalent adds one run at 9 DIP rows / 6 DIP font / 8 DIP icon, which is 18 px rows and 12 px text
  at this machine's 200% scale (classic physical density). Every app run is wrapped in the
  Local\BwcGuiTestLock mutex, held for the whole series, so other GUI tests do not overlap the measurements.
  Only the process this script starts is ever stopped.
#>
[CmdletBinding()]
param(
    [int[]]$RowHeights = @(17, 18, 20),
    [int]$Rows = 200000,
    [int]$HeaderHeight = 22,
    [double]$FontSize = 12,
    [switch]$IncludePhysicalEquivalent
)

$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$projDir = Join-Path $here 'Bwc.Spike.List'
$proj = Join-Path $projDir 'Bwc.Spike.List.csproj'
$out = Join-Path $here 'out'
$exe = Join-Path $projDir 'bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\Bwc.Spike.List.exe'
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host 'Building win-arm64 Release...'
$buildOutput = dotnet build $proj -c Release -r win-arm64 -p:Platform=ARM64 --nologo
$buildExit = $LASTEXITCODE
$buildOutput | Out-Host
if ($buildExit -ne 0) { throw "build failed (exit $buildExit)" }
$warnLine = $buildOutput | Select-String -Pattern '^\s*(\d+) Warning\(s\)' | Select-Object -Last 1
if (-not $warnLine -or [int]$warnLine.Matches[0].Groups[1].Value -ne 0) { throw 'build reported warnings; the spike must build with 0 warnings' }
if (-not (Test-Path $exe)) { throw "exe not found: $exe" }

$runs = @()
foreach ($h in $RowHeights) {
    $runs += [pscustomobject]@{
        Name = "rh$h"
        Args = @('--rows', "$Rows", '--row-height', "$h", '--header-height', "$HeaderHeight", '--font-size', "$FontSize")
    }
}
if ($IncludePhysicalEquivalent) {
    $runs += [pscustomobject]@{
        Name = 'equiv-9dip-6font'
        Args = @('--rows', "$Rows", '--row-height', '9', '--header-height', '11', '--font-size', '6', '--icon-size', '8')
    }
}

$mutex = [System.Threading.Mutex]::new($false, 'Local\BwcGuiTestLock')
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(15)) }
    catch [System.Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Timed out waiting for Local\BwcGuiTestLock' }

    foreach ($run in $runs) {
        $json = Join-Path $out "bench-$($run.Name).json"
        $png = Join-Path $out "shot-$($run.Name).png"
        Remove-Item -Force -ErrorAction SilentlyContinue $json, $png

        # Host load sample before the run: the desktop is shared with other workers' builds, so record it with the result.
        $hostCpu = [math]::Round(((Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 3).CounterSamples | Measure-Object -Property CookedValue -Average).Average, 1)

        $argString = ($run.Args -join ' ') + " --bench `"$json`" --shot `"$png`""
        Write-Host "Running $($run.Name) (host CPU before run: $hostCpu%): $argString"
        $proc = Start-Process -FilePath $exe -ArgumentList $argString -PassThru
        if (-not $proc.WaitForExit(600000)) {
            Stop-Process -Id $proc.Id -Force
            throw "$($run.Name): timed out after 10 minutes (killed the process this script started)"
        }
        if ($proc.ExitCode -ne 0) { throw "$($run.Name): app exited with $($proc.ExitCode)" }
        if (-not (Test-Path $json)) { throw "$($run.Name): no JSON written" }

        $report = Get-Content -Raw $json | ConvertFrom-Json
        if ($report.error) { throw "$($run.Name): bench error: $($report.error)" }
        $report | Add-Member -NotePropertyName hostCpuPctBeforeRun -NotePropertyValue $hostCpu
        $report | ConvertTo-Json -Depth 10 | Set-Content -Path $json -Encoding utf8
        Write-Host ("{0}: maxFrameMs={1} p99={2} framesOver100ms={3} measuredRowHeights={4} sortMs={5} resizeMsMax={6} selectAllMs={7} startupMs={8} privateMB={9}" -f `
            $run.Name, $report.maxFrameMs, $report.p99FrameMs, $report.framesOver100ms, ($report.measuredRowHeights -join ','), `
            $report.sortMs, $report.resizeMsMax, $report.selectAllMs, $report.startupMs, $report.privateMB)
    }
}
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
