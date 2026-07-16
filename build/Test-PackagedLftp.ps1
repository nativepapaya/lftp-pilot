[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsixPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-PackagedRuntime.ps1') -MsixPath $MsixPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
