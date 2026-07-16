[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [string]$OutputPath,
    [string]$Repository = 'nativepapaya/lftp-pilot',
    [switch]$AllowSyntheticFixture
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
[void](Assert-ReleaseRepository $Repository)

$artifact = Get-Item -LiteralPath $ArtifactPath -ErrorAction Stop
if ($artifact.Length -le 0) { throw 'The attested unsigned package is empty.' }
$artifactSha = Get-FileSha256 -LiteralPath $artifact.FullName
$synthetic = $false

if ($AllowSyntheticFixture) {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($artifact.FullName)
    if ($env:LFTP_PILOT_SYNTHETIC_PROVENANCE_TEST -cne 'unit-fixture-only-v1' -or
        -not $fullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $artifact.Name -notmatch '^synthetic-[a-f0-9]{32}\.msix$' -or $artifact.Length -gt 1048576) {
        throw 'Synthetic provenance bypass is restricted to small, specially named fixtures under the operating-system temporary directory.'
    }
    $synthetic = $true
    $sourceDigest = '0000000000000000000000000000000000000000'
    $verification = [ordered]@{ fixtureOnly=$true; networkVerification=$false }
}
else {
    if ($artifact.Name -cne 'LFTPPilot.msix') {
        throw "Production provenance verification requires the exact CI artifact name LFTPPilot.msix, not '$($artifact.Name)'."
    }
    $sourceDigest = Get-ReviewedSourceCommit
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh) { throw 'GitHub CLI is required to verify build provenance.' }
    $arguments = Get-BuildProvenanceVerificationArguments -ArtifactPath $artifact.FullName `
        -SourceDigest $sourceDigest -Repository $Repository
    $savedNativePreference = if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference } else { $null }
    try {
        if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }
        $lines = @(& $gh.Source @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $savedNativePreference }
    }
    if ($exitCode -ne 0) {
        throw "GitHub build-provenance verification failed with exit code $exitCode. $($lines -join [Environment]::NewLine)"
    }
    $json = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    try { $verification = $json | ConvertFrom-Json }
    catch { throw 'GitHub returned invalid JSON after provenance verification.' }
    if ($null -eq $verification -or @($verification).Count -lt 1) {
        throw 'GitHub returned no matching build provenance.'
    }
}

$record = [ordered]@{
    schema = 1
    repository = 'nativepapaya/lftp-pilot'
    signerWorkflow = 'nativepapaya/lftp-pilot/.github/workflows/unsigned-package.yml'
    sourceRef = 'refs/heads/main'
    sourceDigest = $sourceDigest
    githubHostedRunnerRequired = $true
    publicGoodAttestationsAllowed = $false
    unsignedArtifact = [ordered]@{
        name = $artifact.Name
        length = [long]$artifact.Length
        sha256 = $artifactSha
    }
    verificationMode = if ($synthetic) { 'synthetic-fixture-only' } else { 'github-artifact-attestation' }
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('o')
    verification = $verification
}

if ($OutputPath) {
    $resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    if ($resolvedOutput -eq $artifact.FullName -or [IO.Path]::GetExtension($resolvedOutput) -ine '.json') {
        throw 'Provenance output must be a separate JSON file and can never replace the verified artifact.'
    }
    $parent = Split-Path $resolvedOutput -Parent
    if ($parent) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    $temporary = "$resolvedOutput.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, (($record | ConvertTo-Json -Depth 30) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    Get-Item -LiteralPath $resolvedOutput
}
else { [pscustomobject]$record }
