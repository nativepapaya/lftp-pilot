[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsixPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedCertificate,
    [string]$RuntimeLockPath,
    [string]$RuntimeInventoryPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RuntimeLockPath) { $RuntimeLockPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.lock.json' }
if (-not $RuntimeInventoryPath) { $RuntimeInventoryPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.files.json' }
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PackageValidation.psm1') -Force

$resolved = (Resolve-Path -LiteralPath $MsixPath).Path
$certificatePath = (Resolve-Path -LiteralPath $ExpectedCertificate).Path
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
try {
    [void](Assert-ReleaseCertificate -Certificate $certificate)
    $result = Test-LftpPilotMsix -MsixPath $resolved -ExpectedVersion $ExpectedVersion -SignatureMode Signed `
        -RuntimeLockPath $RuntimeLockPath -RuntimeInventoryPath $RuntimeInventoryPath

    $signature = Get-AuthenticodeSignature -LiteralPath $resolved
    if ($signature.Status.ToString() -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "Authenticode verification failed: $($signature.Status) $($signature.StatusMessage)"
    }
    if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The MSIX signer differs from the reviewed public release certificate.'
    }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signTool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -File |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $signTool) { throw 'Windows SDK signtool.exe (x64) was not found.' }
    & $signTool.FullName verify /pa /all $resolved
    if ($LASTEXITCODE -ne 0) { throw "signtool verification failed with exit code $LASTEXITCODE." }
}
finally { $certificate.Dispose() }

"Verified signed MSIX and $($result.RuntimeFileCount) exact locked runtime entries: $resolved"
