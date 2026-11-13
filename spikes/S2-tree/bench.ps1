# Builds the win-arm64 Release app and runs the scripted tree bench at several row heights.
# Writes out\bench-rh<N>.json per row height, plus out\screenshot-rh18.png from the 18 px run.
# Every measured app run holds the shared GUI test mutex, so parallel GUI workers do not overlap it.
param(
    [int]$Rows = 200000,
    [int[]]$RowHeights = @(16, 18, 20),
    [int]$Steps = 300,
    [int]$RunTimeoutSec = 600
)

$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$root = $PSScriptRoot
$proj = Join-Path $root 'Bwc.Spike.Tree\Bwc.Spike.Tree.csproj'
$outDir = Join-Path $root 'out'
$pubDir = Join-Path $root 'Bwc.Spike.Tree\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\publish'
$exe = Join-Path $pubDir 'Bwc.Spike.Tree.exe'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host 'Building win-arm64 Release...'
dotnet publish $proj -c Release -r win-arm64 -p:Platform=ARM64 -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "Missing published exe: $exe" }

# Remove results from earlier runs so a failed run cannot leave a stale JSON behind.
Remove-Item -Path (Join-Path $outDir 'bench-rh*.json') -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $outDir 'screenshot-rh*.png') -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $pubDir 'bwc-s2-error.log') -ErrorAction SilentlyContinue

$mutex = [Threading.Mutex]::new($false, 'Local\BwcGuiTestLock')
$acquired = $false
try {
    try {
        $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(15))
    }
    catch [Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) { throw 'Timed out after 15 min waiting for Local\BwcGuiTestLock' }

    foreach ($rh in $RowHeights) {
        $json = Join-Path $outDir "bench-rh$rh.json"
        $appArgs = @(
            '--rows', $Rows,
            '--row-height', $rh,
            '--font-size', 12,
            '--indent', 19,
            '--steps', $Steps,
            '--bench', $json
        )
        if ($rh -eq 18) {
            $appArgs += @('--screenshot', (Join-Path $outDir 'screenshot-rh18.png'))
        }

        Write-Host "Running bench: rows=$Rows rowHeight=$rh"
        $p = Start-Process -FilePath $exe -ArgumentList $appArgs -PassThru
        if (-not $p.WaitForExit($RunTimeoutSec * 1000)) {
            $p.Kill()
            throw "Bench at row height $rh exceeded $RunTimeoutSec s and was killed (pid $($p.Id))"
        }
        if ($p.ExitCode -ne 0) { throw "Bench at row height $rh exited with code $($p.ExitCode)" }
        if (-not (Test-Path $json)) { throw "Bench at row height $rh wrote no JSON: $json" }

        $result = Get-Content $json -Raw | ConvertFrom-Json
        if ($result.PSObject.Properties.Name -contains 'error') { throw "Bench at row height $rh failed: $($result.error)" }
    }
}
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

Write-Host ''
foreach ($rh in $RowHeights) {
    $r = Get-Content (Join-Path $outDir "bench-rh$rh.json") -Raw | ConvertFrom-Json
    '{0,3} px  maxFrame {1,7:N1}  p99 {2,6:N1}  over100 {3}  rowH {4}  expand {5,6:N1}  collapse {6,5:N1}  startup {7,6:N0}  MB {8,5:N1}' -f `
        $rh, $r.maxFrameMs, $r.p99FrameMs, $r.framesOver100ms, (($r.measuredRowHeights) -join '/'), $r.expandMs, $r.collapseMs, $r.startupMs, $r.privateMB
}
