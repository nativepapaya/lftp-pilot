[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+$')][string]$ProductVersion,
    [Parameter(Mandatory)][ValidateRange(0, 4294967295)][uint64]$Sequence,
    [string]$PreviousVersion,
    [string]$GitHubOutput
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$version = New-MsixVersion -ProductVersion $ProductVersion -Sequence $Sequence -PreviousVersion $PreviousVersion
if ($GitHubOutput) { Add-Content -LiteralPath $GitHubOutput -Value "msix_version=$version" -Encoding utf8 }
$version
