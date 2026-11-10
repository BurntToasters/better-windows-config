<#
.SYNOPSIS
  Publishes the Hub and Tool heads into ONE shared folder: out\<rid>\

.DESCRIPTION
  1. Publishes Bwc.Spike.Hub into an empty out\<rid>\ (its file set is also the separate Hub folder size).
  2. Publishes Bwc.Spike.Tool into the SAME out\<rid>\ folder, after snapshotting the Hub files (SHA-256).
     Any file the Tool publish overwrote is compared with its Hub version: identical bytes are a benign
     overwrite, different bytes are a collision that would break one of the tools.
  3. Publishes Bwc.Spike.Tool alone into out\.separate\<rid>\tool to get the size of a separate
     self-contained Tool folder (the Hub figure comes from step 1, which ran into an empty folder).
  4. Writes out\publish-<rid>.json with collisions, PRI findings, warning counts and folder sizes.
     E2E reads this file for the "saved vs two separate folders" numbers.

.PARAMETER Rid
  win-x64 or win-arm64

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File spikes\S1-publish\publish.ps1 -Rid win-arm64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid
)

$ErrorActionPreference = 'Stop'
trap { Write-Host ("publish.ps1 failed at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) -ForegroundColor Red; exit 1 }
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$root = $PSScriptRoot
$platform = if ($Rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$hubProj = Join-Path $root 'Bwc.Spike.Hub\Bwc.Spike.Hub.csproj'
$toolProj = Join-Path $root 'Bwc.Spike.Tool\Bwc.Spike.Tool.csproj'
$outRoot = Join-Path $root 'out'
$shared = Join-Path $outRoot $Rid
$separateTool = Join-Path $outRoot ".separate\$Rid\tool"
$reportPath = Join-Path $outRoot "publish-$Rid.json"

function Invoke-Publish {
    param([string]$Project, [string]$Destination, [string]$Label)

    Write-Host "== publish $Label -> $Destination" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $lines = @(& dotnet publish $Project -c Release -r $Rid "-p:Platform=$platform" -p:PublishReadyToRun=true -nologo -v minimal -o $Destination 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $lines | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) { throw "dotnet publish failed for $Label (exit $code)" }
    $warnings = @($lines | Where-Object { ($_ -match '(?i)\bwarning\b') -and ($_ -notmatch 'Warning\(s\)') })
    Write-Host ("   {0}: {1:N1}s, {2} warning line(s)" -f $Label, $sw.Elapsed.TotalSeconds, $warnings.Count)
    return [pscustomobject]@{ Label = $Label; Warnings = $warnings.Count; WarningLines = $warnings }
}

function Get-FolderFiles {
    param([string]$Folder)
    $prefixLength = $Folder.TrimEnd('\').Length + 1
    Get-ChildItem -LiteralPath $Folder -Recurse -File | ForEach-Object {
        [pscustomobject]@{
            Path   = $_.FullName.Substring($prefixLength)
            Bytes  = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
}

function Test-FileContains {
    # PRI files store resource names as UTF-8 (ASCII here). Latin-1 decoding maps bytes 1:1, so a plain
    # string search works for both UTF-8 and UTF-16 needles. Latin-1 is code page 28591 (works on Windows PowerShell 5.1 too).
    param([string]$Path, [string]$Needle)
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $text = $latin1.GetString($bytes)
    return $text.Contains($latin1.GetString([Text.Encoding]::UTF8.GetBytes($Needle))) -or
           $text.Contains($latin1.GetString([Text.Encoding]::Unicode.GetBytes($Needle)))
}

# ---- 0. clean this RID's outputs -------------------------------------------------------------
foreach ($p in @($shared, (Split-Path $separateTool -Parent), $reportPath)) {
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $shared | Out-Null

# ---- 1. Hub into an empty shared folder ------------------------------------------------------
$hubRun = Invoke-Publish -Project $hubProj -Destination $shared -Label 'hub'
$hubSnapshot = @(Get-FolderFiles -Folder $shared)
$hubOnlyBytes = ($hubSnapshot | Measure-Object -Property Bytes -Sum).Sum

# ---- 2. Tool into the SAME shared folder -----------------------------------------------------
$toolRun = Invoke-Publish -Project $toolProj -Destination $shared -Label 'tool (shared folder)'
$afterFiles = @{}
foreach ($f in (Get-FolderFiles -Folder $shared)) { $afterFiles[$f.Path] = $f }

$hubPaths = @{}
foreach ($h in $hubSnapshot) { $hubPaths[$h.Path] = $true }

# A file that exists after both publishes with the SAME hash is harmless whether or not the Tool publish rewrote it.
# A file whose hash changed was overwritten by the Tool publish with different content: that is the collision to fix.
$presentInBothIdentical = New-Object System.Collections.Generic.List[string]
$collisions = New-Object System.Collections.Generic.List[object]
$hubOnlyRemoved = New-Object System.Collections.Generic.List[string]
foreach ($h in $hubSnapshot) {
    if (-not $afterFiles.ContainsKey($h.Path)) { $hubOnlyRemoved.Add($h.Path); continue }
    $a = $afterFiles[$h.Path]
    if ($a.Sha256 -eq $h.Sha256) {
        $presentInBothIdentical.Add($h.Path)
    }
    else {
        $collisions.Add([pscustomobject]@{ Path = $h.Path; HubSha256 = $h.Sha256; ToolSha256 = $a.Sha256; HubBytes = $h.Bytes; ToolBytes = $a.Bytes })
    }
}
$toolOnly = New-Object System.Collections.Generic.List[string]
foreach ($k in $afterFiles.Keys) { if (-not $hubPaths.ContainsKey($k)) { $toolOnly.Add($k) } }
$toolOnly.Sort()

Write-Host ("== collision check: {0} present in both with identical bytes, {1} CHANGED by the Tool publish (collisions), {2} tool-only files, {3} hub files removed" -f $presentInBothIdentical.Count, $collisions.Count, $toolOnly.Count, $hubOnlyRemoved.Count)
foreach ($c in $collisions) { Write-Host ("   COLLISION {0}: hub {1} vs tool {2}" -f $c.Path, $c.HubSha256.Substring(0, 12), $c.ToolSha256.Substring(0, 12)) -ForegroundColor Yellow }

# ---- 3. Tool alone, for the separate-folder size baseline ------------------------------------
$sepRun = Invoke-Publish -Project $toolProj -Destination $separateTool -Label 'tool (separate folder)'

# ---- 4. Sizes and PRI findings ---------------------------------------------------------------
$sharedFiles = @(Get-FolderFiles -Folder $shared)
$sharedBytes = ($sharedFiles | Measure-Object -Property Bytes -Sum).Sum
$toolOnlyBytes = (Get-ChildItem -LiteralPath $separateTool -Recurse -File | Measure-Object Length -Sum).Sum
$separateTotal = $hubOnlyBytes + $toolOnlyBytes
$saved = $separateTotal - $sharedBytes

$priFiles = @(Get-ChildItem -LiteralPath $shared -Filter '*.pri' -File | ForEach-Object {
        [pscustomobject]@{
            File              = $_.Name
            Bytes             = $_.Length
            ContainsProbeText = (Test-FileContains -Path $_.FullName -Needle 'ProbeText')
            ContainsLibString = (Test-FileContains -Path $_.FullName -Needle 'Shared library string OK')
        }
    })

$savedPercent = [math]::Round(100.0 * [double]$saved / [double]$separateTotal, 1)
$resourcesPri = Test-Path -LiteralPath (Join-Path $shared 'resources.pri')
$allWarningLines = @($hubRun.WarningLines) + @($toolRun.WarningLines) + @($sepRun.WarningLines) | Select-Object -Unique
$report = [ordered]@{
    rid                  = $Rid
    platform             = $platform
    publishedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
    sharedFolder         = [ordered]@{ path = "out/$Rid"; files = @($sharedFiles).Count; bytes = [int64]$sharedBytes }
    separateFolders      = [ordered]@{
        hubBytes   = [int64]$hubOnlyBytes
        toolBytes  = [int64]$toolOnlyBytes
        totalBytes = [int64]$separateTotal
        toolPath   = "out/.separate/$Rid/tool"
        hubNote    = 'Hub size is the file set of the first publish into an empty folder (identical to a separate Hub folder).'
    }
    savedBytes           = [int64]$saved
    savedPercent         = $savedPercent
    presentInBothIdentical = $presentInBothIdentical.ToArray()
    collisionsDifferent  = $collisions.ToArray()
    hubFilesRemoved      = $hubOnlyRemoved.ToArray()
    toolOnlyPaths        = $toolOnly.ToArray()
    resourcesPriPresent  = $resourcesPri
    priFiles             = @($priFiles)
    buildWarnings        = [ordered]@{ hub = $hubRun.Warnings; toolShared = $toolRun.Warnings; toolSeparate = $sepRun.Warnings }
    buildWarningLines    = @($allWarningLines)
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ''
Write-Host ("RID {0}: shared folder {1:N1} MB ({2} files); two separate folders {3:N1} MB (hub {4:N1} + tool {5:N1}); saved {6:N1} MB ({7}%)" -f $Rid, ($sharedBytes / 1MB), $sharedFiles.Count, ($separateTotal / 1MB), ($hubOnlyBytes / 1MB), ($toolOnlyBytes / 1MB), ($saved / 1MB), $report.savedPercent)
foreach ($p in $priFiles) { Write-Host ("   PRI {0,-28} {1,10:N0} bytes  ProbeText={2}  libString={3}" -f $p.File, $p.Bytes, $p.ContainsProbeText, $p.ContainsLibString) }
Write-Host ("   collisions with different content: {0}" -f $collisions.Count)
Write-Host ("   report: {0}" -f $reportPath)
