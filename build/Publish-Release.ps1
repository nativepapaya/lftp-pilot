[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AttestedUnsignedMsix,
    [Parameter(Mandatory)][string]$SignedMsix,
    [Parameter(Mandatory)][string]$PublicCertificate,
    [Parameter(Mandatory)][string]$RuntimeLockPath,
    [Parameter(Mandatory)][string]$LicenseManifestPath,
    [string]$Repository = 'nativepapaya/lftp-pilot',
    [string]$StagingDirectory = (Join-Path (Get-Location) 'release-staging'),
    [switch]$ApproveInitialReleaseCertificate
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PackageValidation.psm1') -Force
[void](Assert-ReleaseRepository $Repository)
$validatedVersion = (Assert-MsixVersion -Version $Version).ToString(4)
$committedRuntimeLock = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.lock.json')).Path
if ((Resolve-Path -LiteralPath $RuntimeLockPath).Path -cne $committedRuntimeLock) {
    throw 'Release publication must use the committed LFTP runtime lock.'
}
$RuntimeLockPath = $committedRuntimeLock
$tag = "v$validatedVersion"
if (Get-Command gh -ErrorAction SilentlyContinue) { & gh release view $tag --repo $Repository 2>$null; if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists and will never be modified." } }
else { throw 'GitHub CLI is required for the explicit local publish step.' }
& (Join-Path $PSScriptRoot 'Test-ImmutableReleases.ps1') -Repository $Repository | Out-Null
[IO.Directory]::CreateDirectory($StagingDirectory) | Out-Null
$provenancePath = Join-Path $StagingDirectory 'LFTPPilot.provenance.json'
& (Join-Path $PSScriptRoot 'Test-BuildProvenance.ps1') -ArtifactPath $AttestedUnsignedMsix `
    -OutputPath $provenancePath -Repository $Repository | Out-Null
$verifiedProvenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') -Tag $tag -SourceDigest ([string]$verifiedProvenance.sourceDigest) `
    -Repository $Repository | Out-Null
& (Join-Path $PSScriptRoot 'Test-UnsignedPackage.ps1') -MsixPath $AttestedUnsignedMsix `
    -ExpectedVersion $validatedVersion -RequireRuntime | Out-Null
$payloadBinding = Compare-LftpPilotSignedPayload -UnsignedMsix $AttestedUnsignedMsix -SignedMsix $SignedMsix
& (Join-Path $PSScriptRoot 'Test-CertificateContinuity.ps1') -PublicCertificate $PublicCertificate `
    -Repository $Repository -ApproveInitialReleaseCertificate:$ApproveInitialReleaseCertificate | Out-Null
& (Join-Path $PSScriptRoot 'Test-NuGetLocks.ps1') | Out-Null
& (Join-Path $PSScriptRoot 'Test-SignedPackage.ps1') -MsixPath $SignedMsix -ExpectedVersion $validatedVersion `
    -ExpectedCertificate $PublicCertificate | Out-Null
$msix = Join-Path $StagingDirectory 'LFTPPilot.msix'
$cer = Join-Path $StagingDirectory 'LFTPPilot.cer'
Copy-Item -LiteralPath $SignedMsix -Destination $msix -Force
Copy-Item -LiteralPath $PublicCertificate -Destination $cer -Force
& (Join-Path $PSScriptRoot 'New-AppInstaller.ps1') -Version $validatedVersion -OutputPath (Join-Path $StagingDirectory 'LFTPPilot.appinstaller') -Repository $Repository | Out-Null
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
$signedFile = Get-Item -LiteralPath $SignedMsix
$publicCertificateObject = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $PublicCertificate).Path)
try { $certificateSha256 = Get-CertificateSha256 $publicCertificateObject } finally { $publicCertificateObject.Dispose() }
$provenance | Add-Member -NotePropertyName signedArtifact -NotePropertyValue ([ordered]@{
    name='LFTPPilot.msix'; length=[long]$signedFile.Length; sha256=(Get-FileSha256 $signedFile.FullName)
})
$provenance | Add-Member -NotePropertyName payloadBinding -NotePropertyValue ([ordered]@{
    policy='Every decoded ZIP entry must be byte-identical to the attested unsigned package; only non-empty AppxSignature.p7x may be added.'
    payloadEntryCount=[int]$payloadBinding.PayloadEntryCount
    signatureSha256=[string]$payloadBinding.SignatureSha256
})
$provenance | Add-Member -NotePropertyName certificateSha256 -NotePropertyValue $certificateSha256
[IO.File]::WriteAllText($provenancePath, (($provenance | ConvertTo-Json -Depth 40) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
$assets = @($msix, (Join-Path $StagingDirectory 'LFTPPilot.appinstaller'), $cer, $provenancePath)
$sourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'src'
$nugetLocks = @(Get-ChildItem $sourceRoot `
    -Recurse -Filter packages.lock.json -File | Where-Object FullName -NotMatch '\\(?:bin|obj)\\' | Select-Object -ExpandProperty FullName)
if ($nugetLocks.Count -lt 1) { throw 'No committed NuGet lock files were found for the release SBOM.' }
$projectAssets = @(Get-ChildItem $sourceRoot -Recurse -Filter project.assets.json -File |
    Where-Object FullName -Match '\\obj\\project\.assets\.json$' | Select-Object -ExpandProperty FullName)
if ($projectAssets.Count -ne $nugetLocks.Count) { throw 'Every production project must have restored project.assets.json release evidence.' }
& (Join-Path $PSScriptRoot 'New-Sbom.ps1') -Version $validatedVersion -RuntimeLockPath $RuntimeLockPath `
    -NuGetLockPath $nugetLocks -ProjectAssetsPath $projectAssets -ArtifactPath $assets `
    -OutputPath (Join-Path $StagingDirectory 'LFTPPilot.cdx.json') | Out-Null
$assets += Join-Path $StagingDirectory 'LFTPPilot.cdx.json'
$licenseArchive = Join-Path $StagingDirectory 'THIRD-PARTY-LICENSES.zip'
& (Join-Path $PSScriptRoot 'Stage-LicenseEvidence.ps1') -RuntimeLockPath $RuntimeLockPath `
    -LicenseManifestPath $LicenseManifestPath -NuGetLockPath $nugetLocks -ProjectAssetsPath $projectAssets `
    -OutputPath $licenseArchive | Out-Null
$assets += $licenseArchive
& (Join-Path $PSScriptRoot 'New-Checksums.ps1') -LiteralPath $assets -OutputPath (Join-Path $StagingDirectory 'SHA256SUMS.txt') | Out-Null
$assets += Join-Path $StagingDirectory 'SHA256SUMS.txt'
if ($PSCmdlet.ShouldProcess("$Repository $tag", 'Create immutable-intent public GitHub release')) {
    $sourceDigest = [string]$provenance.sourceDigest
    & gh release create $tag @assets --repo $Repository --target $sourceDigest --title "LFTP Pilot $validatedVersion" --generate-notes --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE." }
}
