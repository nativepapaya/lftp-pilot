[CmdletBinding()]
param(
    [string]$EvidenceRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'third-party-licenses'),
    [switch]$MarkComplete
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$evidenceRootResolved = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceRoot))
$runtimeLockPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.lock.json'
$runtimePackLockPath = Join-Path $PSScriptRoot 'runtime-lock\dotnet-runtime-packs.lock.json'
$runtimeLock = Get-Content -LiteralPath $runtimeLockPath -Raw | ConvertFrom-Json
Import-Module (Join-Path $PSScriptRoot 'DependencyEvidence.psm1') -Force

function Get-EvidenceHash {
    param([Parameter(Mandatory)][string]$RelativePath, [ValidateSet('SHA256','SHA512')][string]$Algorithm = 'SHA256')
    $path = Join-Path $evidenceRootResolved $RelativePath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required evidence file is missing: $RelativePath" }
    return (Get-FileHash -LiteralPath $path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function Copy-ManagedEvidenceFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$RelativeDestination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Managed evidence source is missing: $Source" }
    $destination = Join-Path $evidenceRootResolved $RelativeDestination.Replace('/', '\')
    [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
}

$nativeSourcePackage = @{
    'gcc-libs'='gcc'; 'openssl'='openssl'; 'libopenssl'='openssl'; 'libintl'='gettext'
    'libp11-kit'='p11-kit'; 'info'='texinfo'; 'libpcre2_8'='pcre2'; 'libexpat'='expat'
    'gettext'='gettext'; 'libgettextpo'='gettext'; 'libasprintf'='gettext'; 'libgnutls'='gnutls'
    'libnettle'='nettle'; 'libhogweed'='nettle'; 'libreadline'='readline'; 'heimdal-libs'='heimdal'
    'libdb'='db'; 'libsqlite'='sqlite'
}

$gpl3 = 'licenses/common/GPL-3.0.txt'
$lgpl21 = 'licenses/common/LGPL-2.1.txt'
$lgpl3 = 'licenses/common/LGPL-3.0.txt'
$nativeLicenses = @{
    'msys2-runtime'=@('licenses/native/msys2-runtime/COPYING','licenses/native/msys2-runtime-exception/CYGWIN_LICENSE')
    'lftp'=@($gpl3)
    'gcc-libs'=@($gpl3,$lgpl21,$lgpl3,'licenses/native/gcc-libs/RUNTIME.LIBRARY.EXCEPTION')
    'ca-certificates'=@('licenses/native/ca-certificates/copyright')
    'bash'=@($gpl3)
    'openssl'=@('licenses/native/libopenssl/LICENSE.txt')
    'libopenssl'=@('licenses/native/libopenssl/LICENSE.txt')
    'findutils'=@($gpl3)
    'libiconv'=@($lgpl21)
    'libintl'=@($gpl3,$lgpl21)
    'coreutils'=@($gpl3)
    'gmp'=@($lgpl3)
    'sed'=@($gpl3)
    'p11-kit'=@('licenses/native/p11-kit/COPYING')
    'libp11-kit'=@('licenses/native/p11-kit/COPYING')
    'libffi'=@('licenses/native/libffi/LICENSE')
    'libtasn1'=@($gpl3,$lgpl21)
    'info'=@($gpl3)
    'gzip'=@($gpl3)
    'less'=@($gpl3)
    'ncurses'=@('licenses/native/ncurses/LICENSE')
    'libpcre2_8'=@('licenses/native/pcre2/LICENCE.md')
    'libxcrypt'=@($lgpl21)
    'expat'=@('licenses/native/expat/COPYING')
    'gettext'=@($gpl3,$lgpl21)
    'libgettextpo'=@($gpl3,$lgpl21)
    'libasprintf'=@($gpl3,$lgpl21)
    'libexpat'=@('licenses/native/expat/COPYING')
    'libgnutls'=@($gpl3,$lgpl21)
    'libidn2'=@($gpl3,$lgpl3)
    'libunistring'=@($lgpl3)
    'libnettle'=@($lgpl3)
    'libhogweed'=@($lgpl3)
    'zlib'=@('licenses/native/zlib/LICENSE')
    'libreadline'=@($gpl3)
    'openssh'=@('licenses/native/openssh/LICENCE')
    'heimdal'=@('licenses/native/heimdal/LICENSE')
    'heimdal-libs'=@('licenses/native/heimdal/LICENSE')
    'libdb'=@('licenses/common/Berkeley-DB-AGPL-3.0.txt')
    'libedit'=@('licenses/native/libedit/LICENSE')
    'libsqlite'=@('licenses/native/libsqlite/LICENSE')
    'libfido2'=@('licenses/native/libfido2/LICENSE')
    'libcbor'=@('licenses/native/libcbor/LICENSE.md')
}

$nativePackages = foreach ($package in $runtimeLock.packages) {
    if (-not $nativeLicenses.ContainsKey([string]$package.name)) {
        throw "No reviewed native license mapping exists for '$($package.name)'."
    }
    $sourceBase = if ($nativeSourcePackage.ContainsKey([string]$package.name)) {
        $nativeSourcePackage[[string]$package.name]
    } else { [string]$package.name }
    $sourceFile = "$sourceBase-$($package.version).src.tar.zst"
    $sourceRelative = "sources/native/$sourceFile"
    [ordered]@{
        name = [string]$package.name
        version = [string]$package.version
        filename = [string]$package.filename
        sha256 = [string]$package.sha256
        licenseFiles = @($nativeLicenses[[string]$package.name] | ForEach-Object {
            [ordered]@{ path = $_; sha256 = Get-EvidenceHash $_ }
        })
        correspondingSource = [ordered]@{
            kind = 'archive'
            path = $sourceRelative
            sha256 = Get-EvidenceHash $sourceRelative
            url = "https://repo.msys2.org/msys/sources/$sourceFile"
        }
    }
}

$nugetLocks = @(Get-ChildItem (Join-Path $repositoryRoot 'src') -Recurse -Filter packages.lock.json | ForEach-Object FullName)
$projectAssets = @(Get-ChildItem (Join-Path $repositoryRoot 'src') -Recurse -Filter project.assets.json | ForEach-Object FullName)
$managedDependencies = @(
    Get-ProductionNuGetEvidence -NuGetLockPath $nugetLocks
    Get-SelfContainedRuntimePackEvidence -ProjectAssetsPath $projectAssets -RuntimePackLockPath $runtimePackLockPath `
        -GlobalJsonPath (Join-Path $repositoryRoot 'global.json')
)
$runtimePackNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($pack in (Get-Content -LiteralPath $runtimePackLockPath -Raw | ConvertFrom-Json).packs) { [void]$runtimePackNames.Add([string]$pack.name) }

$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$sdkMsixRoot = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools.msix\1.7.251221100'
$runtimeRoot = Join-Path $nugetRoot 'microsoft.netcore.app.runtime.win-x64\10.0.10'
$managedPackages = foreach ($package in ($managedDependencies | Sort-Object Id,Version)) {
    $id = [string]$package.Id
    $version = [string]$package.Version
    $idLower = $id.ToLowerInvariant()
    $packageRoot = Join-Path $nugetRoot "$idLower\$version"
    $archiveName = "$idLower.$($version.ToLowerInvariant()).nupkg"
    $archiveSource = Join-Path $packageRoot $archiveName
    $archiveRelative = "managed-packages/$archiveName"
    Copy-ManagedEvidenceFile $archiveSource $archiveRelative

    $licenseSources = @(switch ($id) {
        'Microsoft.Windows.SDK.BuildTools' { @((Join-Path $sdkMsixRoot 'sdk_license.txt')) }
        'Microsoft.Windows.SDK.BuildTools.MSIX' {
            @((Join-Path $packageRoot 'sdk_license.txt'), (Join-Path $packageRoot 'NOTICE.txt'))
        }
        'System.Security.Cryptography.ProtectedData' {
            @((Join-Path $runtimeRoot 'LICENSE.TXT'), (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.TXT'))
        }
        default {
            @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object {
                $_.Name -match '^(?i)(license|licence|copying|copyright|notice|third.?party.?notices).*\.txt$' -or
                $_.Name -match '^(?i)(license|licence|copying|copyright|notice|third.?party.?notices)$'
            } | Select-Object -ExpandProperty FullName)
        }
    })
    if ($licenseSources.Count -lt 1) { throw "No reviewed managed license or notice file exists for '$id $version'." }
    $safeDirectory = ($idLower + '-' + $version.ToLowerInvariant()) -replace '[^a-z0-9._-]', '_'
    $managedLicenseRecords = foreach ($source in $licenseSources) {
        $relative = "licenses/managed/$safeDirectory/$([IO.Path]::GetFileName($source))"
        Copy-ManagedEvidenceFile $source $relative
        [ordered]@{ path = $relative; sha256 = Get-EvidenceHash $relative }
    }
    $category = if ($runtimePackNames.Contains($id)) { 'dotnet-runtime-pack' } else { 'nuget' }
    $reviewNote = if ($id.StartsWith('System.', [StringComparison]::Ordinal) -or $category -eq 'dotnet-runtime-pack') {
        'The MIT terms and bundled notices were reviewed; notice retention applies and no source-code delivery condition was identified.'
    } else {
        'The packaged Microsoft redistribution terms and bundled notices were reviewed; no source-code delivery condition was identified.'
    }
    [ordered]@{
        name = $id
        version = $version
        category = $category
        sha512 = [string]$package.Sha512
        licenseFiles = @($managedLicenseRecords)
        distributionArchive = [ordered]@{
            kind = 'nupkg'
            path = $archiveRelative
            sha512 = Get-EvidenceHash $archiveRelative 'SHA512'
            url = "https://api.nuget.org/v3-flatcontainer/$idLower/$($version.ToLowerInvariant())/$archiveName"
        }
        reviewedObligations = [ordered]@{
            sourceCodeRequired = $false
            reviewNote = $reviewNote
        }
    }
}

$manifest = [ordered]@{
    schema = 3
    complete = [bool]$MarkComplete
    bundle = [ordered]@{
        schema = $runtimeLock.schema
        bundleRevision = [string]$runtimeLock.bundleRevision
        architecture = [string]$runtimeLock.architecture
        inventorySha256 = [string]$runtimeLock.inventorySha256
    }
    packages = @($nativePackages)
    managedPackages = @($managedPackages)
}
$manifestPath = Join-Path $evidenceRootResolved 'licenses-manifest.json'
$json = $manifest | ConvertTo-Json -Depth 10
Set-Content -LiteralPath $manifestPath -Value $json -Encoding utf8
Get-Item -LiteralPath $manifestPath
