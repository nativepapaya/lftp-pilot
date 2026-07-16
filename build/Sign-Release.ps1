[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$UnsignedMsix,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$OutputPath,
    [Uri]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$ProvenanceOutputPath,
    [switch]$ApproveInitialReleaseCertificate
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PackageValidation.psm1') -Force
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
[void](Assert-ReleaseCertificate -Certificate $certificate -RequireNonExportablePrivateKey)
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signTool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -File |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $signTool) { throw 'Windows SDK signtool.exe (x64) was not found.' }
$source = (Resolve-Path -LiteralPath $UnsignedMsix).Path
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ($source -eq $destination) { throw 'Sign a staged copy, never the CI input in place.' }
$provenancePath = if ($ProvenanceOutputPath) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProvenanceOutputPath)
} else { Join-Path ([IO.Path]::GetDirectoryName($destination)) 'LFTPPilot.provenance.json' }
& (Join-Path $PSScriptRoot 'Test-BuildProvenance.ps1') -ArtifactPath $source -OutputPath $provenancePath | Out-Null
$certificateFixture = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-current-cert-$([Guid]::NewGuid().ToString('N')).cer"
try {
    [IO.File]::WriteAllBytes($certificateFixture, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    & (Join-Path $PSScriptRoot 'Test-CertificateContinuity.ps1') -PublicCertificate $certificateFixture `
        -ApproveInitialReleaseCertificate:$ApproveInitialReleaseCertificate | Out-Null
}
finally { if (Test-Path -LiteralPath $certificateFixture) { Remove-Item -LiteralPath $certificateFixture -Force } }
& (Join-Path $PSScriptRoot 'Test-NuGetLocks.ps1') | Out-Null
$unsignedValidation = Test-LftpPilotMsix -MsixPath $source -SignatureMode Unsigned
if ($PSCmdlet.ShouldProcess($destination, 'Copy, Authenticode-sign, and verify MSIX')) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    & $signTool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $destination
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE." }
    Compare-LftpPilotSignedPayload -UnsignedMsix $source -SignedMsix $destination | Out-Null
    Test-LftpPilotMsix -MsixPath $destination -ExpectedVersion $unsignedValidation.Version -SignatureMode Signed | Out-Null
    & $signTool.FullName verify /pa /all /v $destination
    if ($LASTEXITCODE -ne 0) { throw "signtool verification failed with exit code $LASTEXITCODE." }
    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    if ($signature.Status.ToString() -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -cne $certificate.Thumbprint) {
        throw 'The signed package does not have a valid signature from the selected reviewed certificate.'
    }
}
if (Test-Path -LiteralPath $destination) { Get-Item -LiteralPath $destination }
