[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsixPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $MsixPath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-runtime-smoke-$([Guid]::NewGuid().ToString('N'))"
$expanded = Join-Path $testRoot 'package'
$writable = Join-Path $testRoot 'data'
try {
    $makeAppx = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -File -Filter makeappx.exe |
        Where-Object FullName -Match '\\x64\\makeappx\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $makeAppx) { throw 'Windows SDK makeappx.exe (x64) was not found.' }
    $unpackOutput = & $makeAppx.FullName unpack /p $package /d $expanded /o 2>&1
    if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed with exit code $LASTEXITCODE`: $($unpackOutput -join ' ')" }
    [IO.Directory]::CreateDirectory($writable) | Out-Null
    $runtime = Join-Path $expanded 'lftp'
    $bin = Join-Path $runtime 'usr\bin'
    foreach ($required in @('lftp.exe', 'ssh.exe', 'sh.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bin $required) -PathType Leaf)) { throw "Packaged runtime is missing $required." }
    }
    $before = @{}
    foreach ($file in Get-ChildItem -LiteralPath $expanded -Recurse -File) {
        $before[$file.FullName.Substring($expanded.Length + 1)] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        $file.IsReadOnly = $true
    }
    $saved = @{ HOME=$env:HOME; TMP=$env:TMP; TEMP=$env:TEMP; PATH=$env:PATH }
    try {
        $env:HOME = $writable; $env:TMP = $writable; $env:TEMP = $writable; $env:PATH = "$bin;$($env:PATH)"
        $commands = @(
            @{ Exe = 'lftp.exe'; Arguments = '--norc -c "open file:///; cls -1 /"'; Expected = 'bin' },
            @{ Exe = 'ssh.exe'; Arguments = '-V'; Expected = 'OpenSSH' },
            @{ Exe = 'sh.exe'; Arguments = '-lc "printf packaged-runtime-ok"'; Expected = 'packaged-runtime-ok' }
        )
        foreach ($command in $commands) {
            $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $bin $command.Exe))
            $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
            $start.Arguments = $command.Arguments
            $process = [Diagnostics.Process]::Start($start)
            try {
                if (-not $process.WaitForExit(15000)) { $process.Kill(); throw "$($command.Exe) timed out." }
                $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
                if ($process.ExitCode -ne 0 -or $output -notmatch [Regex]::Escape($command.Expected)) {
                    throw "$($command.Exe) smoke test failed (exit $($process.ExitCode)): $output"
                }
            }
            finally { $process.Dispose() }
        }
    }
    finally { $env:HOME=$saved.HOME; $env:TMP=$saved.TMP; $env:TEMP=$saved.TEMP; $env:PATH=$saved.PATH }
    $afterFiles = @(Get-ChildItem -LiteralPath $expanded -Recurse -File)
    if ($afterFiles.Count -ne $before.Count) { throw 'Packaged processes created or removed files beneath the read-only package root.' }
    foreach ($file in $afterFiles) {
        $relative = $file.FullName.Substring($expanded.Length + 1)
        if (-not $before.ContainsKey($relative) -or (Get-FileHash $file.FullName -Algorithm SHA256).Hash -ne $before[$relative]) {
            throw "Packaged runtime modified '$relative'."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Get-ChildItem -LiteralPath $testRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
'Packaged LFTP, OpenSSH, and shell smoke tests passed with a read-only package and writable app data.'
