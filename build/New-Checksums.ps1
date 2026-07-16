[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$LiteralPath,
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$lines = foreach ($path in ($LiteralPath | Sort-Object { [IO.Path]::GetFileName($_) })) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    if ($resolved -eq $resolvedOutput) { throw 'The checksum output cannot hash itself.' }
    "$(Get-FileSha256 -LiteralPath $resolved) *$([IO.Path]::GetFileName($resolved))"
}
$temporary = "$resolvedOutput.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllLines($temporary, $lines, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Get-Item -LiteralPath $resolvedOutput
