[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeLockPath,
    [Parameter(Mandatory)][string]$LicenseManifestPath,
    [Parameter(Mandatory)][string[]]$NuGetLockPath,
    [Parameter(Mandatory)][string[]]$ProjectAssetsPath,
    [string]$RuntimePackLockPath = (Join-Path $PSScriptRoot 'runtime-lock\dotnet-runtime-packs.lock.json'),
    [string]$GlobalJsonPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'global.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DependencyEvidence.psm1') -Force

function Resolve-EvidenceFile {
    param([string]$Base, [string]$RelativePath, [string]$Label)
    if (-not $RelativePath -or $RelativePath.Contains('\') -or [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|/)\.\.?(?:/|$)') { throw "$Label has an unsafe relative path." }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Base $RelativePath.Replace('/', '\')))
    if (-not $resolved.StartsWith($Base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Label is missing or outside the evidence directory." }
    return $resolved
}

function Assert-PublicSourceUrl {
    param([string]$Value, [string]$ExpectedFileName, [string]$PackageName)
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or
        $uri.UserInfo -or $uri.Fragment -or $uri.Query -or -not $uri.IsDefaultPort -or
        [Uri]::UnescapeDataString([IO.Path]::GetFileName($uri.AbsolutePath)) -cne $ExpectedFileName) {
        throw "Corresponding-source URL for '$PackageName' must be a direct public HTTPS URL for the staged archive."
    }
    $sourceHost = $uri.DnsSafeHost.Trim('[',']').ToLowerInvariant()
    if (-not $sourceHost -or -not $sourceHost.Contains('.') -or $sourceHost -eq 'localhost' -or
        $sourceHost -match '(?:^|\.)(?:localhost|local|invalid|test|internal|home|lan|corp)$' -or
        $sourceHost -match '^(?:.+\.)?example\.(?:com|net|org)$') { throw "Corresponding-source URL for '$PackageName' is not public." }
    $address = $null
    if ([Net.IPAddress]::TryParse($sourceHost, [ref]$address)) {
        if ($address.IsIPv4MappedToIPv6) { $address = $address.MapToIPv4() }
        if ([Net.IPAddress]::IsLoopback($address) -or $address.IsIPv6LinkLocal -or $address.IsIPv6Multicast -or
            $address.IsIPv6SiteLocal) { throw "Corresponding-source URL for '$PackageName' is not public." }
        if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
            $bytes = $address.GetAddressBytes()
            if ($bytes[0] -in @(0,10,127) -or ($bytes[0] -eq 169 -and $bytes[1] -eq 254) -or
                ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
                ($bytes[0] -eq 192 -and $bytes[1] -eq 168) -or $bytes[0] -ge 224) {
                throw "Corresponding-source URL for '$PackageName' is not public."
            }
        }
        else {
            $bytes = $address.GetAddressBytes()
            if ($address.Equals([Net.IPAddress]::IPv6Any) -or ($bytes[0] -band 0xfe) -eq 0xfc -or
                ($bytes[0] -eq 0x20 -and $bytes[1] -eq 0x01 -and $bytes[2] -eq 0x0d -and $bytes[3] -eq 0xb8)) {
                throw "Corresponding-source URL for '$PackageName' is not public."
            }
        }
    }
}

$lock = Get-Content -LiteralPath $RuntimeLockPath -Raw | ConvertFrom-Json
$manifestResolved = (Resolve-Path -LiteralPath $LicenseManifestPath).Path
$evidence = Get-Content -LiteralPath $manifestResolved -Raw | ConvertFrom-Json
if ($lock.schema -ne 3 -or $evidence.schema -ne 3 -or $evidence.complete -ne $true -or $null -eq $evidence.bundle -or
    $null -eq $evidence.PSObject.Properties['managedPackages']) {
    throw 'License evidence must be independently reviewed, complete schema 3 evidence covering native and managed production dependencies.'
}
foreach ($field in @('schema','bundleRevision','architecture','inventorySha256')) {
    if ([string]$evidence.bundle.$field -cne [string]$lock.$field) { throw "License evidence bundle $field differs from the runtime lock." }
}
$locked = @{}; foreach ($package in $lock.packages) {
    if ($locked.ContainsKey($package.name)) { throw "Duplicate runtime package '$($package.name)'." }
    $locked[$package.name] = $package
}
if (@($evidence.packages).Count -ne $locked.Count) { throw 'License evidence package count differs from the runtime lock.' }
$base = Split-Path $manifestResolved -Parent
$seenPackages = @{}; $seenFiles = @{}
foreach ($item in $evidence.packages) {
    if (-not $item.name -or $seenPackages.ContainsKey($item.name) -or -not $locked.ContainsKey($item.name)) {
        throw "License evidence contains an invalid, duplicate, or unexpected package '$($item.name)'."
    }
    $seenPackages[$item.name] = $true
    $package = $locked[$item.name]
    if ($item.version -cne $package.version -or $item.filename -cne $package.filename -or $item.sha256 -cne $package.sha256) {
        throw "License evidence for '$($item.name)' differs from the runtime lock."
    }
    if (@($item.licenseFiles).Count -lt 1) { throw "License files for '$($item.name)' are missing." }
    foreach ($file in $item.licenseFiles) {
        if ($file.sha256 -notmatch '^[a-f0-9]{64}$') { throw "License hash for '$($item.name)' is invalid." }
        $resolved = Resolve-EvidenceFile $base ([string]$file.path) "$($item.name) license"
        if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "License file evidence for '$($item.name)' has the wrong digest."
        }
        $key = ([string]$file.path).ToLowerInvariant()
        if ($seenFiles.ContainsKey($key) -and $seenFiles[$key] -cne $file.sha256) { throw "Evidence path '$($file.path)' has conflicting hashes." }
        $seenFiles[$key] = $file.sha256
    }
    $source = $item.correspondingSource
    if ($null -eq $source -or $source.kind -cne 'archive' -or $source.sha256 -notmatch '^[a-f0-9]{64}$' -or
        -not ([string]$source.path).StartsWith('sources/', [StringComparison]::Ordinal)) {
        throw "Corresponding-source archive evidence for '$($item.name)' is incomplete."
    }
    $sourcePath = Resolve-EvidenceFile $base ([string]$source.path) "$($item.name) corresponding source"
    $sourceName = [IO.Path]::GetFileName($sourcePath)
    Assert-PublicSourceUrl ([string]$source.url) $sourceName ([string]$item.name)
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $source.sha256) {
        throw "Corresponding-source archive for '$($item.name)' has the wrong digest."
    }
    $sourceKey = ([string]$source.path).ToLowerInvariant()
    if ($seenFiles.ContainsKey($sourceKey) -and $seenFiles[$sourceKey] -cne $source.sha256) { throw "Evidence path '$($source.path)' has conflicting hashes." }
    $seenFiles[$sourceKey] = $source.sha256
}

$expectedManaged = @{}
foreach ($package in @(Get-ProductionNuGetEvidence -NuGetLockPath $NuGetLockPath)) {
    $key = "$($package.Id.ToLowerInvariant())@$($package.Version)"
    if ($expectedManaged.ContainsKey($key)) { throw "Duplicate production NuGet dependency '$key'." }
    $expectedManaged[$key] = [pscustomobject]@{ Id=$package.Id; Version=$package.Version; Sha512=$package.Sha512; Category='nuget' }
}
foreach ($package in @(Get-SelfContainedRuntimePackEvidence -ProjectAssetsPath $ProjectAssetsPath `
    -RuntimePackLockPath $RuntimePackLockPath -GlobalJsonPath $GlobalJsonPath)) {
    $key = "$($package.Id.ToLowerInvariant())@$($package.Version)"
    if ($expectedManaged.ContainsKey($key)) { throw "Runtime pack '$key' ambiguously duplicates a production NuGet dependency." }
    $expectedManaged[$key] = [pscustomobject]@{ Id=$package.Id; Version=$package.Version; Sha512=$package.Sha512; Category='dotnet-runtime-pack' }
}
if (@($evidence.managedPackages).Count -ne $expectedManaged.Count) {
    throw 'Managed license/source evidence count differs from the production dependency graph.'
}
$seenManaged = @{}
foreach ($item in $evidence.managedPackages) {
    $key = "$(([string]$item.name).ToLowerInvariant())@$([string]$item.version)"
    if (-not $item.name -or $seenManaged.ContainsKey($key) -or -not $expectedManaged.ContainsKey($key)) {
        throw "Managed evidence contains an invalid, duplicate, or unexpected dependency '$key'."
    }
    $seenManaged[$key] = $true; $expected = $expectedManaged[$key]
    if ([string]$item.name -cne $expected.Id -or [string]$item.version -cne $expected.Version -or
        [string]$item.sha512 -cne $expected.Sha512 -or [string]$item.category -cne $expected.Category) {
        throw "Managed evidence for '$key' differs from production dependency evidence."
    }
    if (@($item.licenseFiles).Count -lt 1) { throw "Managed license files for '$key' are missing." }
    foreach ($file in $item.licenseFiles) {
        if ($file.sha256 -notmatch '^[a-f0-9]{64}$') { throw "Managed license hash for '$key' is invalid." }
        $resolved = Resolve-EvidenceFile $base ([string]$file.path) "$key managed license"
        if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "Managed license file for '$key' has the wrong digest."
        }
        $fileKey = ([string]$file.path).ToLowerInvariant()
        if ($seenFiles.ContainsKey($fileKey) -and $seenFiles[$fileKey] -cne $file.sha256) { throw "Evidence path '$($file.path)' has conflicting hashes." }
        $seenFiles[$fileKey] = $file.sha256
    }
    $distribution = $item.distributionArchive
    if ($null -eq $distribution -or $distribution.kind -cne 'nupkg' -or $distribution.sha512 -cne $expected.Sha512 -or
        -not ([string]$distribution.path).StartsWith('managed-packages/', [StringComparison]::Ordinal)) {
        throw "Managed distribution archive for '$key' is incomplete or differs from its locked SHA-512."
    }
    $distributionPath = Resolve-EvidenceFile $base ([string]$distribution.path) "$key managed distribution"
    Assert-PublicSourceUrl ([string]$distribution.url) ([IO.Path]::GetFileName($distributionPath)) $key
    if ((Get-FileHash -LiteralPath $distributionPath -Algorithm SHA512).Hash.ToLowerInvariant() -cne $expected.Sha512) {
        throw "Managed distribution archive for '$key' has the wrong digest."
    }
    $distributionKey = ([string]$distribution.path).ToLowerInvariant()
    if ($seenFiles.ContainsKey($distributionKey) -and $seenFiles[$distributionKey] -cne $expected.Sha512) { throw "Evidence path '$($distribution.path)' has conflicting hashes." }
    $seenFiles[$distributionKey] = $expected.Sha512
    $obligations = $item.PSObject.Properties['reviewedObligations']
    if ($null -eq $obligations -or $null -eq $obligations.Value.PSObject.Properties['sourceCodeRequired'] -or
        $obligations.Value.sourceCodeRequired -isnot [bool] -or ([string]$obligations.Value.reviewNote).Trim().Length -lt 10) {
        throw "Managed licensing obligations for '$key' have not been explicitly reviewed."
    }
    if ($obligations.Value.sourceCodeRequired) {
        $sourceProperty = $item.PSObject.Properties['correspondingSource']
        if ($null -eq $sourceProperty -or $sourceProperty.Value.kind -cne 'archive' -or
            [string]$sourceProperty.Value.sha256 -notmatch '^[a-f0-9]{64}$' -or
            -not ([string]$sourceProperty.Value.path).StartsWith('sources/managed/', [StringComparison]::Ordinal)) {
            throw "Required corresponding-source evidence for '$key' is incomplete."
        }
        $managedSource = Resolve-EvidenceFile $base ([string]$sourceProperty.Value.path) "$key corresponding source"
        Assert-PublicSourceUrl ([string]$sourceProperty.Value.url) ([IO.Path]::GetFileName($managedSource)) $key
        if ((Get-FileHash -LiteralPath $managedSource -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceProperty.Value.sha256) {
            throw "Corresponding-source archive for '$key' has the wrong digest."
        }
        $managedSourceKey = ([string]$sourceProperty.Value.path).ToLowerInvariant()
        if ($seenFiles.ContainsKey($managedSourceKey) -and $seenFiles[$managedSourceKey] -cne $sourceProperty.Value.sha256) {
            throw "Evidence path '$($sourceProperty.Value.path)' has conflicting hashes."
        }
        $seenFiles[$managedSourceKey] = [string]$sourceProperty.Value.sha256
    }
}
'License, redistribution, and source-obligation evidence exactly covers the native runtime and managed production dependency graph.'
