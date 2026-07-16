[CmdletBinding()]
param(
    [string]$LockPath,
    [string]$InventoryPath,
    [string]$TrustedKeyringPath,
    [switch]$UseAuthenticatedLockEvidence,
    [Parameter(Mandatory)][string]$ArchiveDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $LockPath) { $LockPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.lock.json' }
if (-not $InventoryPath) { $InventoryPath = Join-Path $PSScriptRoot 'runtime-lock\lftp-msys2-x64.files.json' }
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$reference = Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json
if ($lock.schema -ne 3 -or $lock.architecture -ne 'x64' -or $lock.repository -notmatch '^https://repo\.msys2\.org/') {
    throw 'The LFTP runtime lock identity is invalid.'
}
if ($reference.schema -ne 3 -or $reference.architecture -ne 'x64' -or
    $reference.bundleRevision -ne $lock.bundleRevision -or $reference.inventorySha256 -ne $lock.inventorySha256 -or
    @($reference.files).Count -lt 1 -or @($reference.packages).Count -ne @($lock.packages).Count) {
    throw 'The reviewed runtime file inventory does not match the package lock.'
}
foreach ($referencePackage in $reference.packages) {
    $lockedPackage = @($lock.packages | Where-Object name -eq $referencePackage.name)
    if ($lockedPackage.Count -ne 1 -or $lockedPackage[0].version -ne $referencePackage.version -or
        $lockedPackage[0].filename -ne $referencePackage.filename -or $lockedPackage[0].sha256 -ne $referencePackage.sha256) {
        throw "The reviewed inventory package '$($referencePackage.name)' differs from the lock."
    }
}
$gpgv = $null; $keyring = $null
if (-not $UseAuthenticatedLockEvidence) {
    if (-not $TrustedKeyringPath) { throw 'Provide a reviewed keyring or explicitly use authenticated lock evidence.' }
    $gpgv = Get-Command gpgv.exe -ErrorAction SilentlyContinue
    if ($null -eq $gpgv) { $gpgv = Get-Command gpgv -ErrorAction SilentlyContinue }
    if ($null -eq $gpgv) { throw 'gpgv is required to authenticate MSYS2 package signatures.' }
    $keyring = (Resolve-Path -LiteralPath $TrustedKeyringPath).Path
}
[IO.Directory]::CreateDirectory($ArchiveDirectory) | Out-Null
$baseUri = [Uri]$lock.repository

foreach ($package in $lock.packages) {
    if ($package.filename -notmatch '^[A-Za-z0-9+_.-]+\.pkg\.tar\.zst$' -or $package.sha256 -notmatch '^[a-f0-9]{64}$' -or
        $package.signature.sha256 -notmatch '^[a-f0-9]{64}$' -or $package.signature.signerFingerprint -notmatch '^[A-F0-9]{40}$') {
        throw "Package lock entry '$($package.name)' is malformed."
    }
    $archive = Join-Path $ArchiveDirectory $package.filename
    $signature = "$archive.sig"
    foreach ($download in @(
        @{ Path = $archive; Uri = [Uri]::new($baseUri, $package.filename); Sha = [string]$package.sha256 },
        @{ Path = $signature; Uri = [Uri]::new($baseUri, "$($package.filename).sig"); Sha = [string]$package.signature.sha256 }
    )) {
        if (-not (Test-Path -LiteralPath $download.Path) -or (Get-FileSha256 $download.Path) -ne $download.Sha) {
            $temporary = "$($download.Path).$([Guid]::NewGuid().ToString('N')).tmp"
            try {
                Invoke-WebRequest -Uri $download.Uri -OutFile $temporary -UseBasicParsing
                if ((Get-FileSha256 $temporary) -ne $download.Sha) { throw "SHA-256 mismatch for $($download.Uri)." }
                Move-Item -LiteralPath $temporary -Destination $download.Path -Force
            }
            finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        }
    }
    if (-not $UseAuthenticatedLockEvidence) {
        $status = & $gpgv.Source --status-fd 1 --keyring $keyring $signature $archive 2>&1
        if ($LASTEXITCODE -ne 0) { throw "gpgv rejected $($package.filename): $($status -join ' ')" }
        $fingerprints = @($status | Select-String '^\[GNUPG:\] VALIDSIG ([A-F0-9]{40}) ' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        if ($fingerprints.Count -ne 1 -or $fingerprints[0] -ne $package.signature.signerFingerprint) {
            throw "Authenticated signer for $($package.filename) differs from the lock."
        }
    }
}

$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$parent = Split-Path $output -Parent
$stage = Join-Path $parent ".lftp-runtime-$([Guid]::NewGuid().ToString('N'))"
$raw = "$stage.raw"
try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    [IO.Directory]::CreateDirectory($raw) | Out-Null
    foreach ($package in $lock.packages) {
        $archive = Join-Path $ArchiveDirectory $package.filename
        $members = & tar.exe -tf $archive
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect $($package.filename)." }
        foreach ($member in $members) {
            $normalized = ([string]$member).Replace('\', '/')
            if ($normalized.StartsWith('/') -or $normalized -match '(^|/)\.\.(/|$)') { throw "Unsafe archive member '$member'." }
        }
        & tar.exe -xf $archive -C $raw
        if ($LASTEXITCODE -ne 0) { throw "Unable to extract $($package.filename)." }
    }

    foreach ($item in $reference.files) {
        $relative = [string]$item.path
        if (-not $relative -or $relative.StartsWith('/') -or $relative -match '(^|/)\.\.(/|$)' -or $item.sha256 -notmatch '^[a-f0-9]{64}$') {
            throw "Invalid reviewed runtime path '$relative'."
        }
        $source = Join-Path $raw $relative.Replace('/', '\')
        if ($relative -eq 'etc/fstab') {
            [IO.Directory]::CreateDirectory((Split-Path $source -Parent)) | Out-Null
            [IO.File]::WriteAllText($source, "none / cygdrive binary,posix=0,noacl 0 0`n", [Text.UTF8Encoding]::new($false))
        }
        elseif ($relative -eq 'etc/nsswitch.conf') {
            [IO.Directory]::CreateDirectory((Split-Path $source -Parent)) | Out-Null
            [IO.File]::WriteAllText($source, "passwd: files db`ngroup: files db`ndb_home: env windows`n", [Text.UTF8Encoding]::new($false))
        }
        elseif ($relative -eq 'tmp/.keep') {
            [IO.Directory]::CreateDirectory((Split-Path $source -Parent)) | Out-Null
            [IO.File]::WriteAllBytes($source, [byte[]]@())
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-FileSha256 $source) -ne $item.sha256) {
            if ($relative -ne 'usr/ssl/certs/ca-bundle.crt') { throw "Extracted runtime file '$relative' differs from the reviewed inventory." }
            $source = Get-ChildItem -LiteralPath $raw -Recurse -File | Where-Object Length -eq ([long]$item.size) |
                Where-Object { (Get-FileSha256 $_.FullName) -eq $item.sha256 } | Select-Object -First 1 -ExpandProperty FullName
            if (-not $source) { throw 'The locked CA bundle was not found in the authenticated package set.' }
        }
        $destination = Join-Path $stage $relative.Replace('/', '\')
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
        $actual = Get-Item -LiteralPath $destination
        if ($actual.Length -ne [long]$item.size -or (Get-FileSha256 $destination) -ne $item.sha256) {
            throw "Curated runtime file '$relative' failed final verification."
        }
    }
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); size = $_.Length; sha256 = Get-FileSha256 $_.FullName }
    })
    $inventoryJson = ConvertTo-Json -InputObject $files -Depth 4 -Compress
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $inventoryHash = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($inventoryJson)))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ($files.Count -ne @($reference.files).Count -or $inventoryHash -ne $lock.inventorySha256) {
        throw "Curated runtime inventory $inventoryHash differs from the authenticated lock $($lock.inventorySha256)."
    }
    Copy-Item -LiteralPath $InventoryPath -Destination (Join-Path $stage 'bundle-manifest.json')
    [IO.File]::WriteAllText((Join-Path $stage '.bundle-rev'), "$($lock.bundleRevision)`n", [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $output) { throw "Refusing to replace existing runtime '$output'; remove it explicitly after verification." }
    Move-Item -LiteralPath $stage -Destination $output
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $raw) { Remove-Item -LiteralPath $raw -Recurse -Force }
}
Get-Item -LiteralPath $output
