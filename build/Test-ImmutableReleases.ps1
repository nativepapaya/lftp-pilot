[CmdletBinding()]
param([string]$Repository = 'nativepapaya/lftp-pilot')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
[void](Assert-ReleaseRepository $Repository)
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($null -eq $gh) { throw 'GitHub CLI is required to verify immutable-release enforcement.' }
$lines = @(& $gh.Source api -H 'X-GitHub-Api-Version: 2026-03-10' 'repos/nativepapaya/lftp-pilot/immutable-releases' 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "GitHub immutable-release verification failed. The repository must enable immutable releases before publication. $($lines -join [Environment]::NewLine)"
}
try { $status = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) | ConvertFrom-Json }
catch { throw 'GitHub returned invalid immutable-release status JSON.' }
if ($null -eq $status -or $status.enabled -ne $true) {
    throw 'Immutable releases are not enabled for nativepapaya/lftp-pilot.'
}
[pscustomobject]@{ Repository='nativepapaya/lftp-pilot'; Enabled=$true }
