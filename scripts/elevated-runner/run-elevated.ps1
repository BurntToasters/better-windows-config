# Fixed runner for the "BWC Elevated E2E" scheduled task.
#
# install.ps1 copies this file to %ProgramData%\BwcElevatedRunner\run-elevated.ps1, where only
# Administrators and SYSTEM can change it. The task runs that copy elevated with no arguments.
#
# It runs exactly one test command, from the repo recorded in config.json at install time:
#   node scripts/test-e2e.js --rid <host rid> --flavor e2e --filter "TestCategory=Elevated"
# It first checks the E2E publish folder with scripts/flavor-metadata.js. It writes its log to
# %ProgramData%\BwcElevatedRunner\logs and its state to last-run.txt, then exits.
#
# Exit codes: 0 pass, 1 fail, 2 refused before the tests ran, 3 not run.
# Windows PowerShell 5.1 compatible. Keep this file ASCII.

$ErrorActionPreference = 'Continue'

if ($args.Count -gt 0) {
    Write-Error 'run-elevated.ps1 accepts no arguments.'
    exit 2
}

$RunnerRoot = Join-Path $env:ProgramData 'BwcElevatedRunner'
$ConfigPath = Join-Path $RunnerRoot 'config.json'
$LastRunPath = Join-Path $RunnerRoot 'last-run.txt'
$LogDir = Join-Path $RunnerRoot 'logs'
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-RunRecord([System.Collections.IDictionary]$Fields) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Fields.Keys) {
        $value = [string]$Fields[$key]
        $lines.Add(('{0}={1}' -f $key, ($value -replace "[\r\n]", ' ')))
    }
    $temp = $LastRunPath + '.tmp'
    [System.IO.File]::WriteAllLines($temp, $lines.ToArray(), $Utf8NoBom)
    Move-Item -LiteralPath $temp -Destination $LastRunPath -Force
}

function Test-Elevated {
    $principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-LogLine([string]$Text) {
    Add-Content -LiteralPath $script:LogPath -Value $Text -Encoding UTF8
}

function Invoke-LoggedNode([string]$Label, [string[]]$Arguments) {
    Add-LogLine ('# {0}: node {1}' -f $Label, ($Arguments -join ' '))
    $output = & $script:NodePath @Arguments 2>&1
    $code = $LASTEXITCODE
    $output | ForEach-Object { [string]$_ } | Add-Content -LiteralPath $script:LogPath -Encoding UTF8
    Add-LogLine ('# {0} exit={1}' -f $Label, $code)
    return $code
}

function Get-HostRid([string]$NodePath) {
    $arch = (& $NodePath -p 'process.arch' 2>$null | Select-Object -First 1)
    switch ([string]$arch) {
        'arm64' { return 'win-arm64' }
        'x64'   { return 'win-x64' }
        default { return $null }
    }
}

# --- start -------------------------------------------------------------------------------

$runId = '{0}-{1}' -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'),
    ([guid]::NewGuid().ToString('N').Substring(0, 8))
$startedUtc = (Get-Date).ToUniversalTime().ToString('o')
$script:LogPath = Join-Path $LogDir ($runId + '.log')
$script:NodePath = $null
$rid = ''
$repo = ''
$resultArtifact = ''

if (-not (Test-Elevated)) {
    Write-Error 'The elevated runner was started without elevation. Nothing ran.'
    exit 2
}

try {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    Set-Content -LiteralPath $script:LogPath -Value ('# BWC elevated runner ' + $runId) -Encoding UTF8
    Add-LogLine ('# started ' + $startedUtc)
    Write-RunRecord ([ordered]@{
        runId = $runId; status = 'running'; startedUtc = $startedUtc; finishedUtc = '';
        exitCode = ''; rid = ''; repo = ''; log = $script:LogPath; resultArtifact = ''; message = 'started'
    })
} catch {
    Write-Error ('Could not write runner state: ' + $_.Exception.Message)
    exit 2
}

# Turns the scheduled task off. The user enables it again before the next run (install.ps1 -Enable).
# Called on every exit path, so a finished or refused run never leaves the task armed.
function Disable-RunnerTask {
    try {
        Disable-ScheduledTask -TaskName 'BWC Elevated E2E' -ErrorAction Stop | Out-Null
        Add-LogLine '# task disabled after this run'
        return 'yes'
    } catch {
        Add-LogLine ('# could not disable the task: ' + $_.Exception.Message)
        return 'no'
    }
}

function Stop-Refused([string]$Message) {
    Add-LogLine ('# refused: ' + $Message)
    $disabled = Disable-RunnerTask
    Write-RunRecord ([ordered]@{
        runId = $runId; status = 'refused'; startedUtc = $startedUtc; finishedUtc = (Get-Date).ToUniversalTime().ToString('o');
        exitCode = 2; rid = $rid; repo = $repo; log = $script:LogPath; resultArtifact = ''; message = $Message;
        taskDisabled = $disabled
    })
    exit 2
}

# The repo and node paths come only from config.json, which install.ps1 wrote.
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Stop-Refused 'config.json is missing. Run scripts/elevated-runner/install.ps1 again.'
}
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
} catch {
    Stop-Refused ('config.json is unreadable: ' + $_.Exception.Message)
}
$repo = [string]$config.repo
$script:NodePath = [string]$config.node

if ([string]::IsNullOrWhiteSpace($repo) -or -not [System.IO.Path]::IsPathRooted($repo)) {
    Stop-Refused 'config.json has no absolute repo path.'
}
if (-not (Test-Path -LiteralPath $repo -PathType Container)) {
    Stop-Refused ('The recorded repo folder is missing: ' + $repo)
}
$repoItem = Get-Item -LiteralPath $repo -Force
if ($repoItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    Stop-Refused ('The recorded repo is a link or junction, which this runner does not follow: ' + $repo)
}
if (-not (Test-Path -LiteralPath (Join-Path $repo 'scripts\test-e2e.js') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $repo 'BetterWindowsConfig.slnx') -PathType Leaf)) {
    Stop-Refused ('The recorded repo is not a BWC checkout: ' + $repo)
}
if ([string]::IsNullOrWhiteSpace($script:NodePath) -or
    -not (Test-Path -LiteralPath $script:NodePath -PathType Leaf)) {
    Stop-Refused ('The recorded node executable is missing: ' + $script:NodePath)
}

Set-Location -LiteralPath $repo
# Only the machine PATH plus System32. The user PATH is not used: it can name folders that a normal
# account can write, and an elevated run must not load a tool from one of them.
$env:Path = (Join-Path $env:SystemRoot 'System32') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
# The task's inherited environment must not steer node or the E2E publish folder. NODE_OPTIONS and NODE_PATH
# load code, and BWC_* (BWC_PUBLISH_DIR above all) moves the folder under test. The runner takes no
# parameters, so BWC_PUBLISH_DIR is never set here: test-e2e.js then uses its default publish folder.
foreach ($inherited in @('NODE_OPTIONS', 'NODE_PATH')) {
    Remove-Item -LiteralPath ('Env:' + $inherited) -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath 'Env:' | Where-Object { $_.Name -like 'BWC_*' } | ForEach-Object {
    Remove-Item -LiteralPath ('Env:' + $_.Name) -ErrorAction SilentlyContinue
}

$rid = Get-HostRid $script:NodePath
if ($null -eq $rid) {
    Stop-Refused 'Could not map the node process.arch to win-arm64 or win-x64.'
}
$resultArtifact = Join-Path $repo ('artifacts\e2e\result-' + $rid + '-e2e.json')

$flavorCode = Invoke-LoggedNode 'verify-flavor-e2e' @('scripts/flavor-metadata.js', '--rid', $rid, '--flavor', 'e2e')
if ($flavorCode -ne 0) {
    Stop-Refused ('The E2E publish folder is not verified as flavor E2E. Run: node scripts/dotnet-build.js --rid ' + $rid + ' --flavor e2e')
}

$testCode = Invoke-LoggedNode 'e2e-elevated' @('scripts/test-e2e.js', '--rid', $rid, '--flavor', 'e2e', '--filter', 'TestCategory=Elevated')

switch ($testCode) {
    0 { $status = 'pass'; $message = 'Elevated E2E tests passed.' }
    3 { $status = 'not-run'; $message = 'NOT RUN: no elevated test ran. See the log.' }
    default { $status = 'fail'; $message = ('Elevated E2E tests failed (exit ' + $testCode + ').') }
}

$disabled = Disable-RunnerTask
Write-RunRecord ([ordered]@{
    runId = $runId; status = $status; startedUtc = $startedUtc; finishedUtc = (Get-Date).ToUniversalTime().ToString('o');
    exitCode = $testCode; rid = $rid; repo = $repo; log = $script:LogPath;
    resultArtifact = $(if (Test-Path -LiteralPath $resultArtifact -PathType Leaf) { $resultArtifact } else { '' });
    message = $message; taskDisabled = $disabled
})

switch ($status) {
    'pass' { exit 0 }
    'not-run' { exit 3 }
    default { exit 1 }
}
