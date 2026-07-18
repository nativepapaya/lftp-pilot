Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$buildRoot = Split-Path $PSScriptRoot -Parent
$repository = Split-Path $buildRoot -Parent
Import-Module (Join-Path $buildRoot 'ReleaseTools.psm1') -Force

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw "$Message did not throw." } catch { if ($_.Exception.Message -like "$Message did not throw.*") { throw } }
}
function Assert-ThrowsLike([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message did not throw." }
    catch {
        if ($_.Exception.Message -like "$Message did not throw.*") { throw }
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Message produced unexpected error '$($_.Exception.Message)'."
        }
    }
}
function Get-ThrownMessage([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw "$Message did not throw." }
    catch {
        if ($_.Exception.Message -like "$Message did not throw.*") { throw }
        return $_.Exception.Message
    }
}

Assert-Equal '1.0.0.42' (New-MsixVersion -ProductVersion '1.0' -Sequence 42 -PreviousVersion '1.0.0.41') 'Version mapping failed.'
Assert-Equal '1.0.1.0' (New-MsixVersion -ProductVersion '1.0' -Sequence 65536 -PreviousVersion '1.0.0.65535') 'Base-65536 rollover failed.'
Assert-Throws { New-MsixVersion -ProductVersion '1.0' -Sequence 41 -PreviousVersion '1.0.0.41' } 'Non-monotonic version'
Assert-Throws { Assert-MsixVersion -Version '1.2.3.65536' } 'Out-of-range version'

$provenanceArguments = Get-BuildProvenanceVerificationArguments -ArtifactPath 'C:\fixture\LFTPPilot.msix' `
    -SourceDigest ('a' * 40)
$provenanceCommand = $provenanceArguments -join ' '
foreach ($required in @(
    '--repo nativepapaya/lftp-pilot',
    '--signer-workflow nativepapaya/lftp-pilot/.github/workflows/unsigned-package.yml',
    '--source-ref refs/heads/main',
    "--source-digest $('a' * 40)",
    "--signer-digest $('a' * 40)",
    '--deny-self-hosted-runners',
    '--cert-oidc-issuer https://token.actions.githubusercontent.com',
    '--predicate-type https://slsa.dev/provenance/v1',
    '--format json'
)) {
    if (-not $provenanceCommand.Contains($required)) { throw "Build provenance policy omitted '$required'." }
}
if ($provenanceCommand.Contains('--no-public-good')) {
    throw 'Public-repository provenance cannot reject the Sigstore Public Good Instance used by GitHub Actions.'
}
Assert-Throws { Get-BuildProvenanceVerificationArguments 'fixture.msix' ('a' * 40) 'other/repository' } 'Repository scope override'

$fakeGhRoot = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-fake-gh-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Directory]::CreateDirectory($fakeGhRoot) | Out-Null
    $fakeGh = Join-Path $fakeGhRoot 'gh.cmd'
    function Set-FakeGitHubResult([string]$StandardOutput, [string[]]$StandardError, [int]$ExitCode) {
        $commands = [Collections.Generic.List[string]]::new()
        $commands.Add('@echo off')
        foreach ($line in @($StandardError)) { $commands.Add(">&2 echo $line") }
        if ($null -ne $StandardOutput) { $commands.Add("echo $StandardOutput") }
        $commands.Add("exit /b $ExitCode")
        [IO.File]::WriteAllText($fakeGh, (($commands -join "`r`n") + "`r`n"), [Text.Encoding]::ASCII)
    }
    $validPublicTlog = '[{"verificationResult":{"signature":{"certificate":{"sourceRepositoryVisibilityAtSigning":"public"}},"verifiedTimestamps":[{"type":"Tlog"}]}}]'
    Set-FakeGitHubResult $validPublicTlog @('benign verifier diagnostic') 0
    $fakeResult = Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify','fixture.msix')
    if (@($fakeResult.Results).Count -ne 1 -or
        $fakeResult.Results[0].verificationResult.signature.certificate.sourceRepositoryVisibilityAtSigning -cne 'public') {
        throw 'Successful GitHub verification did not preserve the verified JSON result.'
    }
    if ($ErrorActionPreference -cne 'Stop') { throw 'GitHub verification did not restore ErrorActionPreference.' }

    Set-FakeGitHubResult $validPublicTlog @('verifier rejected fixture') 23
    Assert-ThrowsLike {
        Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify')
    } '*exit code 23*verifier rejected fixture*' 'Nonzero GitHub verifier'
    if ($ErrorActionPreference -cne 'Stop') { throw 'GitHub verification did not restore ErrorActionPreference after failure.' }
    Set-FakeGitHubResult $null @(('x' * 5000), (('y' * 5000) + 'TAIL-MARKER')) 24
    $boundedFailure = Get-ThrownMessage {
        Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify')
    } 'Long GitHub verifier diagnostic'
    if ($boundedFailure.Length -gt 8300 -or $boundedFailure.Contains('TAIL-MARKER')) {
        throw 'GitHub verifier stderr was not bounded before entering the release error record.'
    }
    Set-FakeGitHubResult 'not-json' @() 0
    Assert-ThrowsLike {
        Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify')
    } '*invalid JSON*' 'Invalid GitHub verifier JSON'
    Set-FakeGitHubResult '[]' @() 0
    Assert-ThrowsLike {
        Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify')
    } '*no matching build provenance*' 'Empty GitHub verifier result'
    Set-FakeGitHubResult '[{"verificationResult":{"signature":{"certificate":{"sourceRepositoryVisibilityAtSigning":"private"}},"verifiedTimestamps":[{"type":"Tlog"}]}}]' @() 0
    Assert-ThrowsLike {
        Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify')
    } '*transparency-log timestamp and public source identity*' 'Private source attestation'
    Set-FakeGitHubResult '[{"verificationResult":{"signature":{"certificate":{"sourceRepositoryVisibilityAtSigning":"public"}},"verifiedTimestamps":[{"type":"TimestampAuthority"}]}}]' @() 0
    Assert-ThrowsLike {
        Invoke-GitHubBuildProvenanceVerification -ExecutablePath $fakeGh -Arguments @('attestation','verify')
    } '*transparency-log timestamp and public source identity*' 'Attestation without transparency log'
}
finally {
    if (Test-Path -LiteralPath $fakeGhRoot) { Remove-Item -LiteralPath $fakeGhRoot -Recurse -Force }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-appinstaller-$([Guid]::NewGuid().ToString('N')).appinstaller"
try {
    & (Join-Path $buildRoot 'New-AppInstaller.ps1') -Version '1.0.0.42' -OutputPath $temporary | Out-Null
    [xml]$xml = Get-Content -LiteralPath $temporary -Raw
    $namespace = [Xml.XmlNamespaceManager]::new($xml.NameTable)
    $namespace.AddNamespace('a', 'http://schemas.microsoft.com/appx/appinstaller/2021')
    $root = $xml.SelectSingleNode('/a:AppInstaller', $namespace)
    Assert-Equal '1.0.0.42' $root.Version 'Feed version differs.'
    Assert-Equal 'LFTPPilot.Desktop' $xml.SelectSingleNode('/a:AppInstaller/a:MainPackage', $namespace).Name 'Identity differs.'
    $launch = $xml.SelectSingleNode('/a:AppInstaller/a:UpdateSettings/a:OnLaunch', $namespace)
    Assert-Equal '24' $launch.HoursBetweenUpdateChecks 'Update interval differs.'
    Assert-Equal 'false' $launch.ShowPrompt 'Update prompt must be quiet.'
    Assert-Equal 'false' $launch.UpdateBlocksActivation 'Updates must not block startup.'
    if ($null -eq $xml.SelectSingleNode('/a:AppInstaller/a:UpdateSettings/a:AutomaticBackgroundTask', $namespace)) {
        throw 'AutomaticBackgroundTask is missing.'
    }
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }

function New-TestCertificate([DateTimeOffset]$NotBefore, [DateTimeOffset]$NotAfter, [switch]$CodeSigning) {
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=LFTPPilot.Dev', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    if ($CodeSigning) {
        $oids = [Security.Cryptography.OidCollection]::new()
        [void]$oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
    }
    $certificate = $request.CreateSelfSigned($NotBefore, $NotAfter)
    $rsa.Dispose()
    return $certificate
}
$now = [DateTimeOffset]::UtcNow
$exportable = New-TestCertificate $now.AddDays(-1) $now.AddDays(1) -CodeSigning
try {
    [void](Assert-ReleaseCertificate $exportable)
    Assert-ThrowsLike {
        Assert-ReleaseCertificate $exportable -RequireNonExportablePrivateKey
    } '*non-exportable RSACng key*' 'Exportable certificate'
}
finally { $exportable.Dispose() }
$expired = New-TestCertificate $now.AddDays(-3) $now.AddDays(-2) -CodeSigning
try { Assert-Throws { Assert-ReleaseCertificate $expired } 'Expired certificate' } finally { $expired.Dispose() }
$wrongEku = New-TestCertificate $now.AddDays(-1) $now.AddDays(1)
try { Assert-Throws { Assert-ReleaseCertificate $wrongEku } 'Missing code-signing EKU' } finally { $wrongEku.Dispose() }

$continuity = New-TestCertificate $now.AddDays(-1) $now.AddDays(1) -CodeSigning
$otherContinuity = New-TestCertificate $now.AddDays(-1) $now.AddDays(1) -CodeSigning
try {
    Assert-Throws { Assert-CertificateContinuity $continuity } 'Unreviewed first certificate'
    $initial = Assert-CertificateContinuity $continuity -ApproveInitialReleaseCertificate
    if (-not $initial.InitialRelease) { throw 'Explicit initial-certificate review was not recorded.' }
    Assert-Throws { Assert-CertificateContinuity $continuity -PreviousReleaseExists } 'Missing previous release certificate'
    $publicCopy = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $continuity.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    try {
        $continued = Assert-CertificateContinuity $continuity $publicCopy -PreviousReleaseExists
        if ($continued.InitialRelease) { throw 'Matching historical certificate was treated as an initial release.' }
    }
    finally { $publicCopy.Dispose() }
    Assert-Throws { Assert-CertificateContinuity $continuity $otherContinuity -PreviousReleaseExists } 'Changed release certificate'
}
finally { $continuity.Dispose(); $otherContinuity.Dispose() }

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-provenance-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $fixture = Join-Path $fixtureRoot "synthetic-$([Guid]::NewGuid().ToString('N')).msix"
    [IO.File]::WriteAllBytes($fixture, [byte[]](1,2,3,4))
    $previousBypass = $env:LFTP_PILOT_SYNTHETIC_PROVENANCE_TEST
    $env:LFTP_PILOT_SYNTHETIC_PROVENANCE_TEST = 'unit-fixture-only-v1'
    $record = & (Join-Path $buildRoot 'Test-BuildProvenance.ps1') -ArtifactPath $fixture -AllowSyntheticFixture
    if ($record.verificationMode -cne 'synthetic-fixture-only' -or $record.unsignedArtifact.sha256 -notmatch '^[a-f0-9]{64}$' -or
        $record.publicGoodAttestationRequired -ne $false -or $record.transparencyLogRequired -ne $false) {
        throw 'Synthetic diagnostic provenance did not produce bounded fixture evidence.'
    }
    $badFixture = Join-Path $fixtureRoot 'LFTPPilot.msix'; [IO.File]::WriteAllBytes($badFixture, [byte[]](1))
    Assert-Throws { & (Join-Path $buildRoot 'Test-BuildProvenance.ps1') -ArtifactPath $badFixture -AllowSyntheticFixture } 'Unbounded synthetic bypass'
}
finally {
    $env:LFTP_PILOT_SYNTHETIC_PROVENANCE_TEST = $previousBypass
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

$workflow = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\unsigned-package.yml') -Raw
if ($workflow -notmatch '(?m)^\s+id-token:\s+write\s*$' -or $workflow -notmatch '(?m)^\s+attestations:\s+write\s*$' -or
    $workflow -notmatch 'actions/attest@a1948c3f048ba23858d222213b7c278aabede763' -or
    $workflow -notmatch 'subject-path:\s+\$\{\{ runner\.temp \}\}\\unsigned\\LFTPPilot\.msix') {
    throw 'Unsigned-package workflow does not attest the exact staged MSIX with the pinned action and minimum provenance permissions.'
}
$provenanceScript = Get-Content -LiteralPath (Join-Path $buildRoot 'Test-BuildProvenance.ps1') -Raw
$releaseToolsScript = Get-Content -LiteralPath (Join-Path $buildRoot 'ReleaseTools.psm1') -Raw
foreach ($requiredPolicy in @('sourceRepositoryVisibilityAtSigning', "Where-Object type -CEQ 'Tlog'", 'publicGoodAttestationRequired', 'transparencyLogRequired')) {
    if (-not ($provenanceScript + $releaseToolsScript).Contains($requiredPolicy)) {
        throw "Build-provenance verification omits public-repository policy '$requiredPolicy'."
    }
}
if ($releaseToolsScript.Contains('2>&1') -or -not $releaseToolsScript.Contains('2> $standardErrorPath') -or
    -not $provenanceScript.Contains('Invoke-GitHubBuildProvenanceVerification')) {
    throw 'Build-provenance verification must keep native stderr separate from JSON stdout on Windows PowerShell.'
}
$signScript = Get-Content -LiteralPath (Join-Path $buildRoot 'Sign-Release.ps1') -Raw
$publishScript = Get-Content -LiteralPath (Join-Path $buildRoot 'Publish-Release.ps1') -Raw
foreach ($productionScript in @($signScript,$publishScript)) {
    if ($productionScript -match 'AllowSyntheticFixture|LFTP_PILOT_SYNTHETIC_PROVENANCE_TEST') {
        throw 'Production signing/publication exposes the synthetic provenance bypass.'
    }
    foreach ($gate in @('Test-BuildProvenance.ps1','Test-CertificateContinuity.ps1','Compare-LftpPilotSignedPayload')) {
        if (-not $productionScript.Contains($gate)) { throw "Production release script omits '$gate'." }
    }
}
if (-not $publishScript.Contains('Test-ImmutableReleases.ps1') -or -not $publishScript.Contains('Test-ReleaseTag.ps1') -or
    $publishScript -notmatch '\[Parameter\(Mandatory\)\]\[string\]\$AttestedUnsignedMsix' -or
    $publishScript -match "Join-Path \(Split-Path \`$PSScriptRoot -Parent\) 'tests'") {
    throw 'Publication does not enforce immutable releases, mandatory attested input, or production-only dependency scope.'
}
$tagScript = Get-Content -LiteralPath (Join-Path $buildRoot 'Test-ReleaseTag.ps1') -Raw
if ($tagScript -notmatch 'repos/nativepapaya/lftp-pilot/git/ref/tags/\$Tag' -or
    $tagScript -notmatch 'SourceDigest\.ToLowerInvariant') {
    throw 'Remote release tags are not bound to the exact attested source digest.'
}
$immutableScript = Get-Content -LiteralPath (Join-Path $buildRoot 'Test-ImmutableReleases.ps1') -Raw
if ($immutableScript -notmatch 'X-GitHub-Api-Version: 2026-03-10' -or
    $immutableScript -notmatch 'repos/nativepapaya/lftp-pilot/immutable-releases') {
    throw 'Immutable-release verification is not pinned to the reviewed repository/API contract.'
}

$sbomRoot = Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-sbom-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Directory]::CreateDirectory($sbomRoot) | Out-Null
    $artifact = Join-Path $sbomRoot 'fixture.msix'; [IO.File]::WriteAllBytes($artifact, [byte[]](1,2,3))
    $sbom = Join-Path $sbomRoot 'fixture.cdx.json'
    $locks = @(Get-ChildItem (Join-Path $repository 'src') -Recurse -Filter packages.lock.json -File |
        Where-Object FullName -NotMatch '\\(?:bin|obj)\\' | Select-Object -ExpandProperty FullName)
    $projectAssets = @(Get-ChildItem (Join-Path $repository 'src') -Recurse -Filter project.assets.json -File |
        Where-Object FullName -Match '\\obj\\project\.assets\.json$' | Select-Object -ExpandProperty FullName)
    if ($projectAssets.Count -ne $locks.Count) { throw 'Every production project must have restored project.assets.json evidence.' }
    & (Join-Path $buildRoot 'New-Sbom.ps1') -Version '1.0.0.42' `
        -RuntimeLockPath (Join-Path $buildRoot 'runtime-lock\lftp-msys2-x64.lock.json') `
        -NuGetLockPath $locks -ProjectAssetsPath $projectAssets -ArtifactPath $artifact -OutputPath $sbom | Out-Null
    $document = Get-Content -LiteralPath $sbom -Raw | ConvertFrom-Json
    $nuget = @($document.components | Where-Object { $_.PSObject.Properties['purl'] -and $_.purl -like 'pkg:nuget/*' })
    if ($nuget.Count -lt 1 -or @($nuget.purl | Sort-Object -Unique).Count -ne $nuget.Count -or
        @($nuget | Where-Object { $_.hashes.alg -ne 'SHA-512' -or $_.hashes.content -notmatch '^[a-f0-9]{128}$' }).Count -ne 0) {
        throw 'SBOM NuGet components are missing, duplicated, or not anchored to locked SHA-512 hashes.'
    }
    $appLock = Get-Content -LiteralPath (Join-Path $repository 'src\LFTPPilot.App\packages.lock.json') -Raw | ConvertFrom-Json
    $expectedBuildToolsVersions = @($appLock.dependencies.PSObject.Properties.Value |
        ForEach-Object { $_.PSObject.Properties['Microsoft.Windows.SDK.BuildTools'] } |
        Where-Object { $null -ne $_ } |
        ForEach-Object { $_.Value.resolved } |
        Sort-Object -Unique)
    if ($expectedBuildToolsVersions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($expectedBuildToolsVersions[0])) {
        throw 'The application lock must select exactly one Windows SDK BuildTools version.'
    }
    $buildTools = @($nuget | Where-Object name -eq 'Microsoft.Windows.SDK.BuildTools')
    if ($buildTools.Count -ne 1 -or $buildTools[0].version -ne $expectedBuildToolsVersions[0]) {
        throw 'SBOM contains split or stale Windows SDK BuildTools metadata.'
    }
    if (@($nuget | Where-Object { $_.name -match '^(?:xunit|Microsoft\.NET\.Test\.Sdk)' }).Count -ne 0) {
        throw 'SBOM contains test-only packages.'
    }
    $runtimePack = @($nuget | Where-Object name -eq 'Microsoft.NETCore.App.Runtime.win-x64')
    if ($runtimePack.Count -ne 1 -or $runtimePack[0].version -ne '10.0.10' -or
        $runtimePack[0].hashes.content -cne '77fea6d9bc0d3f7e7f6c10e6b4d3dbd0604bc252b47b233cfd06fbcc4b481490e08c0a3deb46d1394941e9772e2cce1599f03c86494e8060d4f7c9d483ac43c0') {
        throw 'SBOM omits or misstates the selected self-contained .NET runtime pack.'
    }
}
finally { if (Test-Path -LiteralPath $sbomRoot) { Remove-Item -LiteralPath $sbomRoot -Recurse -Force } }
'Release tooling tests passed.'
