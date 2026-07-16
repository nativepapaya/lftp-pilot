[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsixPath,
    [string]$ExpectedVersion,
    [switch]$RequireRuntime,
    [string]$RuntimeLockPath,
    [string]$RuntimeInventoryPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RuntimeLockPath) { $RuntimeLockPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.lock.json' }
if (-not $RuntimeInventoryPath) { $RuntimeInventoryPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.files.json' }
Import-Module (Join-Path $PSScriptRoot 'PackageValidation.psm1') -Force
if (-not $RequireRuntime) {
    Write-Warning '-RequireRuntime is now always enforced; release packages cannot bypass runtime attestation.'
}
$result = Test-LftpPilotMsix -MsixPath $MsixPath -ExpectedVersion $ExpectedVersion -SignatureMode Unsigned `
    -RuntimeLockPath $RuntimeLockPath -RuntimeInventoryPath $RuntimeInventoryPath
"Verified unsigned MSIX and $($result.RuntimeFileCount) exact locked runtime entries: $($result.Path)"
