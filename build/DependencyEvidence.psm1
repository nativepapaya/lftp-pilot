Set-StrictMode -Version Latest

function ConvertFrom-NuGetSha512 {
    param([Parameter(Mandatory)][string]$ContentHash, [Parameter(Mandatory)][string]$Label)
    try { $bytes = [Convert]::FromBase64String($ContentHash.Trim()) }
    catch { throw "$Label has an invalid base64 NuGet content hash." }
    if ($bytes.Length -ne 64) { throw "$Label does not have a SHA-512 NuGet content hash." }
    return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Get-ProductionNuGetEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$NuGetLockPath)
    $resolved = @{}
    foreach ($lockPath in $NuGetLockPath) {
        $file = Get-Item -LiteralPath $lockPath
        if ($file.FullName -match '\\tests\\' -or $file.FullName -notmatch '\\src\\') {
            throw "Only source-project lock files are allowed in production evidence: $($file.FullName)"
        }
        $lock = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($lock.version -ne 1 -or $null -eq $lock.dependencies) { throw "Invalid NuGet lock file: $($file.FullName)" }
        $project = $file.Directory.Name
        foreach ($target in $lock.dependencies.PSObject.Properties) {
            foreach ($dependency in $target.Value.PSObject.Properties) {
                $value = $dependency.Value
                if ($value.type -eq 'Project') { continue }
                $id = [string]$dependency.Name; $version = [string]$value.resolved
                $hash = ConvertFrom-NuGetSha512 ([string]$value.contentHash) "$id in $($file.FullName)"
                $key = "$($id.ToLowerInvariant())@$version"
                if (-not $resolved.ContainsKey($key)) {
                    $resolved[$key] = [pscustomobject]@{
                        Id=$id; Version=$version; Sha512=$hash; ContentHash=[string]$value.contentHash
                        Projects=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                        Targets=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                        Types=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                    }
                }
                $item = $resolved[$key]
                if ($item.Sha512 -cne $hash) { throw "Conflicting locked SHA-512 hashes exist for $id $version." }
                [void]$item.Projects.Add($project); [void]$item.Targets.Add($target.Name); [void]$item.Types.Add([string]$value.type)
            }
        }
    }
    return @($resolved.Values | Sort-Object Id,Version)
}

function Get-SelfContainedRuntimePackEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$ProjectAssetsPath,
        [Parameter(Mandatory)][string]$RuntimePackLockPath,
        [string]$GlobalJsonPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'global.json')
    )
    $runtimeLock = Get-Content -LiteralPath $RuntimePackLockPath -Raw | ConvertFrom-Json
    $global = Get-Content -LiteralPath $GlobalJsonPath -Raw | ConvertFrom-Json
    if ($runtimeLock.schema -ne 1 -or $runtimeLock.runtimeIdentifier -cne 'win-x64' -or
        $runtimeLock.sdkVersion -cne [string]$global.sdk.version -or @($runtimeLock.packs).Count -lt 1) {
        throw 'The .NET self-contained runtime-pack lock is invalid or does not match global.json.'
    }
    $frameworkToPack = @{
        'Microsoft.NETCore.App'='Microsoft.NETCore.App.Runtime.win-x64'
        'Microsoft.AspNetCore.App'='Microsoft.AspNetCore.App.Runtime.win-x64'
        'Microsoft.WindowsDesktop.App'='Microsoft.WindowsDesktop.App.Runtime.win-x64'
    }
    $selected = @{}; $packageFolders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($assetsPath in $ProjectAssetsPath) {
        $file = Get-Item -LiteralPath $assetsPath
        if ($file.FullName -match '\\tests\\' -or $file.FullName -notmatch '\\src\\' -or $file.Name -cne 'project.assets.json') {
            throw "Only production project.assets.json files are allowed: $($file.FullName)"
        }
        $assets = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($null -eq $assets.project -or $null -eq $assets.project.frameworks) { throw "Invalid project assets: $($file.FullName)" }
        if ($assets.packageFolders) { foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) { [void]$packageFolders.Add($folder) } }
        $projectName = if ($assets.project.restore.projectName) { [string]$assets.project.restore.projectName } else { $file.Directory.Parent.Parent.Name }
        foreach ($target in $assets.project.frameworks.PSObject.Properties) {
            $framework = $target.Value
            if (-not $framework.frameworkReferences) { continue }
            $downloadProperty = $framework.PSObject.Properties['downloadDependencies']
            if ($null -eq $downloadProperty) { continue }
            foreach ($reference in $framework.frameworkReferences.PSObject.Properties.Name) {
                if (-not $frameworkToPack.ContainsKey($reference)) { continue }
                $packName = $frameworkToPack[$reference]
                $dependencies = @($downloadProperty.Value | Where-Object { $_.name -ceq $packName })
                if ($dependencies.Count -eq 0) { continue }
                $versionMatch = if ($dependencies.Count -eq 1) {
                    [regex]::Match([string]$dependencies[0].version, '^\[([^,]+),\s*([^\]]+)\]$')
                } else { $null }
                if ($null -eq $versionMatch -or -not $versionMatch.Success -or $versionMatch.Groups[1].Value -cne $versionMatch.Groups[2].Value) {
                    throw "Project assets do not bind one exact $packName version for $projectName."
                }
                $version = $versionMatch.Groups[1].Value; $key = "$($packName.ToLowerInvariant())@$version"
                if (-not $selected.ContainsKey($key)) {
                    $selected[$key] = [pscustomobject]@{
                        FrameworkReference=$reference; Id=$packName; Version=$version
                        Projects=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                        Targets=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                    }
                }
                [void]$selected[$key].Projects.Add($projectName); [void]$selected[$key].Targets.Add($target.Name)
            }
        }
    }
    if ($selected.Count -ne @($runtimeLock.packs).Count) { throw 'Selected self-contained runtime packs differ from the committed runtime-pack lock.' }
    $results = @()
    foreach ($locked in $runtimeLock.packs) {
        $key = "$(([string]$locked.name).ToLowerInvariant())@$([string]$locked.version)"
        if (-not $selected.ContainsKey($key) -or $selected[$key].FrameworkReference -cne [string]$locked.frameworkReference -or
            [string]$locked.sha512 -notmatch '^[a-f0-9]{128}$') { throw "Runtime pack '$($locked.name)' is not the exact selected project-assets dependency." }
        $decoded = ConvertFrom-NuGetSha512 ([string]$locked.contentHash) "$($locked.name) $($locked.version)"
        if ($decoded -cne [string]$locked.sha512) { throw "Runtime-pack lock hashes disagree for '$($locked.name)'." }
        $hashEvidence = $null; $nupkg = $null
        foreach ($folder in $packageFolders) {
            $base = Join-Path (Join-Path $folder ([string]$locked.name).ToLowerInvariant()) ([string]$locked.version)
            $candidateHash = Join-Path $base "$(([string]$locked.name).ToLowerInvariant()).$($locked.version).nupkg.sha512"
            $candidatePackage = Join-Path $base "$(([string]$locked.name).ToLowerInvariant()).$($locked.version).nupkg"
            if ((Test-Path -LiteralPath $candidateHash -PathType Leaf) -and (Test-Path -LiteralPath $candidatePackage -PathType Leaf)) {
                $hashEvidence = $candidateHash; $nupkg = $candidatePackage; break
            }
        }
        if (-not $hashEvidence) { throw "Restored nupkg SHA-512 evidence is missing for '$($locked.name)'." }
        $restoredHash = (Get-Content -LiteralPath $hashEvidence -Raw).Trim()
        if ($restoredHash -cne [string]$locked.contentHash -or
            (Get-FileHash -LiteralPath $nupkg -Algorithm SHA512).Hash.ToLowerInvariant() -cne [string]$locked.sha512) {
            throw "Restored nupkg evidence differs from the committed runtime-pack lock for '$($locked.name)'."
        }
        $item = $selected[$key]
        $results += [pscustomobject]@{
            FrameworkReference=$item.FrameworkReference; Id=$item.Id; Version=$item.Version
            Sha512=[string]$locked.sha512; ContentHash=[string]$locked.contentHash
            Projects=$item.Projects; Targets=$item.Targets
        }
    }
    return @($results | Sort-Object Id,Version)
}

Export-ModuleMember -Function Get-ProductionNuGetEvidence, Get-SelfContainedRuntimePackEvidence
