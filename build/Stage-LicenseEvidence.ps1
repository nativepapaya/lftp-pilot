[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeLockPath,
    [Parameter(Mandatory)][string]$LicenseManifestPath,
    [Parameter(Mandatory)][string[]]$NuGetLockPath,
    [Parameter(Mandatory)][string[]]$ProjectAssetsPath,
    [string]$RuntimePackLockPath = (Join-Path $PSScriptRoot 'runtime-lock\dotnet-runtime-packs.lock.json'),
    [string]$GlobalJsonPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'global.json'),
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
& (Join-Path $PSScriptRoot 'Test-LicenseEvidence.ps1') -RuntimeLockPath $RuntimeLockPath -LicenseManifestPath $LicenseManifestPath `
    -NuGetLockPath $NuGetLockPath -ProjectAssetsPath $ProjectAssetsPath -RuntimePackLockPath $RuntimePackLockPath `
    -GlobalJsonPath $GlobalJsonPath | Out-Null
$manifest = (Resolve-Path -LiteralPath $LicenseManifestPath).Path
$root = Split-Path $manifest -Parent
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($output) -ine '.zip') { throw 'License evidence output must be a ZIP archive.' }
$stage = Join-Path ([IO.Path]::GetDirectoryName($output)) ".license-evidence-$([Guid]::NewGuid().ToString('N'))"
$temporary = "$output.$([Guid]::NewGuid().ToString('N')).tmp.zip"
try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $evidence = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$paths.Add('licenses-manifest.json')
    if (Test-Path -LiteralPath (Join-Path $root 'README.md')) { [void]$paths.Add('README.md') }
    foreach ($package in $evidence.packages) {
        foreach ($license in $package.licenseFiles) { [void]$paths.Add([string]$license.path) }
        [void]$paths.Add([string]$package.correspondingSource.path)
    }
    foreach ($package in $evidence.managedPackages) {
        foreach ($license in $package.licenseFiles) { [void]$paths.Add([string]$license.path) }
        [void]$paths.Add([string]$package.distributionArchive.path)
        if ($package.reviewedObligations.sourceCodeRequired) { [void]$paths.Add([string]$package.correspondingSource.path) }
    }
    foreach ($relative in $paths) {
        if (-not $relative -or $relative.Contains('\') -or [IO.Path]::IsPathRooted($relative) -or
            $relative -match '(^|/)\.\.?(?:/|$)') { throw "Unsafe staged evidence path '$relative'." }
        $source = if ($relative -eq 'licenses-manifest.json') { $manifest } else {
            [IO.Path]::GetFullPath((Join-Path $root $relative.Replace('/', '\')))
        }
        if (-not $source.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Staged evidence path '$relative' escaped its root or disappeared." }
        $destination = Join-Path $stage $relative.Replace('/', '\')
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    $outputStream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create, $true, [Text.Encoding]::UTF8)
        try {
            foreach ($relative in ($paths | Sort-Object)) {
                $entry = $archive.CreateEntry($relative.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
                $sourceStream = [IO.File]::OpenRead((Join-Path $stage $relative.Replace('/', '\')))
                $entryStream = $entry.Open()
                try { $sourceStream.CopyTo($entryStream) }
                finally { $entryStream.Dispose(); $sourceStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
        $outputStream.Flush($true)
    }
    finally { $outputStream.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $output -Force
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Get-Item -LiteralPath $output
