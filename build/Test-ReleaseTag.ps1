[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceDigest,
    [string]$Repository = 'nativepapaya/lftp-pilot'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
[void](Assert-ReleaseRepository $Repository)
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($null -eq $gh) { throw 'GitHub CLI is required to bind the release tag to attested source.' }
function Invoke-ReleaseGitApi([string]$Endpoint) {
    $lines = @(& $gh.Source api -H 'X-GitHub-Api-Version: 2026-03-10' $Endpoint 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "GitHub tag query failed for '$Endpoint'. $($lines -join [Environment]::NewLine)" }
    try { return (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) | ConvertFrom-Json }
    catch { throw "GitHub returned invalid tag JSON for '$Endpoint'." }
}
$reference = Invoke-ReleaseGitApi "repos/nativepapaya/lftp-pilot/git/ref/tags/$Tag"
if ([string]$reference.ref -cne "refs/tags/$Tag" -or $null -eq $reference.object) {
    throw "The reviewed remote tag '$Tag' is missing or ambiguous."
}
$target = $reference.object
for ($depth = 0; $depth -lt 5 -and [string]$target.type -ceq 'tag'; $depth++) {
    if ([string]$target.sha -notmatch '^[0-9a-f]{40}$') { throw "Annotated tag '$Tag' has an invalid object digest." }
    $tagObject = Invoke-ReleaseGitApi "repos/nativepapaya/lftp-pilot/git/tags/$($target.sha)"
    $target = $tagObject.object
}
if ($null -eq $target -or [string]$target.type -cne 'commit' -or
    ([string]$target.sha).ToLowerInvariant() -cne $SourceDigest.ToLowerInvariant()) {
    throw "Remote tag '$Tag' does not resolve exactly to attested source commit $($SourceDigest.ToLowerInvariant())."
}
[pscustomobject]@{ Repository='nativepapaya/lftp-pilot'; Tag=$Tag; SourceDigest=$SourceDigest.ToLowerInvariant() }
