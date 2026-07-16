[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsixPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OpenSshConfiguration {
    param(
        [Parameter(Mandatory)][string]$SshPath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    # Windows PowerShell promotes native stderr to an ErrorRecord when the
    # script-wide preference is Stop. OpenSSH may emit a harmless TTY warning
    # while still returning a valid `-G` configuration, so capture the stream
    # and decide from the native exit code instead.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $SshPath -G @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Packaged ssh.exe could not resolve its effective configuration: $($output -join ' ')"
    }
    $configuration = @{}
    foreach ($line in $output) {
        $parts = $line.ToString().Split(' ', 2, [StringSplitOptions]::RemoveEmptyEntries)
        if ($parts.Count -eq 2 -and -not $configuration.ContainsKey($parts[0])) {
            $configuration[$parts[0]] = $parts[1]
        }
    }
    return $configuration
}

function Assert-OpenSshConfiguration {
    param(
        [Parameter(Mandatory)][hashtable]$Configuration,
        [Parameter(Mandatory)][hashtable]$Expected,
        [Parameter(Mandatory)][string]$Mode
    )
    foreach ($entry in $Expected.GetEnumerator()) {
        if (-not $Configuration.ContainsKey($entry.Key) -or
            $Configuration[$entry.Key] -cne $entry.Value) {
            $actual = if ($Configuration.ContainsKey($entry.Key)) { $Configuration[$entry.Key] } else { '<missing>' }
            throw "Packaged ssh.exe resolved unsafe $Mode configuration '$($entry.Key)=$actual'; expected '$($entry.Value)'."
        }
    }
}

$package = (Resolve-Path -LiteralPath $MsixPath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-runtime-smoke-$([Guid]::NewGuid().ToString('N'))"
$expanded = Join-Path $testRoot 'package'
$writable = Join-Path $testRoot 'data'
$mirrorSource = Join-Path $writable 'mirror-source'
$mirrorDestination = Join-Path $writable 'mirror-destination'
$mirrorOutside = Join-Path $writable 'mirror-outside'
$skipSource = Join-Path $writable 'skip-source.txt'
$skipTarget = Join-Path $mirrorDestination 'skip-target.txt'
try {
    $makeAppx = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -File -Filter makeappx.exe |
        Where-Object FullName -Match '\\x64\\makeappx\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $makeAppx) { throw 'Windows SDK makeappx.exe (x64) was not found.' }
    $unpackOutput = & $makeAppx.FullName unpack /p $package /d $expanded /o 2>&1
    if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed with exit code $LASTEXITCODE`: $($unpackOutput -join ' ')" }
    [IO.Directory]::CreateDirectory((Join-Path $mirrorSource 'nested')) | Out-Null
    [IO.Directory]::CreateDirectory($mirrorDestination) | Out-Null
    [IO.Directory]::CreateDirectory($mirrorOutside) | Out-Null
    [IO.File]::WriteAllText((Join-Path $mirrorSource 'nested\payload.txt'), 'packaged-directory-mirror-ok')
    [IO.File]::WriteAllText((Join-Path $mirrorSource 'overwrite.txt'), 'packaged-temporary-replacement')
    $overwriteTarget = Join-Path $mirrorDestination 'overwrite.txt'
    $overwriteHardLink = Join-Path $mirrorOutside 'outside-overwrite-hardlink.txt'
    [IO.File]::WriteAllText($overwriteTarget, 'old')
    New-Item -ItemType HardLink -Path $overwriteHardLink -Target $overwriteTarget | Out-Null
    [IO.File]::WriteAllText($skipSource, 'must-not-replace-skip-target')
    [IO.File]::WriteAllText($skipTarget, 'skip-target-must-remain')
    [IO.File]::WriteAllText((Join-Path $mirrorDestination 'extraneous-sentinel.txt'), 'must-remain')
    [IO.File]::WriteAllText((Join-Path $mirrorOutside 'outside-must-not-transfer.txt'), 'must-not-transfer')
    New-Item -ItemType Junction -Path (Join-Path $mirrorSource 'junction-must-not-transfer') -Target $mirrorOutside | Out-Null
    $runtime = Join-Path $expanded 'lftp'
    $bin = Join-Path $runtime 'usr\bin'
    foreach ($required in @('lftp.exe', 'ssh.exe', 'sh.exe', 'cygpath.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bin $required) -PathType Leaf)) { throw "Packaged runtime is missing $required." }
    }
    $before = @{}
    foreach ($file in Get-ChildItem -LiteralPath $expanded -Recurse -File) {
        $before[$file.FullName.Substring($expanded.Length + 1)] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        $file.IsReadOnly = $true
    }
    $saved = @{ HOME=$env:HOME; TMP=$env:TMP; TEMP=$env:TEMP; PATH=$env:PATH; LANG=$env:LANG; LC_ALL=$env:LC_ALL }
    try {
        $env:HOME = $writable; $env:TMP = $writable; $env:TEMP = $writable; $env:PATH = "$bin;$($env:PATH)"; $env:LANG = 'C.UTF-8'; $env:LC_ALL = 'C.UTF-8'
        $sourcePosix = (& (Join-Path $bin 'cygpath.exe') -u $mirrorSource | Select-Object -First 1).Trim()
        $destinationPosix = (& (Join-Path $bin 'cygpath.exe') -u $mirrorDestination | Select-Object -First 1).Trim()
        $skipSourcePosix = (& (Join-Path $bin 'cygpath.exe') -u $skipSource | Select-Object -First 1).Trim()
        $skipTargetPosix = (& (Join-Path $bin 'cygpath.exe') -u $skipTarget | Select-Object -First 1).Trim()
        $knownHostsPosix = (& (Join-Path $bin 'cygpath.exe') -u (Join-Path $writable 'known hosts') | Select-Object -First 1).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourcePosix) -or
            [string]::IsNullOrWhiteSpace($destinationPosix) -or [string]::IsNullOrWhiteSpace($skipSourcePosix) -or
            [string]::IsNullOrWhiteSpace($skipTargetPosix) -or [string]::IsNullOrWhiteSpace($knownHostsPosix)) {
            throw 'cygpath.exe could not map the writable directory-mirror paths.'
        }
        $sshPath = Join-Path $bin 'ssh.exe'
        $strictSsh = Get-OpenSshConfiguration -SshPath $sshPath -Arguments @(
            '-F', 'none', '-a', '-x',
            '-o', 'StrictHostKeyChecking=yes',
            '-o', "UserKnownHostsFile=`"$knownHostsPosix`"",
            '-o', 'GlobalKnownHostsFile=none',
            '-o', 'UpdateHostKeys=no',
            '-o', 'VerifyHostKeyDNS=no',
            '-o', 'CheckHostIP=no',
            '-o', 'IdentityAgent=none',
            '-o', 'HostKeyAlias=lftp-pilot-runtime-smoke',
            '-p', '22', '-l', 'alice', 'example.test'
        )
        Assert-OpenSshConfiguration -Mode 'strict host-key' -Configuration $strictSsh -Expected @{
            stricthostkeychecking = 'true'
            userknownhostsfile = $knownHostsPosix
            globalknownhostsfile = 'none'
            updatehostkeys = 'false'
            verifyhostkeydns = 'false'
            checkhostip = 'no'
            identityagent = 'none'
            hostkeyalias = 'lftp-pilot-runtime-smoke'
        }
        $probeSsh = Get-OpenSshConfiguration -SshPath $sshPath -Arguments @(
            '-F', 'none', '-a', '-x', '-n', '-N',
            '-o', 'BatchMode=yes',
            '-o', 'StrictHostKeyChecking=accept-new',
            '-o', "UserKnownHostsFile=`"$knownHostsPosix`"",
            '-o', 'GlobalKnownHostsFile=none',
            '-o', 'HashKnownHosts=no',
            '-o', 'UpdateHostKeys=no',
            '-o', 'VerifyHostKeyDNS=no',
            '-o', 'CheckHostIP=no',
            '-o', 'PubkeyAuthentication=no',
            '-o', 'PasswordAuthentication=no',
            '-o', 'KbdInteractiveAuthentication=no',
            '-o', 'GSSAPIAuthentication=no',
            '-o', 'HostbasedAuthentication=no',
            '-o', 'IdentityAgent=none',
            '-o', 'IdentitiesOnly=yes',
            '-o', 'NumberOfPasswordPrompts=0',
            '-o', 'ConnectionAttempts=1',
            '-o', 'ConnectTimeout=15',
            '-o', 'HostKeyAlias=lftp-pilot-runtime-probe',
            '-p', '22', '-l', 'lftp-pilot-host-key-probe', 'example.test'
        )
        Assert-OpenSshConfiguration -Mode 'host-key probe' -Configuration $probeSsh -Expected @{
            batchmode = 'yes'
            stricthostkeychecking = 'accept-new'
            userknownhostsfile = $knownHostsPosix
            globalknownhostsfile = 'none'
            hashknownhosts = 'no'
            updatehostkeys = 'false'
            verifyhostkeydns = 'false'
            checkhostip = 'no'
            pubkeyauthentication = 'false'
            passwordauthentication = 'no'
            kbdinteractiveauthentication = 'no'
            gssapiauthentication = 'no'
            hostbasedauthentication = 'no'
            identityagent = 'none'
            identitiesonly = 'yes'
            numberofpasswordprompts = '0'
            connectionattempts = '1'
            connecttimeout = '15'
            hostkeyalias = 'lftp-pilot-runtime-probe'
        }
        $commands = @(
            @{ Exe = 'lftp.exe'; Arguments = '--norc -c "open file:///; cls -1 /"'; Expected = 'bin' },
            @{ Exe = 'lftp.exe'; Arguments = "--norc -c `"open file:///; set xfer:make-backup no; set xfer:use-temp-file yes; mirror --reverse --continue --no-symlinks --overwrite --verbose=1 '$sourcePosix' '$destinationPosix'`""; Expected = 'Transferring file' },
            @{ Exe = 'lftp.exe'; Arguments = "--norc -c `"open file:///; set xfer:make-backup no; set xfer:use-temp-file yes; set xfer:use-temp-file no; set xfer:clobber no; get '$skipSourcePosix' -o '$skipTargetPosix'; set xfer:clobber yes; set xfer:use-temp-file yes`""; Expected = $null },
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
                if ($process.ExitCode -ne 0 -or ($null -ne $command.Expected -and $output -notmatch [Regex]::Escape($command.Expected))) {
                    throw "$($command.Exe) smoke test failed (exit $($process.ExitCode)): $output"
                }
            }
            finally { $process.Dispose() }
        }
        $mirroredPayload = Join-Path $mirrorDestination 'nested\payload.txt'
        if (-not (Test-Path -LiteralPath $mirroredPayload -PathType Leaf) -or
            [IO.File]::ReadAllText($mirroredPayload) -ne 'packaged-directory-mirror-ok') {
            throw 'Packaged LFTP did not mirror the writable directory tree exactly.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $mirrorDestination 'extraneous-sentinel.txt') -PathType Leaf) -or
            [IO.File]::ReadAllText((Join-Path $mirrorDestination 'extraneous-sentinel.txt')) -ne 'must-remain') {
            throw 'Packaged LFTP removed or changed an extraneous destination file without delete approval.'
        }
        if (Test-Path -LiteralPath (Join-Path $mirrorDestination 'junction-must-not-transfer')) {
            throw 'Packaged LFTP followed or materialized a source junction despite --no-symlinks.'
        }
        if ([IO.File]::ReadAllText($overwriteTarget) -ne 'packaged-temporary-replacement' -or
            [IO.File]::ReadAllText($overwriteHardLink) -ne 'old') {
            throw 'Packaged LFTP did not replace the changed destination through a temporary file while preserving the prior hard-linked inode.'
        }
        if ([IO.File]::ReadAllText($skipTarget) -ne 'skip-target-must-remain') {
            throw 'Packaged LFTP replaced an existing no-clobber target despite the scoped temporary-file override.'
        }
        if (@(Get-ChildItem -LiteralPath $mirrorDestination -File -Filter 'overwrite.txt~*~').Count -ne 0) {
            throw 'Packaged LFTP left timestamped backup debris after a successful temporary-file replacement.'
        }
    }
    finally { $env:HOME=$saved.HOME; $env:TMP=$saved.TMP; $env:TEMP=$saved.TEMP; $env:PATH=$saved.PATH; $env:LANG=$saved.LANG; $env:LC_ALL=$saved.LC_ALL }
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
'Packaged LFTP file/directory, strict SFTP host-key, credential-free OpenSSH probe, and shell smoke tests passed with a read-only package and writable app data.'
