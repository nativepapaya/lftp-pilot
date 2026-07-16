[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicCertificate,
    [string]$Repository = 'nativepapaya/lftp-pilot',
    [switch]$ApproveInitialReleaseCertificate
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
[void](Assert-ReleaseRepository $Repository)
$current = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $PublicCertificate).Path)
try {
    [void](Assert-ReleaseCertificate $current)
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh) { throw 'GitHub CLI is required to verify release certificate continuity.' }
    $lines = @(& $gh.Source api --paginate --slurp -H 'X-GitHub-Api-Version: 2026-03-10' `
        'repos/nativepapaya/lftp-pilot/releases?per_page=100' 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "GitHub release-history query failed. $($lines -join [Environment]::NewLine)" }
    try { $pages = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) | ConvertFrom-Json }
    catch { throw 'GitHub returned invalid release-history JSON.' }
    $published = @(
        foreach ($page in @($pages)) {
            foreach ($release in @($page)) {
                if ($release.draft -eq $false -and $release.published_at) { $release }
            }
        }
    )
    if ($published.Count -eq 0) {
        return Assert-CertificateContinuity -CurrentCertificate $current `
            -ApproveInitialReleaseCertificate:$ApproveInitialReleaseCertificate
    }
    $latest = $published | Sort-Object { [DateTimeOffset]$_.published_at } -Descending | Select-Object -First 1
    if ($latest.immutable -ne $true) {
        throw "Previous release '$($latest.tag_name)' is not immutable; certificate continuity evidence is not trustworthy."
    }
    $certificateAssets = @($latest.assets | Where-Object { $_.name -ceq 'LFTPPilot.cer' })
    if ($certificateAssets.Count -ne 1) {
        throw "Previous immutable release '$($latest.tag_name)' must contain exactly one LFTPPilot.cer asset."
    }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-cert-continuity-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.Directory]::CreateDirectory($temporary) | Out-Null
        & $gh.Source release download ([string]$latest.tag_name) --repo 'nativepapaya/lftp-pilot' `
            --pattern 'LFTPPilot.cer' --dir $temporary --clobber
        if ($LASTEXITCODE -ne 0) { throw "Could not download LFTPPilot.cer from '$($latest.tag_name)'." }
        $downloaded = @(Get-ChildItem -LiteralPath $temporary -File -Filter 'LFTPPilot.cer')
        if ($downloaded.Count -ne 1) { throw 'The previous release certificate download was missing or ambiguous.' }
        $previous = [Security.Cryptography.X509Certificates.X509Certificate2]::new($downloaded[0].FullName)
        try {
            return Assert-CertificateContinuity -CurrentCertificate $current -PreviousCertificate $previous -PreviousReleaseExists
        }
        finally { $previous.Dispose() }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force } }
}
finally { $current.Dispose() }
