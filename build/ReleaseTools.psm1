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
        '--cert-oidc-issuer', 'https://token.actions.githubusercontent.com',
        '--predicate-type', 'https://slsa.dev/provenance/v1',
        '--format', 'json'
    )
}

function Invoke-GitHubBuildProvenanceVerification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw 'The GitHub CLI executable could not be found.'
    }
    if ($Arguments.Count -eq 0) { throw 'GitHub CLI verification arguments are required.' }

    $standardErrorPath = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-gh-attestation-$([Guid]::NewGuid().ToString('N')).stderr"
    $savedNativePreference = if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference } else { $null }
    $savedErrorActionPreference = $ErrorActionPreference
    $lines = @()
    $exitCode = -1
    try {
        try {
            # Windows PowerShell 5 promotes native stderr to ErrorRecord objects
            # under Stop. Keep stdout as parseable JSON and retain stderr only
            # for a bounded failure diagnostic.
            $ErrorActionPreference = 'Continue'
            if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }
            $lines = @(& $ExecutablePath @Arguments 2> $standardErrorPath)
            $exitCode = $LASTEXITCODE
        }
        finally {
            if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $savedNativePreference }
            $ErrorActionPreference = $savedErrorActionPreference
        }
        $standardError = if (Test-Path -LiteralPath $standardErrorPath) {
            [IO.File]::ReadAllText($standardErrorPath)
        } else { '' }
        if ($standardError.Length -gt 8192) { $standardError = $standardError.Substring(0, 8192) }
        if ($exitCode -ne 0) {
            throw "GitHub build-provenance verification failed with exit code $exitCode. $standardError"
        }

        $json = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        try { $verification = $json | ConvertFrom-Json }
        catch { throw 'GitHub returned invalid JSON after provenance verification.' }
        if ($null -eq $verification -or @($verification).Count -lt 1) {
            throw 'GitHub returned no matching build provenance.'
        }

        $publicGoodVerified = $false
        foreach ($entry in @($verification)) {
            $certificate = $entry.verificationResult.signature.certificate
            $transparencyTimestamps = @($entry.verificationResult.verifiedTimestamps | Where-Object type -CEQ 'Tlog')
            if ($certificate.sourceRepositoryVisibilityAtSigning -ceq 'public' -and $transparencyTimestamps.Count -gt 0) {
                $publicGoodVerified = $true
                break
            }
        }
        if (-not $publicGoodVerified) {
            throw 'The public-repository attestation did not contain a verified transparency-log timestamp and public source identity.'
        }

        return [pscustomobject]@{ Results = @($verification) }
    }
    finally {
        if (Test-Path -LiteralPath $standardErrorPath) { Remove-Item -LiteralPath $standardErrorPath -Force }
    }
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
    Invoke-GitHubBuildProvenanceVerification, Get-CertificateSha256, Assert-CertificateContinuity
