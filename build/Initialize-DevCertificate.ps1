[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PublicCertificatePath = (Join-Path (Get-Location) 'LFTPPilot.cer'),
    [ValidateRange(1, 5)][int]$ValidYears = 3
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Certificate initialization requires Windows.' }
$existing = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=LFTPPilot.Dev')
if ($existing.Count -gt 0) { throw 'A LFTPPilot.Dev certificate already exists. Reuse its thumbprint; never create competing update identities.' }
if (-not $PSCmdlet.ShouldProcess('Cert:\CurrentUser\My', 'Create non-exportable LFTP Pilot code-signing certificate')) { return }
$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=LFTPPilot.Dev' `
    -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable -KeySpec Signature -NotAfter (Get-Date).AddYears($ValidYears)
try {
    [void](Assert-ReleaseCertificate -Certificate $certificate -RequireNonExportablePrivateKey)
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublicCertificatePath)
    Export-Certificate -Cert $certificate -FilePath $resolved -Type CERT -Force | Out-Null
    Import-Certificate -FilePath $resolved -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    [pscustomobject]@{
        Subject = $certificate.Subject
        Thumbprint = $certificate.Thumbprint
        PublicCertificate = $resolved
        PrivateKeyExportable = $false
        TrustedForCurrentUserVerification = $true
    }
}
catch {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
    throw
}
