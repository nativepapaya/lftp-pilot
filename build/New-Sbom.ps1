[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$RuntimeLockPath,
    [Parameter(Mandatory)][string[]]$NuGetLockPath,
    [Parameter(Mandatory)][string[]]$ProjectAssetsPath,
    [string]$RuntimePackLockPath = (Join-Path $PSScriptRoot 'runtime-lock\dotnet-runtime-packs.lock.json'),
    [Parameter(Mandatory)][string[]]$ArtifactPath,
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DependencyEvidence.psm1') -Force
[void](Assert-MsixVersion -Version $Version)
$lock = Get-Content -LiteralPath $RuntimeLockPath -Raw | ConvertFrom-Json
$runtimeComponents = @(
    foreach ($package in $lock.packages) {
        [ordered]@{
            type = 'library'; name = [string]$package.name; version = [string]$package.version
            purl = "pkg:msys2/$([Uri]::EscapeDataString([string]$package.name))@$([Uri]::EscapeDataString([string]$package.version))?arch=x86_64"
            hashes = @([ordered]@{ alg = 'SHA-256'; content = [string]$package.sha256 })
        }
    }
)
$resolved = @(Get-ProductionNuGetEvidence -NuGetLockPath $NuGetLockPath)
$nugetComponents = @(
    foreach ($item in $resolved) {
        $category = if ($item.Id -like 'Microsoft.Windows*') { 'Windows/WinUI' }
            elseif ($item.Id -like 'Microsoft.NET*' -or $item.Id -like 'System.*' -or $item.Id -like 'runtime.*') { '.NET' }
            else { 'NuGet' }
        [ordered]@{
            type='library'; name=$item.Id; version=$item.Version
            purl="pkg:nuget/$([Uri]::EscapeDataString($item.Id))@$([Uri]::EscapeDataString($item.Version))"
            hashes=@([ordered]@{ alg='SHA-512'; content=$item.Sha512 })
            properties=@(
                [ordered]@{ name='lftp-pilot:category'; value=$category },
                [ordered]@{ name='lftp-pilot:projects'; value=(($item.Projects | Sort-Object) -join ',') },
                [ordered]@{ name='lftp-pilot:targets'; value=(($item.Targets | Sort-Object) -join ',') },
                [ordered]@{ name='lftp-pilot:dependency-types'; value=(($item.Types | Sort-Object) -join ',') }
            )
        }
    }
)
$selfContainedPacks = @(Get-SelfContainedRuntimePackEvidence -ProjectAssetsPath $ProjectAssetsPath `
    -RuntimePackLockPath $RuntimePackLockPath)
$dotnetRuntimeComponents = @(
    foreach ($item in $selfContainedPacks) {
        [ordered]@{
            type='framework'; name=$item.Id; version=$item.Version
            purl="pkg:nuget/$([Uri]::EscapeDataString($item.Id))@$([Uri]::EscapeDataString($item.Version))"
            hashes=@([ordered]@{ alg='SHA-512'; content=$item.Sha512 })
            properties=@(
                [ordered]@{ name='lftp-pilot:category'; value='.NET self-contained runtime pack' },
                [ordered]@{ name='lftp-pilot:framework-reference'; value=$item.FrameworkReference },
                [ordered]@{ name='lftp-pilot:projects'; value=(($item.Projects | Sort-Object) -join ',') },
                [ordered]@{ name='lftp-pilot:targets'; value=(($item.Targets | Sort-Object) -join ',') }
            )
        }
    }
)
$artifacts = @(
    foreach ($path in $ArtifactPath) {
        $file = Get-Item -LiteralPath $path
        [ordered]@{ type = 'file'; name = $file.Name; version = $Version; hashes = @([ordered]@{ alg = 'SHA-256'; content = Get-FileSha256 $file.FullName }) }
    }
)
$bom = [ordered]@{
    bomFormat = 'CycloneDX'; specVersion = '1.5'; serialNumber = "urn:uuid:$([Guid]::NewGuid())"; version = 1
    metadata = [ordered]@{ timestamp = [DateTimeOffset]::UtcNow.ToString('o'); component = [ordered]@{ type = 'application'; name = 'LFTP Pilot'; version = $Version } }
    components = @($runtimeComponents) + @($nugetComponents) + @($dotnetRuntimeComponents) + @($artifacts)
}
$json = $bom | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $OutputPath
