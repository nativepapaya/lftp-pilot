[CmdletBinding()]
param([string]$SolutionPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'DependencyEvidence.psm1') -Force
if (-not $SolutionPath) { $SolutionPath = Join-Path $repository 'LFTPPilot.slnx' }
$solution = (Resolve-Path -LiteralPath $SolutionPath).Path
$projects = @(Get-ChildItem (Join-Path $repository 'src'),(Join-Path $repository 'tests') -Recurse -Filter *.csproj -File |
    Where-Object FullName -NotMatch '\\(?:bin|obj)\\')
if ($projects.Count -lt 1) { throw 'No .NET projects were found for locked restore validation.' }
$runtimePackLock = Join-Path $PSScriptRoot 'runtime-lock\dotnet-runtime-packs.lock.json'
if (-not (Test-Path -LiteralPath $runtimePackLock -PathType Leaf)) { throw 'The self-contained .NET runtime-pack lock is missing.' }
foreach ($project in $projects) {
    $lockPath = Join-Path $project.DirectoryName 'packages.lock.json'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) { throw "NuGet lock file is missing for $($project.FullName)." }
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    if ($lock.version -ne 1 -or $null -eq $lock.dependencies) { throw "NuGet lock file is malformed: $lockPath" }
    foreach ($target in $lock.dependencies.PSObject.Properties) {
        foreach ($dependency in $target.Value.PSObject.Properties) {
            if ($dependency.Value.type -ne 'Project' -and
                ($dependency.Value.resolved -notmatch '^\d' -or $dependency.Value.contentHash -notmatch '^[A-Za-z0-9+/]+={0,2}$')) {
                throw "Resolved NuGet evidence is incomplete for $($dependency.Name) in $lockPath."
            }
            if ($dependency.Value.type -ne 'Project') {
                try { $hashBytes = [Convert]::FromBase64String([string]$dependency.Value.contentHash) }
                catch { throw "Resolved NuGet hash is malformed for $($dependency.Name) in $lockPath." }
                if ($hashBytes.Length -ne 64) { throw "Resolved NuGet SHA-512 is malformed for $($dependency.Name) in $lockPath." }
            }
        }
    }
}
$git = Get-Command git -ErrorAction SilentlyContinue
if ($null -ne $git) {
    $savedPreference = $ErrorActionPreference
    try { $ErrorActionPreference='Continue'; & $git.Source -C $repository rev-parse --verify HEAD *> $null; $headExit=$LASTEXITCODE }
    finally { $ErrorActionPreference=$savedPreference }
    if ($headExit -eq 0) {
        $relativeLocks = @($projects | ForEach-Object {
            (Join-Path $_.DirectoryName 'packages.lock.json').Substring($repository.Length + 1).Replace('\','/')
        })
        $relativeLocks += $runtimePackLock.Substring($repository.Length + 1).Replace('\','/')
        foreach ($relative in $relativeLocks) {
            try { $ErrorActionPreference='Continue'; & $git.Source -C $repository ls-files --error-unmatch -- $relative *> $null; $trackedExit=$LASTEXITCODE }
            finally { $ErrorActionPreference=$savedPreference }
            if ($trackedExit -ne 0) { throw "NuGet lock file is not committed: $relative" }
        }
        try { $ErrorActionPreference='Continue'; & $git.Source -C $repository diff --quiet HEAD -- @relativeLocks *> $null; $diffExit=$LASTEXITCODE }
        finally { $ErrorActionPreference=$savedPreference }
        if ($diffExit -ne 0) { throw 'Committed NuGet lock files have unreviewed working-tree changes.' }
    }
}
$dotnet = Join-Path $repository '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet restore $solution --locked-mode -r win-x64 -p:LockedRestore=true
if ($LASTEXITCODE -ne 0) { throw "Locked NuGet restore failed with exit code $LASTEXITCODE." }
$productionAssets = @(Get-ChildItem (Join-Path $repository 'src') -Recurse -Filter project.assets.json -File |
    Where-Object FullName -Match '\\obj\\project\.assets\.json$' | Select-Object -ExpandProperty FullName)
$productionProjects = @(Get-ChildItem (Join-Path $repository 'src') -Recurse -Filter *.csproj -File |
    Where-Object FullName -NotMatch '\\(?:bin|obj)\\')
if ($productionAssets.Count -ne $productionProjects.Count) { throw 'Production project-assets evidence is incomplete after locked restore.' }
$packs = @(Get-SelfContainedRuntimePackEvidence -ProjectAssetsPath $productionAssets -RuntimePackLockPath $runtimePackLock)
"Verified $($projects.Count) committed NuGet locks and $($packs.Count) self-contained runtime-pack lock with a locked win-x64 restore."
