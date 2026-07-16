[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = 'true'

if (-not $SkipRestore) {
    & $dotnet restore (Join-Path $PSScriptRoot 'LFTPPilot.slnx') --locked-mode -r win-x64 -p:LockedRestore=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
}

& $dotnet build (Join-Path $PSScriptRoot 'LFTPPilot.slnx') `
    --configuration $Configuration `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

& $dotnet test (Join-Path $PSScriptRoot 'tests\LFTPPilot.Tests\LFTPPilot.Tests.csproj') `
    --configuration $Configuration `
    --no-build `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

$toolTests = Join-Path $PSScriptRoot 'build\tests'
if (Test-Path -LiteralPath $toolTests -PathType Container) {
    Get-ChildItem -LiteralPath $toolTests -Filter '*.Tests.ps1' -File |
        Sort-Object Name |
        ForEach-Object {
            & $_.FullName
            if ($LASTEXITCODE -ne 0) {
                throw "Release-tool test '$($_.Name)' failed with exit code $LASTEXITCODE."
            }
        }
}

Write-Host 'LFTP Pilot validation completed successfully.'
