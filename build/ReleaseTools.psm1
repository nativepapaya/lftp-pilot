Set-StrictMode -Version Latest

function Assert-MsixVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "'$Version' is not a four-part MSIX version."
    }
    foreach ($part in $Version.Split('.')) {
        [uint32]$number = 0
        if (-not [uint32]::TryParse($part, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -gt [uint16]::MaxValue) {
            throw "'$Version' is invalid; every MSIX version part must be between 0 and 65535."
        }
    }
    return [Version]$Version
}

function New-MsixVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+$')][string]$ProductVersion,
        [Parameter(Mandatory)][ValidateRange(0, 4294967295)][uint64]$Sequence,
        [string]$PreviousVersion
    )
    [uint64]$build = [Math]::Floor($Sequence / 65536)
    [uint64]$revision = $Sequence % 65536
    if ($build -gt [uint16]::MaxValue) { throw "Release sequence $Sequence exceeds MSIX version capacity." }
    $candidate = Assert-MsixVersion -Version "$ProductVersion.$build.$revision"
    if ($PreviousVersion) {
        $previous = Assert-MsixVersion -Version $PreviousVersion
        if ($candidate -le $previous) {
            throw "MSIX version $candidate must be greater than previous version $previous."
        }
    }
    return $candidate.ToString(4)
}

function Get-FileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { throw "File not found: $LiteralPath" }
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ReleaseRepository {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repository)
    if ($Repository -cne 'nativepapaya/lftp-pilot') {
        throw "Production release tooling is permanently scoped to nativepapaya/lftp-pilot, not '$Repository'."
    }
    return $Repository
}

function Get-ReviewedSourceCommit {
    [CmdletBinding()]
    param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) { throw 'Git is required to bind a release candidate to reviewed source.' }
    $root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $commit = (& $git.Source -C $root rev-parse --verify 'HEAD^{commit}' 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]$commit -notmatch '^[0-9a-f]{40}$') {
        throw 'A committed Git HEAD is required to verify production provenance.'
    }
    $status = @(& $git.Source -C $root status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Git could not verify the release source worktree.' }
    if ($status.Count -ne 0) { throw 'Production release verification requires a completely clean worktree, including no untracked files.' }
    return ([string]$commit).ToLowerInvariant()
}

function Get-BuildProvenanceVerificationArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArtifactPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceDigest,
        [string]$Repository = 'nativepapaya/lftp-pilot'
    )
    [void](Assert-ReleaseRepository $Repository)
    return [string[]]@(
        'attestation', 'verify', $ArtifactPath,
        '--repo', 'nativepapaya/lftp-pilot',
        '--signer-workflow', 'nativepapaya/lftp-pilot/.github/workflows/unsigned-package.yml',
        '--source-ref', 'refs/heads/main',
        '--source-digest', $SourceDigest.ToLowerInvariant(),
        '--signer-digest', $SourceDigest.ToLowerInvariant(),
        '--deny-self-hosted-runners',
        '--no-public-good',
        '--cert-oidc-issuer', 'https://token.actions.githubusercontent.com',
        '--predicate-type', 'https://slsa.dev/provenance/v1',
        '--format', 'json'
    )
}

function Get-CertificateSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Certificate.RawData))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Assert-CertificateContinuity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$CurrentCertificate,
        [Security.Cryptography.X509Certificates.X509Certificate2]$PreviousCertificate,
        [switch]$PreviousReleaseExists,
        [switch]$ApproveInitialReleaseCertificate
    )
    if (-not $PreviousReleaseExists) {
        if (-not $ApproveInitialReleaseCertificate) {
            throw 'No previous immutable release exists. The first release certificate requires -ApproveInitialReleaseCertificate after explicit review.'
        }
        return [pscustomobject]@{ InitialRelease=$true; CertificateSha256=(Get-CertificateSha256 $CurrentCertificate) }
    }
    if ($null -eq $PreviousCertificate) {
        throw 'The latest immutable release does not provide LFTPPilot.cer; certificate continuity cannot be established.'
    }
    $current = Get-CertificateSha256 $CurrentCertificate
    $previous = Get-CertificateSha256 $PreviousCertificate
    if ($current -cne $previous) {
        throw "The release certificate changed (current $current, previous $previous). Update identity continuity is mandatory."
    }
    return [pscustomobject]@{ InitialRelease=$false; CertificateSha256=$current }
}

function Assert-ReleaseCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [switch]$RequireNonExportablePrivateKey
    )
    $now = [DateTime]::UtcNow
    if ($Certificate.Subject -cne 'CN=LFTPPilot.Dev' -or $now -lt $Certificate.NotBefore.ToUniversalTime() -or
        $now -gt $Certificate.NotAfter.ToUniversalTime()) {
        throw 'The release certificate has the wrong subject or is outside its validity period.'
    }
    $codeSigning = $false
    $digitalSignature = $false
    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($usage in $extension.EnhancedKeyUsages) {
                if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { $codeSigning = $true }
            }
        }
        if ($extension -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension] -and
            ($extension.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature)) {
            $digitalSignature = $true
        }
    }
    if (-not $codeSigning -or -not $digitalSignature) {
        throw 'The release certificate must have code-signing EKU and digital-signature key usage.'
    }
    if ($RequireNonExportablePrivateKey) {
        if (-not $Certificate.HasPrivateKey) { throw 'The release certificate has no private key.' }
        $rsa = $Certificate.GetRSAPrivateKey()
        try {
            if ($rsa -isnot [Security.Cryptography.RSACng] -or $rsa.KeySize -lt 3072 -or
                $rsa.Key.ExportPolicy -ne [Security.Cryptography.CngExportPolicies]::None) {
                throw 'Release signing requires a 3072-bit or stronger non-exportable RSACng key.'
            }
        }
        finally { if ($null -ne $rsa) { $rsa.Dispose() } }
    }
    return $Certificate
}

Export-ModuleMember -Function Assert-MsixVersion, New-MsixVersion, Get-FileSha256, Assert-ReleaseCertificate, `
    Assert-ReleaseRepository, Get-ReviewedSourceCommit, Get-BuildProvenanceVerificationArguments, `
    Get-CertificateSha256, Assert-CertificateContinuity
