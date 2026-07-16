Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-StreamSha256 {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Get-BytesSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try { return Get-StreamSha256 -Stream $stream }
    finally { $stream.Dispose() }
}

function Assert-JsonEqual {
    param($Expected, $Actual, [Parameter(Mandatory)][string]$Label)
    $left = ConvertTo-Json -InputObject $Expected -Depth 30 -Compress
    $right = ConvertTo-Json -InputObject $Actual -Depth 30 -Compress
    if ($left -cne $right) { throw "$Label differs between the runtime lock and reviewed inventory." }
}

function Assert-CommittedEvidenceFile {
    param([Parameter(Mandatory)][string]$Path)
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) { return }
    $probe = [IO.DirectoryInfo]::new((Split-Path $Path -Parent))
    while ($null -ne $probe -and -not (Test-Path -LiteralPath (Join-Path $probe.FullName '.git'))) { $probe = $probe.Parent }
    if ($null -eq $probe) { return }
    $root = $probe.FullName
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $git.Source -C $root rev-parse --verify HEAD *> $null
        $headExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedPreference }
    if ($headExit -ne 0) { return }
    $root = [IO.Path]::GetFullPath([string]$root)
    if (-not $Path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release evidence is outside its Git repository: $Path"
    }
    $relative = $Path.Substring($root.Length + 1).Replace('\','/')
    try {
        $ErrorActionPreference = 'Continue'
        & $git.Source -C $root ls-files --error-unmatch -- $relative *> $null
        $trackedExit = $LASTEXITCODE
        & $git.Source -C $root diff --quiet HEAD -- $relative *> $null
        $diffExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedPreference }
    if ($trackedExit -ne 0) { throw "Release evidence is not committed: $relative" }
    if ($diffExit -ne 0) { throw "Release evidence has unreviewed changes: $relative" }
}

function Get-ReviewedRuntimeEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RuntimeLockPath,
        [Parameter(Mandatory)][string]$RuntimeInventoryPath
    )
    $lockPath = (Resolve-Path -LiteralPath $RuntimeLockPath).Path
    $inventoryPath = (Resolve-Path -LiteralPath $RuntimeInventoryPath).Path
    Assert-CommittedEvidenceFile $lockPath
    Assert-CommittedEvidenceFile $inventoryPath
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if ($lock.schema -ne 3 -or $inventory.schema -ne 3 -or $lock.architecture -ne 'x64' -or
        $inventory.architecture -ne 'x64' -or $lock.bundleRevision -ne $inventory.bundleRevision -or
        $lock.inventorySha256 -notmatch '^[a-f0-9]{64}$' -or $lock.inventorySha256 -cne $inventory.inventorySha256 -or
        $lock.repository -notmatch '^https://repo\.msys2\.org/' -or $lock.repository -cne $inventory.repository) {
        throw 'The committed runtime lock and reviewed file inventory are inconsistent.'
    }
    Assert-JsonEqual $lock.roots $inventory.roots 'Runtime roots'
    Assert-JsonEqual $lock.packages $inventory.packages 'Runtime package metadata'
    Assert-JsonEqual $lock.caSource $inventory.caSource 'Runtime CA source metadata'
    if ($lock.caBundleSha256 -cne $inventory.caBundleSha256) { throw 'Runtime CA bundle metadata differs.' }

    $expectedFiles = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $canonicalFiles = @()
    foreach ($file in @($inventory.files)) {
        $path = [string]$file.path
        $sha = [string]$file.sha256
        [long]$size = $file.size
        if (-not $path -or $path.Contains('\') -or $path.StartsWith('/') -or $path.EndsWith('/') -or
            $path -match '(^|/)\.\.?(?:/|$)' -or $sha -notmatch '^[a-f0-9]{64}$' -or $size -lt 0 -or
            $expectedFiles.ContainsKey($path)) {
            throw "The reviewed runtime contains an invalid or duplicate path '$path'."
        }
        $expectedFiles.Add($path, [pscustomobject]@{ Path=$path; Size=$size; Sha256=$sha })
        $canonicalFiles += [ordered]@{ path=$path; size=$size; sha256=$sha }
    }
    if ($expectedFiles.Count -lt 1) { throw 'The reviewed runtime inventory is empty.' }
    $inventoryJson = ConvertTo-Json -InputObject $canonicalFiles -Depth 4 -Compress
    $calculatedInventory = Get-BytesSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($inventoryJson))
    if ($calculatedInventory -cne $lock.inventorySha256) {
        throw "The reviewed runtime inventory digest $calculatedInventory differs from the lock."
    }
    [pscustomobject]@{
        Lock = $lock
        Inventory = $inventory
        InventoryPath = $inventoryPath
        InventoryLength = (Get-Item -LiteralPath $inventoryPath).Length
        InventoryFileSha256 = Get-FileSha256 -LiteralPath $inventoryPath
        Files = $expectedFiles
    }
}

function ConvertFrom-AppxEntryName {
    param([Parameter(Mandatory)][string]$Name)
    try { $decoded = [Uri]::UnescapeDataString($Name) }
    catch { throw "Package entry '$Name' has invalid escaping." }
    if (-not $decoded -or $decoded.Contains('\') -or $decoded.StartsWith('/') -or
        $decoded -match '(^|/)\.\.?(?:/|$)') { throw "Package entry '$Name' has an unsafe path." }
    return $decoded
}

function Get-PackageContentMap {
    param([Parameter(Mandatory)][string]$MsixPath)
    $resolved = (Resolve-Path -LiteralPath $MsixPath).Path
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $logicalName = ConvertFrom-AppxEntryName $entry.FullName
            if ($map.ContainsKey($logicalName)) { throw "Package '$resolved' contains duplicate logical entry '$logicalName'." }
            $stream = $entry.Open()
            try { $sha = Get-StreamSha256 -Stream $stream } finally { $stream.Dispose() }
            $map.Add($logicalName, [pscustomobject]@{
                Name=$logicalName; Length=[long]$entry.Length; Sha256=$sha
            })
        }
        return $map
    }
    finally { $archive.Dispose() }
}

function Compare-LftpPilotSignedPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$UnsignedMsix,
        [Parameter(Mandatory)][string]$SignedMsix
    )
    $unsignedPath = (Resolve-Path -LiteralPath $UnsignedMsix).Path
    $signedPath = (Resolve-Path -LiteralPath $SignedMsix).Path
    if ($unsignedPath -eq $signedPath) { throw 'Unsigned and signed package paths must be different files.' }
    $unsigned = Get-PackageContentMap $unsignedPath
    $signed = Get-PackageContentMap $signedPath
    if ($unsigned.ContainsKey('AppxSignature.p7x')) { throw 'The attested input already contains AppxSignature.p7x.' }
    if (-not $signed.ContainsKey('AppxSignature.p7x') -or $signed['AppxSignature.p7x'].Name -cne 'AppxSignature.p7x' -or
        $signed['AppxSignature.p7x'].Length -le 0) { throw 'The signed package lacks a non-empty canonical AppxSignature.p7x entry.' }
    if ($signed.Count -ne ($unsigned.Count + 1)) {
        throw "The signed package entry count $($signed.Count) is not exactly the unsigned count $($unsigned.Count) plus its signature."
    }
    foreach ($name in $unsigned.Keys) {
        if (-not $signed.ContainsKey($name)) { throw "Signing removed package payload '$name'." }
        $before = $unsigned[$name]; $after = $signed[$name]
        if ($before.Name -cne $after.Name -or $before.Length -ne $after.Length -or $before.Sha256 -cne $after.Sha256) {
            throw "Signing changed package payload '$name'."
        }
    }
    foreach ($name in $signed.Keys) {
        if ($name -cne 'AppxSignature.p7x' -and -not $unsigned.ContainsKey($name)) {
            throw "Signing added unexpected package payload '$name'."
        }
    }
    return [pscustomobject]@{
        UnsignedPath=$unsignedPath
        SignedPath=$signedPath
        UnsignedSha256=Get-FileSha256 $unsignedPath
        SignedSha256=Get-FileSha256 $signedPath
        PayloadEntryCount=$unsigned.Count
        SignatureSha256=$signed['AppxSignature.p7x'].Sha256
    }
}

function Assert-PackageManifest {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry, [string]$ExpectedVersion)
    if ($Entry.Length -gt 262144) { throw 'AppxManifest.xml is unreasonably large.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1048576
    $stream = $Entry.Open()
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    $manifest = [Xml.XmlDocument]::new(); $manifest.XmlResolver = $null
    try { $manifest.Load($reader) } finally { $reader.Dispose(); $stream.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identityNodes = @($manifest.SelectNodes('/f:Package/f:Identity', $ns))
    $applicationNodes = @($manifest.SelectNodes('/f:Package/f:Applications/f:Application', $ns))
    if ($identityNodes.Count -ne 1 -or $applicationNodes.Count -ne 1) { throw 'Package identity/application metadata is ambiguous.' }
    $identity = $identityNodes[0]
    $version = (Assert-MsixVersion -Version $identity.GetAttribute('Version')).ToString(4)
    if ($identity.GetAttribute('Name') -ne 'LFTPPilot.Desktop' -or $identity.GetAttribute('Publisher') -ne 'CN=LFTPPilot.Dev' -or
        $identity.GetAttribute('ProcessorArchitecture') -ne 'x64' -or ($ExpectedVersion -and $version -ne (Assert-MsixVersion $ExpectedVersion).ToString(4))) {
        throw 'The package identity, publisher, version, or architecture is invalid.'
    }
    $properties = $manifest.SelectSingleNode('/f:Package/f:Properties', $ns)
    $family = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $ns)
    $application = $applicationNodes[0]
    $propertyNames = @($manifest.SelectNodes('/f:Package/f:Properties/*', $ns) | ForEach-Object LocalName | Sort-Object)
    $dependencyNodes = @($manifest.SelectNodes('/f:Package/f:Dependencies/*', $ns))
    if ($null -eq $properties -or $properties.DisplayName -ne 'LFTP Pilot' -or
        $properties.PublisherDisplayName -ne 'LFTP Pilot contributors' -or $properties.Logo -ne 'Assets\StoreLogo.png' -or
        ($propertyNames -join ',') -cne 'DisplayName,Logo,PublisherDisplayName' -or $dependencyNodes.Count -ne 1 -or
        $null -eq $family -or $family.GetAttribute('Name') -ne 'Windows.Desktop' -or
        $family.GetAttribute('MinVersion') -ne '10.0.22000.0' -or $family.GetAttribute('MaxVersionTested') -ne '10.0.26100.0' -or
        $application.GetAttribute('Id') -ne 'App' -or $application.GetAttribute('Executable') -ne 'LFTPPilot.exe' -or
        $application.GetAttribute('EntryPoint') -ne 'Windows.FullTrustApplication') {
        throw 'The package application metadata is invalid.'
    }
    $protocols = @($manifest.SelectNodes("//*[local-name()='Protocol']", $ns))
    $extensions = @($manifest.SelectNodes("//*[local-name()='Extension']", $ns))
    if ($protocols.Count -ne 1 -or $protocols[0].GetAttribute('Name') -ne 'lftp-pilot' -or
        $extensions.Count -ne 1 -or $extensions[0].GetAttribute('Category') -ne 'windows.protocol') {
        throw 'The package protocol/extension metadata is invalid.'
    }
    $capabilities = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']/*[local-name()='Capability']") |
        ForEach-Object { [string]$_.GetAttribute('Name') } | Sort-Object)
    if (($capabilities -join ',') -cne 'internetClient,runFullTrust') { throw 'The package capability set is invalid.' }
    return $version
}

function Test-LftpPilotMsix {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$MsixPath,
        [string]$ExpectedVersion,
        [Parameter(Mandatory)][ValidateSet('Unsigned','Signed')][string]$SignatureMode,
        [string]$RuntimeLockPath,
        [string]$RuntimeInventoryPath
    )
    if (-not $RuntimeLockPath) { $RuntimeLockPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.lock.json' }
    if (-not $RuntimeInventoryPath) { $RuntimeInventoryPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.files.json' }
    $evidence = Get-ReviewedRuntimeEvidence -RuntimeLockPath $RuntimeLockPath -RuntimeInventoryPath $RuntimeInventoryPath
    $resolved = (Resolve-Path -LiteralPath $MsixPath).Path
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $logicalName = ConvertFrom-AppxEntryName $entry.FullName
            if ($entries.ContainsKey($logicalName)) { throw "Package contains duplicate logical entry '$logicalName'." }
            $entries.Add($logicalName, $entry)
        }
        $hasSignature = $entries.ContainsKey('AppxSignature.p7x')
        if (($SignatureMode -eq 'Unsigned' -and $hasSignature) -or ($SignatureMode -eq 'Signed' -and -not $hasSignature)) {
            throw "The package does not satisfy the required $SignatureMode signature state."
        }
        if (-not $entries.ContainsKey('AppxManifest.xml')) { throw 'AppxManifest.xml is missing.' }
        $version = Assert-PackageManifest -Entry $entries['AppxManifest.xml'] -ExpectedVersion $ExpectedVersion
        foreach ($required in @('LFTPPilot.exe', 'agent/LFTPPilot.Agent.exe', 'agent/LFTPPilot.Engine.dll')) {
            if (-not $entries.ContainsKey($required) -or $entries[$required].Length -le 0) { throw "Required package payload '$required' is missing or empty." }
        }

        $expectedRuntime = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in $evidence.Files.Values) {
            [void]$expectedRuntime.Add("lftp/$($item.Path)", $item)
        }
        [void]$expectedRuntime.Add('lftp/bundle-manifest.json', [pscustomobject]@{
            Path='bundle-manifest.json'; Size=$evidence.InventoryLength; Sha256=$evidence.InventoryFileSha256
        })
        $revisionBytes = [Text.UTF8Encoding]::new($false).GetBytes("$($evidence.Lock.bundleRevision)`n")
        [void]$expectedRuntime.Add('lftp/.bundle-rev', [pscustomobject]@{
            Path='.bundle-rev'; Size=[long]$revisionBytes.Length; Sha256=(Get-BytesSha256 $revisionBytes)
        })

        $actualRuntime = @($entries.Keys | Where-Object { $_.StartsWith('lftp/', [StringComparison]::OrdinalIgnoreCase) })
        if ($actualRuntime.Count -ne $expectedRuntime.Count) {
            throw "Package runtime entry count $($actualRuntime.Count) differs from reviewed count $($expectedRuntime.Count)."
        }
        foreach ($name in $actualRuntime) {
            if (-not $expectedRuntime.ContainsKey($name)) { throw "Unexpected package runtime entry '$name'." }
            $expected = $expectedRuntime[$name]
            $entry = $entries[$name]
            if ($name -cne "lftp/$($expected.Path)" -or $entry.Length -ne [long]$expected.Size) {
                throw "Package runtime entry '$name' differs in path or size from the reviewed inventory."
            }
            $stream = $entry.Open()
            try { $actualSha = Get-StreamSha256 -Stream $stream } finally { $stream.Dispose() }
            if ($actualSha -cne [string]$expected.Sha256) { throw "Package runtime entry '$name' differs from its reviewed SHA-256." }
        }
        foreach ($name in $expectedRuntime.Keys) {
            if (-not $entries.ContainsKey($name)) { throw "Reviewed package runtime entry '$name' is missing." }
        }
        return [pscustomobject]@{ Path=$resolved; Version=$version; RuntimeFileCount=$expectedRuntime.Count }
    }
    finally { $archive.Dispose() }
}

Export-ModuleMember -Function Get-ReviewedRuntimeEvidence, Test-LftpPilotMsix, Compare-LftpPilotSignedPayload
