[CmdletBinding()]
param(
    [string]$PythonPath = 'python',
    [string]$LftpPath,
    [switch]$KeepLab
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\protocol-lab'
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$artifactRoot = (Resolve-Path $artifactRoot).Path
$labRoot = Join-Path $artifactRoot ("run-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($labRoot) | Out-Null
$labRoot = (Resolve-Path $labRoot).Path
if (-not $labRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The protocol-lab working root escaped the ignored artifact directory.'
}

$venv = Join-Path $artifactRoot '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'
$requirements = Join-Path $repoRoot 'eng\protocol-lab\requirements.txt'
$serverScript = Join-Path $repoRoot 'eng\protocol-lab\server.py'
$configPath = Join-Path $labRoot 'config.json'
$stopPath = Join-Path $labRoot 'stop'
$stdoutPath = Join-Path $labRoot 'server.stdout.log'
$stderrPath = Join-Path $labRoot 'server.stderr.log'
$serverProcess = $null
$savedConfig = $env:LFTP_PILOT_PROTOCOL_LAB_CONFIG
$savedLftp = $env:LFTP_PILOT_PROTOCOL_LAB_LFTP

try {
    if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
        & $PythonPath -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw "Python could not create the protocol-lab virtual environment (exit $LASTEXITCODE)." }
    }
    & $venvPython -m pip install --disable-pip-version-check --quiet --require-hashes --requirement $requirements
    if ($LASTEXITCODE -ne 0) { throw "The pinned protocol-lab dependencies could not be installed (exit $LASTEXITCODE)." }

    if ([string]::IsNullOrWhiteSpace($LftpPath)) {
        $candidate = Join-Path $repoRoot 'artifacts\lftp-runtime-acquired-current\usr\bin\lftp.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw 'No LFTP runtime was supplied and artifacts\lftp-runtime-acquired-current\usr\bin\lftp.exe was not found.'
        }
        $LftpPath = $candidate
    }
    $sourceLftp = (Resolve-Path -LiteralPath $LftpPath).Path
    $sourceBin = Split-Path $sourceLftp -Parent
    $sourceUsr = Split-Path $sourceBin -Parent
    $sourceRuntime = Split-Path $sourceUsr -Parent
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRuntime 'usr\ssl\certs\ca-bundle.crt') -PathType Leaf)) {
        throw 'The selected LFTP runtime does not contain its expected CA bundle.'
    }

    $serverProcess = Start-Process -FilePath $venvPython -ArgumentList @(
        $serverScript,
        '--root', (Join-Path $labRoot 'server'),
        '--config', $configPath,
        '--stop-file', $stopPath
    ) -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $configPath -PathType Leaf) -and
        -not $serverProcess.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        $serverError = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
        throw "The controlled protocol lab did not become ready. $serverError"
    }

    # Keep the runtime copy disposable. The integration fixture injects this
    # run's ssl:ca-file through a test-only ILftpProcessHost wrapper because the
    # relocatable MSYS2 runtime does not discover an appended default bundle.
    # Certificate verification remains enabled and no user trust store changes.
    $runtimeCopy = Join-Path $labRoot 'runtime'
    Copy-Item -LiteralPath $sourceRuntime -Destination $runtimeCopy -Recurse
    $testLftp = Join-Path $runtimeCopy 'usr\bin\lftp.exe'

    $env:LFTP_PILOT_PROTOCOL_LAB_CONFIG = $configPath
    $env:LFTP_PILOT_PROTOCOL_LAB_LFTP = $testLftp
    & (Join-Path $repoRoot '.dotnet\dotnet.exe') restore `
        (Join-Path $repoRoot 'tests\LFTPPilot.Tests\LFTPPilot.Tests.csproj') `
        --locked-mode -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "The protocol integration test restore failed (exit $LASTEXITCODE)." }
    & (Join-Path $repoRoot '.dotnet\dotnet.exe') test `
        (Join-Path $repoRoot 'tests\LFTPPilot.Tests\LFTPPilot.Tests.csproj') `
        -c Release --no-restore --filter 'Category=ProtocolIntegration'
    if ($LASTEXITCODE -ne 0) { throw "The controlled protocol matrix failed (exit $LASTEXITCODE)." }

    'Controlled FTP, opportunistic TLS, FTPES, implicit FTPS, and SFTP password endpoints passed Unicode browse, mutation, upload, segmented download, and cleanup checks.'
}
finally {
    $env:LFTP_PILOT_PROTOCOL_LAB_CONFIG = $savedConfig
    $env:LFTP_PILOT_PROTOCOL_LAB_LFTP = $savedLftp
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        [IO.File]::WriteAllText($stopPath, 'stop')
        if (-not $serverProcess.WaitForExit(5000)) {
            Stop-Process -Id $serverProcess.Id -Force
        }
    }
    if (-not $KeepLab -and (Test-Path -LiteralPath $labRoot)) {
        $resolved = (Resolve-Path -LiteralPath $labRoot).Path
        if (-not $resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a protocol-lab directory outside the ignored artifact root.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    elseif ($KeepLab) {
        "Protocol lab retained at $labRoot"
    }
}
