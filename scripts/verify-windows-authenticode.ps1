#requires -Version 5.1
# Verifies the Authenticode signature of each named file: Valid status, publisher simple name, full
# subject, and an RFC 3161 timestamp. scripts/build-installer.js passes the BWC binaries in the publish
# folder, the setup EXE, and the uninstaller copy. SKIP_WIN_CODESIGN=1 (exactly) skips the check.
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string[]]$Files
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:SKIP_WIN_CODESIGN -eq '1') { Write-Host 'SKIP_WIN_CODESIGN=1; skipping Authenticode verification.'; exit 0 }
if ($env:OS -ne 'Windows_NT') { throw 'Authenticode verification must run on Windows.' }
if ([string]::IsNullOrWhiteSpace($env:AZURE_ARTIFACT_SIGNING_PUBLISHER)) { throw 'AZURE_ARTIFACT_SIGNING_PUBLISHER is required for Authenticode verification.' }
if ([string]::IsNullOrWhiteSpace($env:AZURE_ARTIFACT_SIGNING_PUBLISHER_DN)) { throw 'AZURE_ARTIFACT_SIGNING_PUBLISHER_DN is required for full Authenticode identity verification.' }
. (Join-Path $PSScriptRoot 'artifact-signing-tools.ps1')
Import-BundledPowerShellSecurityModule

$expected = $env:AZURE_ARTIFACT_SIGNING_PUBLISHER.Trim()
$expectedSubject = $env:AZURE_ARTIFACT_SIGNING_PUBLISHER_DN.Trim()
$checked = 0
foreach ($file in $Files) {
  if ([string]::IsNullOrWhiteSpace($file)) { throw 'An empty file name was given to verify.' }
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "File to verify was not found: $file" }
  $resolved = (Resolve-Path -LiteralPath $file).Path
  $signature = Get-AuthenticodeSignature -LiteralPath $resolved
  if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Invalid or missing Authenticode signature: $resolved ($($signature.Status))"
  }
  if (-not $signature.SignerCertificate) { throw "Missing signer certificate: $resolved" }
  $publisher = $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
  if ($publisher -ne $expected) { throw "Unexpected publisher for ${resolved}: '$publisher'" }
  $subject = $signature.SignerCertificate.Subject.Trim()
  if ($subject -ne $expectedSubject) { throw "Unexpected certificate Subject for $resolved. Expected '$expectedSubject', got '$subject'." }
  if (-not $signature.TimeStamperCertificate) { throw "Missing RFC3161 timestamp: $resolved" }
  Write-Host "Verified: $resolved"
  $checked += 1
}
if ($checked -eq 0) { throw 'No files to verify.' }
Write-Host "Verified $checked timestamped Windows file(s) from '$expectedSubject'."
