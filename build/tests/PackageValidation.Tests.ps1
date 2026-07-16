Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$buildRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $buildRoot 'PackageValidation.psm1') -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256([byte[]]$Bytes) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
function Add-ZipBytes($Archive, [string]$Name, [byte[]]$Bytes) {
    $entry = $Archive.CreateEntry($Name)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
}
function Assert-Throws([scriptblock]$Action, [string]$Label) {
    try { & $Action; throw "$Label did not throw." } catch { if ($_.Exception.Message -eq "$Label did not throw.") { throw } }
}

function New-SignedFixture([string]$Unsigned, [string]$Signed, [string]$MutateEntry, [string]$RemoveEntry, [string]$ExtraEntry) {
    $source = [IO.Compression.ZipFile]::OpenRead($Unsigned)
    $target = [IO.Compression.ZipFile]::Open($Signed, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $source.Entries) {
            if ($entry.FullName -ceq $RemoveEntry) { continue }
            $input = $entry.Open(); $memory = [IO.MemoryStream]::new()
            try { $input.CopyTo($memory); $bytes = $memory.ToArray() }
            finally { $memory.Dispose(); $input.Dispose() }
            if ($entry.FullName -ceq $MutateEntry) { $bytes = [byte[]](9,8,7,6) }
            Add-ZipBytes $target $entry.FullName $bytes
        }
        Add-ZipBytes $target 'AppxSignature.p7x' ([byte[]](5,4,3,2,1))
        if ($ExtraEntry) { Add-ZipBytes $target $ExtraEntry ([byte[]](1)) }
    }
    finally { $target.Dispose(); $source.Dispose() }
}

function New-Fixture([string]$Root, [string]$Name, [switch]$MutateRuntime, [switch]$ExtraRuntime,
    [switch]$MutateEmbeddedManifest, [switch]$MutatePackageMetadata) {
    $directory = Join-Path $Root $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $lftp = [Text.Encoding]::UTF8.GetBytes('lftp-fixture')
    $ssh = [Text.Encoding]::UTF8.GetBytes('ssh-fixture')
    $files = @(
        [ordered]@{ path='usr/bin/lftp.exe'; size=[long]$lftp.Length; sha256=Get-Sha256 $lftp },
        [ordered]@{ path='usr/bin/ssh.exe'; size=[long]$ssh.Length; sha256=Get-Sha256 $ssh }
    )
    $canonical = ConvertTo-Json -InputObject $files -Depth 4 -Compress
    $inventorySha = Get-Sha256 ([Text.Encoding]::UTF8.GetBytes($canonical))
    $package = [ordered]@{
        name='fixture'; version='1.0-1'; filename='fixture-1.0-1-x86_64.pkg.tar.zst'; sha256=('1' * 64)
        dependencies=@(); signature=[ordered]@{ sha256=('2' * 64); signerFingerprint=('A' * 40); signingKeyId=('B' * 16) }
    }
    $common = [ordered]@{
        schema=3; repository='https://repo.msys2.org/msys/x86_64/'; architecture='x64'; bundleRevision='8'
        inventorySha256=$inventorySha; roots=@('fixture'); packages=@($package); caBundleSha256=('3' * 64)
        caSource=[ordered]@{ type='fixture'; package='fixture'; filename='fixture' }
    }
    $lockPath = Join-Path $directory 'runtime.lock.json'
    Write-Utf8 $lockPath (($common | ConvertTo-Json -Depth 20) + "`n")
    $inventory = [ordered]@{}
    foreach ($property in $common.GetEnumerator()) { $inventory[$property.Key] = $property.Value }
    $inventory.generatedAt='2026-01-01T00:00:00Z'; $inventory.source='fixture'; $inventory.files=$files
    $inventoryPath = Join-Path $directory 'runtime.files.json'
    Write-Utf8 $inventoryPath (($inventory | ConvertTo-Json -Depth 20) + "`n")

    $protocol = if ($MutatePackageMetadata) { 'evil-command' } else { 'lftp-pilot' }
    $manifest = @"
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"><Identity Name="LFTPPilot.Desktop" Publisher="CN=LFTPPilot.Dev" Version="1.0.0.0" ProcessorArchitecture="x64"/><Properties><DisplayName>LFTP Pilot</DisplayName><PublisherDisplayName>LFTP Pilot contributors</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties><Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0"/></Dependencies><Applications><Application Id="App" Executable="LFTPPilot.exe" EntryPoint="Windows.FullTrustApplication"><Extensions><uap:Extension Category="windows.protocol"><uap:Protocol Name="$protocol"/></uap:Extension></Extensions></Application></Applications><Capabilities><rescap:Capability Name="runFullTrust"/><Capability Name="internetClient"/></Capabilities></Package>
"@
    $msix = Join-Path $directory 'fixture.msix'
    $archive = [IO.Compression.ZipFile]::Open($msix, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-ZipBytes $archive 'AppxManifest.xml' ([Text.Encoding]::UTF8.GetBytes($manifest))
        foreach ($required in @('LFTPPilot.exe','agent/LFTPPilot.Agent.exe','agent/LFTPPilot.Engine.dll')) {
            Add-ZipBytes $archive $required ([byte[]](1,2,3))
        }
        $lftpPayload = if ($MutateRuntime) { [Text.Encoding]::UTF8.GetBytes('lftp-fixturE') } else { $lftp }
        Add-ZipBytes $archive 'lftp/usr/bin/lftp.exe' $lftpPayload
        Add-ZipBytes $archive 'lftp/usr/bin/ssh.exe' $ssh
        $embedded = if ($MutateEmbeddedManifest) { [Text.Encoding]::UTF8.GetBytes('{}') } else { [IO.File]::ReadAllBytes($inventoryPath) }
        Add-ZipBytes $archive 'lftp/bundle-manifest.json' $embedded
        Add-ZipBytes $archive 'lftp/.bundle-rev' ([Text.Encoding]::UTF8.GetBytes("8`n"))
        if ($ExtraRuntime) { Add-ZipBytes $archive 'lftp/usr/bin/evil.exe' ([byte[]](9)) }
    }
    finally { $archive.Dispose() }
    [pscustomobject]@{ Msix=$msix; Lock=$lockPath; Inventory=$inventoryPath }
}

$root = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-package-tests-$([Guid]::NewGuid().ToString('N'))"
try {
    $good = New-Fixture $root 'good'
    Test-LftpPilotMsix -MsixPath $good.Msix -ExpectedVersion '1.0.0.0' -SignatureMode Unsigned `
        -RuntimeLockPath $good.Lock -RuntimeInventoryPath $good.Inventory | Out-Null
    $hashTamper = New-Fixture $root 'hash-tamper' -MutateRuntime
    Assert-Throws { Test-LftpPilotMsix $hashTamper.Msix '1.0.0.0' Unsigned $hashTamper.Lock $hashTamper.Inventory } 'Runtime hash tamper'
    $extra = New-Fixture $root 'extra-runtime' -ExtraRuntime
    Assert-Throws { Test-LftpPilotMsix $extra.Msix '1.0.0.0' Unsigned $extra.Lock $extra.Inventory } 'Unexpected runtime entry'
    $manifestTamper = New-Fixture $root 'manifest-tamper' -MutateEmbeddedManifest
    Assert-Throws { Test-LftpPilotMsix $manifestTamper.Msix '1.0.0.0' Unsigned $manifestTamper.Lock $manifestTamper.Inventory } 'Embedded manifest tamper'
    $metadataTamper = New-Fixture $root 'metadata-tamper' -MutatePackageMetadata
    Assert-Throws { Test-LftpPilotMsix $metadataTamper.Msix '1.0.0.0' Unsigned $metadataTamper.Lock $metadataTamper.Inventory } 'Package metadata tamper'

    $signed = Join-Path $root 'signed.msix'
    New-SignedFixture $good.Msix $signed
    $comparison = Compare-LftpPilotSignedPayload -UnsignedMsix $good.Msix -SignedMsix $signed
    if ($comparison.PayloadEntryCount -lt 1 -or $comparison.UnsignedSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Signed/unsigned comparison did not return complete package binding evidence.'
    }
    foreach ($payload in @('LFTPPilot.exe','agent/LFTPPilot.Agent.exe','agent/LFTPPilot.Engine.dll')) {
        $mutatedSigned = Join-Path $root "signed-mutated-$([IO.Path]::GetFileName($payload)).msix"
        New-SignedFixture $good.Msix $mutatedSigned $payload
        Assert-Throws { Compare-LftpPilotSignedPayload $good.Msix $mutatedSigned } "Changed payload $payload"
    }
    $extraSigned = Join-Path $root 'signed-extra.msix'
    New-SignedFixture $good.Msix $extraSigned $null $null 'unexpected-root.dll'
    Assert-Throws { Compare-LftpPilotSignedPayload $good.Msix $extraSigned } 'Added payload'
    $removedSigned = Join-Path $root 'signed-removed.msix'
    New-SignedFixture $good.Msix $removedSigned $null 'agent/LFTPPilot.Engine.dll'
    Assert-Throws { Compare-LftpPilotSignedPayload $good.Msix $removedSigned } 'Removed payload'
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
'Package attestation tamper tests passed.'
